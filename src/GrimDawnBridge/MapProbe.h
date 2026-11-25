#pragma once
#include "MinimapProjection.h"

// Diagnostic only. Draws a reference target at a supplied world position during
// the audited map render and records what that render actually saw. It exists
// to establish, with one live run, whether Grim Dawn's full-screen map shares
// the minimap render path and transform before a map marker lane is enabled.
namespace MapProbe
{
    using FanFn = void(__fastcall*)(void*, const void*, const float*);
    struct Reading
    {
        int mode = -1, enabled = -1, orthographic = -1;
        std::array<float, 4> viewport{};
        float originX = 0, originZ = 0;
        float screenX = 0, screenY = 0;
        bool projected = false, vtable = false, region = false;
    };
    struct Snapshot
    {
        unsigned long long frames = 0, rendered = 0, renderedAt = 0, lastFrameAt = 0;
        int mode = -1, enabled = -1, orthographic = -1;
        std::array<float, 4> viewport{};
        float originX = 0, originZ = 0;
        float screenX = 0, screenY = 0;
        bool projected = false, vtable = false, region = false, boundsFresh = false;
    };
    inline SRWLOCK lock = SRWLOCK_INIT;
    inline Snapshot snapshot{};
    inline std::atomic<bool> armed{false};
    inline std::atomic<bool> available{false};
    inline float targetX = 0, targetZ = 0;
    inline FanFn fan = nullptr;

    inline void Arm(float worldX, float worldZ)
    {
        AcquireSRWLockExclusive(&lock);
        targetX = worldX; targetZ = worldZ;
        snapshot.rendered = 0; snapshot.renderedAt = 0;
        ReleaseSRWLockExclusive(&lock);
        armed = true;
    }
    inline void Disarm() { armed = false; }

    // Same absolute screen formula as MinimapProjection::Sample, but without
    // the circle normalization: the canvas fan draws in viewport pixels.
    inline bool Read(Reading& reading, const void* canvas, unsigned canvasOffsetX, unsigned canvasOffsetY,
        float worldX, float worldZ)
    {
        const auto map = MinimapProjection::currentMap;
        const auto offset = MinimapProjection::currentOffset;
        if (!map || !offset || !canvas) return false;
        __try
        {
            const auto bytes = static_cast<const unsigned char*>(map);
            reading.mode = *reinterpret_cast<const int*>(bytes + 0x38);
            reading.enabled = *reinterpret_cast<const unsigned char*>(bytes + 0x161);
            reading.orthographic = *reinterpret_cast<const int*>(bytes + 0x170);
            reading.vtable = *reinterpret_cast<void* const*>(bytes) ==
                MinimapProjection::executable + MinimapProjection::mapVtableRva;
            reading.originX = *reinterpret_cast<const float*>(bytes + 0x170 + 0x28);
            reading.originZ = *reinterpret_cast<const float*>(bytes + 0x170 + 0x30);
            for (size_t index = 0; index < reading.viewport.size(); ++index)
                reading.viewport[index] = *reinterpret_cast<const float*>(bytes + 0x1C8 + index * 4);
            // Only call the game's projector on the audited object and camera.
            if (!reading.vtable || reading.orthographic != 1) return false;
            // The camera projects region-local coordinates, while the player and
            // catalog use global ones. Remove the region origin like the game's
            // own world-to-local conversion does.
            const auto region = *reinterpret_cast<const unsigned char* const*>(bytes + 0x1C0);
            reading.region = region != nullptr;
            if (!region) return false;
            int offsetX = 0, offsetY = 0, offsetZ = 0;
            memcpy(&offsetX, region + 0x3c, sizeof(offsetX));
            memcpy(&offsetY, region + 0x40, sizeof(offsetY));
            memcpy(&offsetZ, region + 0x44, sizeof(offsetZ));
            const float target[3]{ worldX - static_cast<float>(offsetX), 0, worldZ - static_cast<float>(offsetZ) };
            float projected[2]{};
            MinimapProjection::project(bytes + 0x170, projected, target, bytes + 0x1C8);
            const auto canvasBytes = static_cast<const unsigned char*>(canvas);
            const float translationX = *reinterpret_cast<const float*>(canvasBytes + canvasOffsetX);
            const float translationY = *reinterpret_cast<const float*>(canvasBytes + canvasOffsetY);
            reading.screenX = projected[0] + *reinterpret_cast<const float*>(bytes + 0xAC4) + offset[0] + translationX;
            reading.screenY = projected[1] + *reinterpret_cast<const float*>(bytes + 0xAC8) + offset[1] + translationY;
            reading.projected = std::isfinite(reading.screenX) && std::isfinite(reading.screenY);
            return reading.projected;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    inline void Disc(void* canvas, float x, float y, float radius, const float* color)
    {
        struct Point { float x, y; };
        std::array<Point, 15> vertices{};
        vertices[0] = { x, y };
        for (size_t index = 0; index <= 14; ++index)
        {
            const auto angle = static_cast<float>(index % 14) * 6.283185307f / 14.f;
            vertices[index + 1] = { x + cosf(angle) * radius, y + sinf(angle) * radius };
        }
        const std::array<const Point*, 3> view{ vertices.data(), vertices.data() + 15, vertices.data() + 15 };
        fan(canvas, &view, color);
    }

    inline void DrawTarget(void* canvas, float x, float y)
    {
        if (!fan) return;
        constexpr float shade[]{ .02f, .01f, .04f, 1.f };
        constexpr float bright[]{ 1.f, .12f, .85f, 1.f };
        constexpr float white[]{ 1.f, 1.f, 1.f, 1.f };
        Disc(canvas, x, y, 22.f, shade);
        Disc(canvas, x, y, 17.f, bright);
        Disc(canvas, x, y, 6.f, white);
    }

    inline void Render(void* canvas, unsigned canvasOffsetX, unsigned canvasOffsetY, bool boundsFresh)
    {
        if (!available) return;
        float worldX = 0, worldZ = 0;
        AcquireSRWLockShared(&lock);
        worldX = targetX; worldZ = targetZ;
        ReleaseSRWLockShared(&lock);
        Reading reading{};
        const bool projected = Read(reading, canvas, canvasOffsetX, canvasOffsetY, worldX, worldZ);
        const bool draw = armed.load(std::memory_order_relaxed) && projected;
        if (draw) DrawTarget(canvas, reading.screenX, reading.screenY);
        const auto now = GetTickCount64();
        AcquireSRWLockExclusive(&lock);
        snapshot.frames++;
        snapshot.lastFrameAt = now;
        snapshot.boundsFresh = boundsFresh;
        snapshot.mode = reading.mode;
        snapshot.enabled = reading.enabled;
        snapshot.orthographic = reading.orthographic;
        snapshot.vtable = reading.vtable;
        snapshot.region = reading.region;
        snapshot.viewport = reading.viewport;
        snapshot.originX = reading.originX;
        snapshot.originZ = reading.originZ;
        snapshot.screenX = reading.screenX;
        snapshot.screenY = reading.screenY;
        snapshot.projected = projected;
        if (draw) { snapshot.rendered++; snapshot.renderedAt = now; }
        ReleaseSRWLockExclusive(&lock);
    }

    inline void Describe(char* response, size_t size)
    {
        Snapshot value;
        float x = 0, z = 0;
        AcquireSRWLockShared(&lock);
        value = snapshot; x = targetX; z = targetZ;
        ReleaseSRWLockShared(&lock);
        const auto now = GetTickCount64();
        sprintf_s(response, size,
            "OK MAP_PROBE armed=%d frames=%llu frameAge=%llu drawn=%llu drawAge=%llu mode=%d enabled=%d "
            "vtable=%d ortho=%d region=%d projected=%d compass=%d screen=%g,%g origin=%g,%g "
            "viewport=%g,%g,%g,%g target=%g,%g",
            armed.load() ? 1 : 0, value.frames, value.lastFrameAt ? now - value.lastFrameAt : 999999ULL,
            value.rendered, value.renderedAt ? now - value.renderedAt : 999999ULL, value.mode, value.enabled,
            value.vtable ? 1 : 0, value.orthographic, value.region ? 1 : 0, value.projected ? 1 : 0,
            value.boundsFresh ? 1 : 0, value.screenX, value.screenY,
            value.originX, value.originZ, value.viewport[0], value.viewport[1], value.viewport[2], value.viewport[3],
            x, z);
    }

    inline bool Initialize(HMODULE engine)
    {
        if (!MinimapProjection::available) return false;
        fan = reinterpret_cast<FanFn>(GetProcAddress(engine,
            "?RenderTriFan@GraphicsCanvas@GAME@@QEAAXAEBV?$vector@VVec2@GAME@@@mem@@AEBVColor@2@@Z"));
        available = fan != nullptr;
        return available;
    }
}
