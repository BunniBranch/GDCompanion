#include <windows.h>
#include <MinHook.h>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>

// Dedicated 32-bit bridge for the current Steam x86 game image.  This file is
// intentionally small: x64 object offsets, vtables and C++ container layouts
// must never be reused in the 32-bit process.  Catalog browsing and ordinary
// item spawning use the game's exported Lua manager and are independently
// audited here; unsupported utilities simply are not advertised.
namespace
{
    constexpr char kUpdateExport[] = "?Update@LuaManager@GAME@@QAEXH@Z";
    constexpr char kRunCodeExport[] = "?RunCode@LuaManager@GAME@@QAE_NPBD@Z";
    constexpr DWORD kExecutableTimestamp = 0x6AC7F41E;
    constexpr DWORD kExecutableSize = 0x3AC000;
    constexpr DWORD kEngineTimestamp = 0x6AC7F397;
    constexpr DWORD kEngineSize = 0x389000;
    constexpr uintptr_t kUpdateRva = 0xD93B0;
    constexpr uintptr_t kRunCodeRva = 0xD8F40;
    constexpr size_t kCommandSize = 4096;
    constexpr size_t kResponseSize = 256;
    constexpr size_t kQueueSlots = 64;

    using UpdateFunction = void(__thiscall*)(void*, int);
    using RunCodeFunction = bool(__thiscall*)(void*, const char*);

    struct WorkRequest
    {
        std::array<char, kCommandSize> payload{};
        std::array<char, kResponseSize> response{};
        std::mutex mutex;
        std::condition_variable completedCondition;
        unsigned long long deadline = 0;
        bool completed = false;
    };

    HMODULE g_bridgeModule = nullptr;
    void* g_updateTarget = nullptr;
    UpdateFunction g_originalUpdate = nullptr;
    RunCodeFunction g_runCode = nullptr;
    CRITICAL_SECTION g_queueLock{};
    std::array<std::shared_ptr<WorkRequest>, kQueueSlots> g_queue{};
    size_t g_readIndex = 0, g_writeIndex = 0, g_queueCount = 0;
    std::atomic<bool> g_ready{false};
    std::atomic<bool> g_shutdownRequested{false};
    std::atomic<DWORD> g_error{0};
    bool g_minHookInitialized = false;
    bool g_hookEnabled = false;

    bool SupportedImage(HMODULE module, DWORD timestamp, DWORD imageSize)
    {
        if (!module) return false;
        const auto bytes = reinterpret_cast<const unsigned char*>(module);
        const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(bytes);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
        const auto pe = reinterpret_cast<const IMAGE_NT_HEADERS32*>(bytes + dos->e_lfanew);
        return pe->Signature == IMAGE_NT_SIGNATURE && pe->OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR32_MAGIC &&
            pe->FileHeader.TimeDateStamp == timestamp && pe->OptionalHeader.SizeOfImage == imageSize;
    }

    HMODULE WaitForModule(const wchar_t* name)
    {
        for (int i = 0; i < 300; ++i)
        {
            if (auto module = GetModuleHandleW(name)) return module;
            Sleep(100);
        }
        return nullptr;
    }

    void Complete(const std::shared_ptr<WorkRequest>& request, const char* response)
    {
        std::lock_guard lock(request->mutex);
        strcpy_s(request->response.data(), request->response.size(), response);
        request->completed = true;
        request->completedCondition.notify_one();
    }

    bool Enqueue(const std::shared_ptr<WorkRequest>& request)
    {
        EnterCriticalSection(&g_queueLock);
        if (g_queueCount >= g_queue.size())
        {
            LeaveCriticalSection(&g_queueLock);
            return false;
        }
        g_queue[g_writeIndex] = request;
        g_writeIndex = (g_writeIndex + 1) % g_queue.size();
        ++g_queueCount;
        LeaveCriticalSection(&g_queueLock);
        return true;
    }

    bool Dequeue(std::shared_ptr<WorkRequest>& request)
    {
        EnterCriticalSection(&g_queueLock);
        if (!g_queueCount)
        {
            LeaveCriticalSection(&g_queueLock);
            return false;
        }
        request = std::move(g_queue[g_readIndex]);
        g_readIndex = (g_readIndex + 1) % g_queue.size();
        --g_queueCount;
        LeaveCriticalSection(&g_queueLock);
        return true;
    }

    void __fastcall HookedUpdate(void* manager, void*, int argument)
    {
        g_originalUpdate(manager, argument);
        const bool initialized = manager && *static_cast<void**>(manager);
        std::shared_ptr<WorkRequest> request;
        for (int i = 0; i < 4 && Dequeue(request); ++i)
        {
            if (GetTickCount64() > request->deadline)
                Complete(request, "ERROR LUA_MANAGER_NOT_READY");
            else if (!initialized)
            {
                Enqueue(request);
                break;
            }
            else Complete(request, g_runCode && g_runCode(manager, request->payload.data()) ? "OK LUA" : "ERROR LUA_EXECUTION_FAILED");
        }
    }

    void Reply(HANDLE pipe, const char* value)
    {
        char line[kResponseSize + 2]{};
        sprintf_s(line, "%s\n", value);
        DWORD written = 0;
        WriteFile(pipe, line, static_cast<DWORD>(strlen(line)), &written, nullptr);
    }

    void SubmitAndWait(HANDLE pipe, const std::shared_ptr<WorkRequest>& request)
    {
        if (!Enqueue(request)) { Reply(pipe, "ERROR QUEUE_FULL"); return; }
        std::unique_lock lock(request->mutex);
        if (!request->completedCondition.wait_for(lock, std::chrono::seconds(8), [&] { return request->completed; }))
            Reply(pipe, "ERROR GAME_THREAD_TIMEOUT");
        else Reply(pipe, request->response.data());
    }

    void ServeLine(HANDLE pipe, char* line)
    {
        if (!strcmp(line, "PING"))
        {
            if (!g_ready)
            {
                char response[64]{};
                sprintf_s(response, "ERROR INIT %lu", g_error.load());
                Reply(pipe, response);
            }
            else Reply(pipe, "PONG 38 READY CAPS=LUA_SYNC,UNLOAD,X86");
            return;
        }
        if (!g_ready) { Reply(pipe, "ERROR NOT_READY"); return; }
        if (!strcmp(line, "SHUTDOWN"))
        {
            Reply(pipe, "OK UNLOADING");
            g_ready = false;
            g_shutdownRequested = true;
            return;
        }
        const bool synchronous = !strncmp(line, "LUA_SYNC\t", 9);
        const bool asynchronous = !strncmp(line, "LUA\t", 4);
        if (synchronous || asynchronous)
        {
            const char* script = line + (synchronous ? 9 : 4);
            if (strlen(script) >= kCommandSize) { Reply(pipe, "ERROR COMMAND_TOO_LONG"); return; }
            auto request = std::make_shared<WorkRequest>();
            strcpy_s(request->payload.data(), request->payload.size(), script);
            request->deadline = GetTickCount64() + (synchronous ? 7500 : 2000);
            if (synchronous) SubmitAndWait(pipe, request);
            else Reply(pipe, Enqueue(request) ? "QUEUED" : "ERROR QUEUE_FULL_OR_COMMAND_TOO_LONG");
            return;
        }
        Reply(pipe, "ERROR UNKNOWN_COMMAND");
    }

    void ServeClient(HANDLE pipe)
    {
        std::array<char, kCommandSize + 16> buffer{};
        DWORD received = 0;
        while (!g_shutdownRequested && ReadFile(pipe, buffer.data(), static_cast<DWORD>(buffer.size() - 1), &received, nullptr) && received)
        {
            buffer[received] = '\0';
            char* context = nullptr;
            for (char* line = strtok_s(buffer.data(), "\r\n", &context); line && !g_shutdownRequested; line = strtok_s(nullptr, "\r\n", &context))
                ServeLine(pipe, line);
            buffer.fill(0);
        }
    }

    DWORD WINAPI PipeThread(void*)
    {
        wchar_t name[128]{};
        swprintf_s(name, L"\\\\.\\pipe\\GrimDawnCompanion.%lu", GetCurrentProcessId());
        while (!g_shutdownRequested)
        {
            HANDLE pipe = CreateNamedPipeW(name, PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT |
                PIPE_REJECT_REMOTE_CLIENTS, 1, 8192, 8192, 0, nullptr);
            if (pipe == INVALID_HANDLE_VALUE) return GetLastError();
            if (ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED) ServeClient(pipe);
            FlushFileBuffers(pipe);
            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
        }
        if (g_hookEnabled) MH_DisableHook(g_updateTarget);
        if (g_updateTarget) MH_RemoveHook(g_updateTarget);
        if (g_minHookInitialized) MH_Uninitialize();
        DeleteCriticalSection(&g_queueLock);
        FreeLibraryAndExitThread(g_bridgeModule, 0);
        return 0;
    }

    DWORD WINAPI InitializeBridge(void*)
    {
        InitializeCriticalSection(&g_queueLock);
        const auto executable = GetModuleHandleW(nullptr);
        const auto engine = WaitForModule(L"Engine.dll");
        if (!SupportedImage(executable, kExecutableTimestamp, kExecutableSize) ||
            !SupportedImage(engine, kEngineTimestamp, kEngineSize)) g_error = 1;
        else
        {
            g_updateTarget = reinterpret_cast<void*>(GetProcAddress(engine, kUpdateExport));
            g_runCode = reinterpret_cast<RunCodeFunction>(GetProcAddress(engine, kRunCodeExport));
            const auto base = reinterpret_cast<uintptr_t>(engine);
            if (!g_updateTarget || !g_runCode || reinterpret_cast<uintptr_t>(g_updateTarget) - base != kUpdateRva ||
                reinterpret_cast<uintptr_t>(g_runCode) - base != kRunCodeRva) g_error = 2;
            else if (MH_Initialize() != MH_OK) g_error = 3;
            else
            {
                g_minHookInitialized = true;
                if (MH_CreateHook(g_updateTarget, reinterpret_cast<void*>(&HookedUpdate), reinterpret_cast<void**>(&g_originalUpdate)) != MH_OK) g_error = 4;
                else if (MH_EnableHook(g_updateTarget) != MH_OK) g_error = 5;
                else { g_hookEnabled = true; g_ready = true; }
            }
        }
        HANDLE thread = CreateThread(nullptr, 0, PipeThread, nullptr, 0, nullptr);
        if (thread) CloseHandle(thread);
        return g_error.load();
    }
}

extern "C" __declspec(dllexport) unsigned int GrimDawnCompanionBridgeVersion() { return 38; }

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_bridgeModule = module;
        DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(nullptr, 0, InitializeBridge, nullptr, 0, nullptr);
        if (thread) CloseHandle(thread);
    }
    return TRUE;
}
