#pragma once
#include "RadarCanvasState.h"
#include <algorithm>

namespace RadarCanvas
{
    using FanFn = void(__fastcall*)(void*, const void*, const float*);
    using RectFn = void(__fastcall*)(void*, const float*, const float*);
    using TextFn = void(__fastcall*)(void*, int, int, const std::wstring&, const std::string&, const float*, int);
    inline FanFn fan = nullptr;
    inline RectFn rectangle = nullptr;
    inline TextFn text = nullptr;
    inline bool available = false;
    inline SRWLOCK stateLock = SRWLOCK_INIT;
    inline State state;
    inline std::atomic<unsigned long long> lastDraw{0};
    inline std::atomic<unsigned int> drawnCount{0};

    inline void Clear()
    {
        AcquireSRWLockExclusive(&stateLock); state.Clear(); ReleaseSRWLockExclusive(&stateLock);
        lastDraw = 0; drawnCount = 0;
    }
    inline bool Command(const char* command)
    {
        AcquireSRWLockExclusive(&stateLock);
        const bool accepted = state.Command(command, GetTickCount64());
        ReleaseSRWLockExclusive(&stateLock);
        return accepted;
    }
    inline void Glyph(void* canvas, float x, float y, float radius, bool star, const float* color)
    {
        struct Point { float x, y; };
        std::array<Point, 14> vertices{};
        const size_t count = star ? 10 : 12;
        vertices[0] = {x, y};
        for (size_t i = 0; i <= count; ++i)
        {
            const auto angle = -1.570796327f + static_cast<float>(i % count) * 6.283185307f / static_cast<float>(count);
            const auto r = star && i % 2 ? radius * .43f : radius;
            vertices[i + 1] = { x + cosf(angle) * r, y + sinf(angle) * r };
        }
        const std::array<const Point*, 3> view {vertices.data(), vertices.data() + count + 2, vertices.data() + count + 2};
        fan(canvas, &view, color);
    }
    inline void Draw(void* canvas, const std::array<float, 6>& bounds, const std::array<float, 8>& transform,
        unsigned long long context)
    {
        if (!available || !canvas) return;
        DWORD foregroundPid = 0;
        const auto window = GetForegroundWindow();
        GetWindowThreadProcessId(window, &foregroundPid);
        if (foregroundPid != GetCurrentProcessId() || IsIconic(window)) return;
        Frame frame;
        AcquireSRWLockShared(&stateLock); frame = state.active; ReleaseSRWLockShared(&stateLock);
        const auto now = GetTickCount64();
        if (!Fresh(frame, context, now)) return;
        const auto canvasBytes = static_cast<const unsigned char*>(canvas);
        const float offsetX = *reinterpret_cast<const float*>(canvasBytes + MinimapTelemetry::canvasOffsetX);
        const float offsetY = *reinterpret_cast<const float*>(canvasBytes + MinimapTelemetry::canvasOffsetY);
        const float centerX = bounds[0] + bounds[2] * .5f, centerY = bounds[1] + bounds[3] * .5f;
        const float scale = std::clamp(bounds[2] / 230.f, .75f, 2.25f);
        constexpr float outline[]{ .03f, .067f, .114f, 1.f };
        constexpr float colors[][4]{ {1.f,.82f,.35f,1.f}, {.20f,.88f,.79f,1.f}, {.67f,.50f,1.f,1.f},
            {.30f,.67f,1.f,1.f}, {1.f,.60f,.32f,1.f}, {.95f,.77f,.40f,1.f}, {.8f,.86f,.94f,1.f} };
        POINT cursor{}; RECT client{};
        const bool haveCursor = GetCursorPos(&cursor) && ScreenToClient(window, &cursor) &&
            GetClientRect(window, &client) && client.right > 0 && client.bottom > 0;
        const float mouseX = haveCursor ? cursor.x * bounds[4] / client.right : -10000.f;
        const float mouseY = haveCursor ? cursor.y * bounds[5] / client.bottom : -10000.f;
        const Marker* hovered = nullptr;
        float nearest = 100000.f;
        unsigned count = 0;
        for (const auto& marker : frame.markers)
        {
            const auto point = Project(marker, transform);
            if (!std::isfinite(point[0]) || !std::isfinite(point[1])) continue;
            const float x = centerX + point[0] * bounds[2] * .5f, y = centerY + point[1] * bounds[3] * .5f;
            const bool star = marker.kind == 0;
            const float radius = (star ? 6.5f : 4.f) * scale;
            // RenderTriFan consumes viewport pixel positions directly, unlike
            // RenderRect/Text, which add the canvas translation themselves.
            Glyph(canvas, x, y, radius + scale, star, outline);
            Glyph(canvas, x, y, radius, star, colors[marker.kind]);
            ++count;
            const float distance = hypotf(mouseX - x, mouseY - y);
            if (distance < std::max(10.f, radius * 1.5f) && distance < nearest) { hovered = &marker; nearest = distance; }
        }
        if (hovered && text && rectangle)
        {
            // Use an installed UI text style through the public canvas API;
            // no borrowed font/texture/device pointers survive a render call.
            std::array<wchar_t, 256> label{};
            if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, hovered->label.c_str(), -1, label.data(), static_cast<int>(label.size())) > 0)
            {
                const float width = std::min(460.f, bounds[4] - 16.f);
                const float left = std::clamp(mouseX - width, 8.f, bounds[4] - width - 8.f);
                const float top = std::clamp(mouseY + 18.f, 8.f, bounds[5] - 46.f);
                const float box[]{left - offsetX, top - offsetY, width, 34.f};
                rectangle(canvas, box, outline);
                const std::string style("records/ui/styles/text/style_textwhite_sizes.dbr");
                std::wstring title(label.data());
                if (title.size() > 48)
                {
                    size_t end = 47;
                    if (title[end - 1] >= 0xD800 && title[end - 1] <= 0xDBFF) --end;
                    title.resize(end); title += L'\x2026';
                }
                text(canvas, static_cast<int>(left - offsetX + 8), static_cast<int>(top - offsetY + 6), title, style, colors[6], 0);
            }
        }
        drawnCount = count; lastDraw = now;
    }
    inline void Initialize(HMODULE engine)
    {
        const auto base = reinterpret_cast<const unsigned char*>(engine);
        const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        const auto pe = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
        if (!MinimapProjection::available || pe->FileHeader.TimeDateStamp != 0x6A85FB5B || pe->OptionalHeader.SizeOfImage != 0x450000) return;
        fan = reinterpret_cast<FanFn>(GetProcAddress(engine, "?RenderTriFan@GraphicsCanvas@GAME@@QEAAXAEBV?$vector@VVec2@GAME@@@mem@@AEBVColor@2@@Z"));
        rectangle = reinterpret_cast<RectFn>(GetProcAddress(engine, "?RenderRect@GraphicsCanvas@GAME@@QEAAXAEBVRect@2@AEBVColor@2@@Z"));
        text = reinterpret_cast<TextFn>(GetProcAddress(engine, "?RenderColoredText2d@GraphicsCanvas@GAME@@QEAAXHHAEBV?$basic_string@GU?$char_traits@G@std@@V?$allocator@G@2@@std@@AEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@4@AEBVColor@2@W4FontLayout@2@@Z"));
        available = fan && rectangle && text;
        if (available) MinimapTelemetry::onFrame.store(&Draw, std::memory_order_release);
    }
}
