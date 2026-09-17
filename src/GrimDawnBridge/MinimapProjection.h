#pragma once

// Read-only observer of the supported executable's minimap render. Project
// three local reference points through the same camera/viewport as native POIs.
namespace MinimapProjection
{
    using RenderFn = void(__fastcall*)(void*, void*, const float*);
    using ProjectFn = float*(__fastcall*)(const void*, float*, const float*, const void*);
    using WorldCtorFn = void*(__fastcall*)(void*, void*, const float*);
    using WorldPositionFn = float*(__fastcall*)(const void*, float*);
    inline RenderFn originalRender = nullptr;
    inline ProjectFn project = nullptr;
    inline WorldCtorFn worldCtor = nullptr;
    inline WorldPositionFn worldPosition = nullptr;
    inline unsigned char* executable = nullptr;
    inline bool available = false;
    inline thread_local void* currentMap = nullptr;
    inline thread_local const float* currentOffset = nullptr;
    inline void (*onRendered)(void*) = nullptr;

    template<typename T> T Field(const void* object, size_t offset)
    { return *reinterpret_cast<const T*>(static_cast<const unsigned char*>(object) + offset); }

    inline void __fastcall RenderHook(void* map, void* canvas, const float* offset)
    {
        const auto previousMap = currentMap;
        const auto previousOffset = currentOffset;
        currentMap = map; currentOffset = offset;
        originalRender(map, canvas, offset);
        if (onRendered) onRendered(canvas);
        currentMap = previousMap; currentOffset = previousOffset;
    }

    inline bool Fingerprint(size_t rva, size_t length, unsigned long long expected)
    {
        unsigned long long hash = 14695981039346656037ULL;
        for (size_t i = 0; i < length; ++i) hash = (hash ^ executable[rva + i]) * 1099511628211ULL;
        return hash == expected;
    }

    inline void Initialize(HMODULE engine)
    {
        executable = reinterpret_cast<unsigned char*>(GetModuleHandleW(nullptr));
        project = reinterpret_cast<ProjectFn>(GetProcAddress(engine, "?Project@Camera@GAME@@QEBA?AVVec2@2@AEBVVec3@2@AEBVViewport@2@@Z"));
        worldCtor = reinterpret_cast<WorldCtorFn>(GetProcAddress(engine, "??0WorldVec3@GAME@@QEAA@PEAVRegion@1@AEBVVec3@1@@Z"));
        worldPosition = reinterpret_cast<WorldPositionFn>(GetProcAddress(engine, "?GetWorldPosition@WorldVec3@GAME@@QEBA?AVVec3@2@XZ"));
        bool supported = false;
        __try
        {
            const auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(executable);
            const auto pe = reinterpret_cast<IMAGE_NT_HEADERS64*>(executable + dos->e_lfanew);
            supported = project && worldCtor && worldPosition && dos->e_magic == IMAGE_DOS_SIGNATURE &&
                pe->Signature == IMAGE_NT_SIGNATURE && pe->FileHeader.TimeDateStamp == 0x6A85FBEC &&
                pe->OptionalHeader.SizeOfImage == 0x482000 &&
                Fingerprint(0x176830, 0x410, 0x4CD9847DCA67E0CEULL) &&
                Fingerprint(0x175B90, 0x470, 0x5E3EA46E4EE07C7DULL);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { supported = false; }
        if (!supported) return;
        auto target = executable + 0x176830;
        if (MH_CreateHook(target, reinterpret_cast<void*>(&RenderHook), reinterpret_cast<void**>(&originalRender)) != MH_OK) return;
        if (MH_EnableHook(target) == MH_OK) available = true;
        else MH_RemoveHook(target);
    }

    // origin world X/Z, origin circle ratios X/Y, X basis X/Y, Z basis X/Y.
    inline bool Sample(float left, float top, float diameter, float canvasX, float canvasY, std::array<float, 8>& result)
    {
        if (!available || !currentMap || !currentOffset) return false;
        __try
        {
            const auto map = static_cast<const unsigned char*>(currentMap);
            if (Field<void*>(map, 0) != executable + 0x3159A0 || !Field<unsigned char>(map, 0x161) ||
                Field<int>(map, 0x38) == 2) return false; // The native render skips mode 2 (closed).
            auto region = Field<void*>(map, 0x1C0);
            auto camera = map + 0x170;
            auto viewport = map + 0x1C8;
            if (!region || Field<int>(camera, 0) != 1) return false; // Orthographic only.
            float origin[3] = { Field<float>(camera, 0x28), 0, Field<float>(camera, 0x30) };
            float xPoint[3] = { origin[0] + 16, 0, origin[2] };
            float zPoint[3] = { origin[0], 0, origin[2] + 16 };
            float p[2]{}, x[2]{}, z[2]{}, world[3]{};
            alignas(16) unsigned char worldVector[32]{};
            worldCtor(worldVector, region, origin);
            worldPosition(worldVector, world);
            project(camera, p, origin, viewport);
            project(camera, x, xPoint, viewport);
            project(camera, z, zPoint, viewport);
            const float radius = diameter * .5f;
            result = { world[0], world[2],
                (p[0] + Field<float>(map, 0xAC4) + currentOffset[0] + canvasX - left - radius) / radius,
                (p[1] + Field<float>(map, 0xAC8) + currentOffset[1] + canvasY - top - radius) / radius,
                (x[0] - p[0]) / (16 * radius), (x[1] - p[1]) / (16 * radius),
                (z[0] - p[0]) / (16 * radius), (z[1] - p[1]) / (16 * radius) };
            for (auto value : result) if (!std::isfinite(value)) return false;
            const float xs = hypotf(result[4], result[5]), zs = hypotf(result[6], result[7]);
            const bool valid = xs >= .001f && xs <= 1 && zs >= .001f && zs <= 1 &&
                fabsf(xs - zs) < xs * .02f && fabsf(result[4] * result[6] + result[5] * result[7]) < xs * zs * .02f &&
                fabsf(result[2]) < 2 && fabsf(result[3]) < 2;
            return valid;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }
}
