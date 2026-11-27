#pragma once
#include "SupportedBuilds.h"
#include "RadarCanvasState.h"
#include <algorithm>
#include <memory>

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
    inline std::shared_ptr<const Frame> activeSnapshot;
    inline std::atomic<unsigned long long> lastDraw{0};
    inline std::atomic<unsigned int> drawnCount{0};
    inline constexpr float kOutline[]{ .03f, .067f, .114f, 1.f };
    inline constexpr float kColors[][4]{ {1.f,.82f,.35f,1.f}, {.20f,.88f,.79f,1.f}, {.67f,.50f,1.f,1.f},
        {.30f,.67f,1.f,1.f}, {1.f,.60f,.32f,1.f}, {.95f,.77f,.40f,1.f}, {.8f,.86f,.94f,1.f}, {.35f,.92f,.42f,1.f} };
    static_assert(std::size(kColors) == MaxMarkerKind + 1);

    inline void Clear()
    {
        AcquireSRWLockExclusive(&stateLock);
        state.Clear();
        activeSnapshot.reset();
        ReleaseSRWLockExclusive(&stateLock);
        lastDraw = 0;
        drawnCount = 0;
    }
    inline bool Command(const char* command)
    {
        AcquireSRWLockExclusive(&stateLock);
        const bool accepted = state.Command(command, GetTickCount64());
        if (accepted)
        {
            if (strcmp(command, "COMMIT") == 0)
                activeSnapshot = std::make_shared<const Frame>(state.active);
            else if (strcmp(command, "CLEAR") == 0)
                activeSnapshot.reset();
        }
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
    inline void DrawTooltip(void* canvas, float mouseX, float mouseY, float viewWidth, float viewHeight,
        const Marker& marker, float offsetX, float offsetY)
    {
        if (!text || !rectangle) return;
        // Use an installed UI text style through the public canvas API;
        // no borrowed font/texture/device pointers survive a render call.
        std::array<wchar_t, 256> label{};
        if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, marker.label.c_str(), -1, label.data(),
            static_cast<int>(label.size())) <= 0) return;
        const float boxWidth = std::min(460.f, viewWidth - 16.f);
        const float left = std::clamp(mouseX - boxWidth, 8.f, viewWidth - boxWidth - 8.f);
        const float top = std::clamp(mouseY + 18.f, 8.f, viewHeight - 46.f);
        const float box[]{ left - offsetX, top - offsetY, boxWidth, 34.f };
        rectangle(canvas, box, kOutline);
        const std::string style("records/ui/styles/text/style_textwhite_sizes.dbr");
        std::wstring title(label.data());
        if (title.size() > 48)
        {
            size_t end = 47;
            if (title[end - 1] >= 0xD800 && title[end - 1] <= 0xDBFF) --end;
            title.resize(end); title += L'\x2026';
        }
        text(canvas, static_cast<int>(left - offsetX + 8), static_cast<int>(top - offsetY + 6), title, style, kColors[6], 0);
    }
    inline void DrawSurface(void* canvas, bool fullMap, const std::array<float, 6>& bounds,
        const std::array<float, 8>& transform, float viewWidth, float viewHeight, float scale,
        unsigned long long context)
    {
        if (!available || !canvas) return;
        DWORD foregroundPid = 0;
        const auto window = GetForegroundWindow();
        GetWindowThreadProcessId(window, &foregroundPid);
        if (foregroundPid != GetCurrentProcessId() || IsIconic(window)) return;
        std::shared_ptr<const Frame> frame;
        AcquireSRWLockShared(&stateLock); frame = activeSnapshot; ReleaseSRWLockShared(&stateLock);
        const auto now = GetTickCount64();
        if (!frame || !Fresh(*frame, context, now)) return;
        const auto canvasBytes = static_cast<const unsigned char*>(canvas);
        const float offsetX = *reinterpret_cast<const float*>(canvasBytes + MinimapTelemetry::canvasOffsetX);
        const float offsetY = *reinterpret_cast<const float*>(canvasBytes + MinimapTelemetry::canvasOffsetY);
        const float halfW = bounds[2] * .5f, halfH = bounds[3] * .5f;
        const float centerX = bounds[0] + halfW, centerY = bounds[1] + halfH;
        POINT cursor{}; RECT client{};
        const bool haveCursor = GetCursorPos(&cursor) && ScreenToClient(window, &cursor) &&
            GetClientRect(window, &client) && client.right > 0 && client.bottom > 0;
        const float mouseX = haveCursor ? cursor.x * viewWidth / client.right : -10000.f;
        const float mouseY = haveCursor ? cursor.y * viewHeight / client.bottom : -10000.f;
        const Marker* hovered = nullptr;
        float nearest = 100000.f;
        unsigned count = 0;
        for (const auto& marker : frame->markers)
        {
            const auto point = fullMap ? ProjectRaw(marker, transform) : Project(marker, transform);
            if (!std::isfinite(point[0]) || !std::isfinite(point[1])) continue;
            const float x = fullMap ? point[0] : centerX + point[0] * halfW;
            const float y = fullMap ? point[1] : centerY + point[1] * halfH;
            if (fullMap && (x < 0 || y < 0 || x > viewWidth || y > viewHeight)) continue;
            const bool star = marker.kind == 0;
            const float radius = (star ? 6.5f : 4.f) * scale;
            // RenderTriFan consumes viewport pixel positions directly, unlike
            // RenderRect/Text, which add the canvas translation themselves.
            Glyph(canvas, x, y, radius + scale, star, kOutline);
            Glyph(canvas, x, y, radius, star, kColors[marker.kind]);
            ++count;
            const float distance = hypotf(mouseX - x, mouseY - y);
            if (distance < std::max(10.f, radius * 1.5f) && distance < nearest) { hovered = &marker; nearest = distance; }
        }
        if (hovered) DrawTooltip(canvas, mouseX, mouseY, viewWidth, viewHeight, *hovered, offsetX, offsetY);
        drawnCount = count; lastDraw = now;
    }
    inline void Draw(void* canvas, const std::array<float, 6>& bounds, const std::array<float, 8>& transform,
        unsigned long long context)
    {
        DrawSurface(canvas, false, bounds, transform, bounds[4], bounds[5],
            std::clamp(bounds[2] / 230.f, .75f, 2.25f), context);
    }
    inline void DrawMap(void* canvas, const std::array<float, 8>& transform, unsigned long long context)
    {
        auto graphics = MinimapTelemetry::engineInstance && *MinimapTelemetry::engineInstance
            ? MinimapTelemetry::getGraphics(*MinimapTelemetry::engineInstance) : nullptr;
        if (!graphics) return;
        const int width = MinimapTelemetry::getWidth(graphics), height = MinimapTelemetry::getHeight(graphics);
        if (width < 640 || height < 480) return;
        DrawSurface(canvas, true, {}, transform, static_cast<float>(width), static_cast<float>(height),
            std::clamp(height / 1080.f, .75f, 2.f), context);
    }
    inline void Initialize(HMODULE engine)
    {
        if (!MinimapProjection::available || SupportedBuilds::Engine(engine) == SupportedBuilds::Build::Unknown) return;
        fan = reinterpret_cast<FanFn>(GetProcAddress(engine, "?RenderTriFan@GraphicsCanvas@GAME@@QEAAXAEBV?$vector@VVec2@GAME@@@mem@@AEBVColor@2@@Z"));
        rectangle = reinterpret_cast<RectFn>(GetProcAddress(engine, "?RenderRect@GraphicsCanvas@GAME@@QEAAXAEBVRect@2@AEBVColor@2@@Z"));
        text = reinterpret_cast<TextFn>(GetProcAddress(engine, "?RenderColoredText2d@GraphicsCanvas@GAME@@QEAAXHHAEBV?$basic_string@GU?$char_traits@G@std@@V?$allocator@G@2@@std@@AEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@4@AEBVColor@2@W4FontLayout@2@@Z"));
        available = fan && rectangle && text;
        if (available)
        {
            MinimapTelemetry::onFrame.store(&Draw, std::memory_order_release);
            MinimapTelemetry::onMapFrame.store(&DrawMap, std::memory_order_release);
        }
    }
}
