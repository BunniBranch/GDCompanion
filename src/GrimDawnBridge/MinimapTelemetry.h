#pragma once
#include "MinimapProjection.h"

// Observe, but never modify, the game's compass texture draws. Stock artwork
// contains transparent padding: circular rims are260/371 and174/247 pixels.
namespace MinimapTelemetry
{
    using TextureFn = const void*(__fastcall*)(const void*);
    using FrameFn = const void*(__fastcall*)(const void*, int);
    using NameFn = const char*(__fastcall*)(const void*);
    using ObjectFn = void*(__fastcall*)(void*);
    using DimensionFn = int(__fastcall*)(const void*);
    using RectFn = void(__fastcall*)(void*, const void*, const void*, const void*, const void*, const void*, const void*, float);
    inline TextureFn originalTexture = nullptr;
    inline FrameFn originalFrame = nullptr;
    inline RectFn originalRect = nullptr;
    inline NameFn fileName = nullptr;
    inline ObjectFn getGraphics = nullptr;
    inline DimensionFn getWidth = nullptr, getHeight = nullptr;
    inline void** engineInstance = nullptr;
    inline std::array<std::atomic<const void*>, 2> textures{};
    inline SRWLOCK lock = SRWLOCK_INIT;
    inline std::array<float, 6> bounds{};
    inline std::array<float, 8> projection{};
    inline bool projectionValid = false;
    inline unsigned long long sampledAt = 0;
    inline unsigned long long projectedAt = 0;
    inline std::array<float, 6> projectedBounds{};
    inline unsigned int canvasOffsetX = 0, canvasOffsetY = 0;
    inline bool available = false;
    inline unsigned long long renderContext = 0;
    inline unsigned long long nextRenderContext = 0;
    inline unsigned long long lastRenderAt = 0;
    inline void* lastRenderRegion = nullptr;
    inline bool mapValid = false;
    inline unsigned long long mapSampledAt = 0;
    using FrameCallback = void(*)(void*, const std::array<float, 6>&, const std::array<float, 8>&, unsigned long long);
    inline std::atomic<FrameCallback> onFrame{nullptr};
    using MapCallback = void(*)(void*, const std::array<float, 8>&, unsigned long long);
    inline std::atomic<MapCallback> onMapFrame{nullptr};

    inline void ObserveTexture(const void* resource, const void* texture)
    {
        if (!resource || !texture) return;
        const char* path = fileName(resource);
        if (!path) return;
        const char* leaf = strrchr(path, '/');
        const char* backslash = strrchr(path, '\\');
        if (!leaf || (backslash && backslash > leaf)) leaf = backslash;
        leaf = leaf ? leaf + 1 : path;
        if (_stricmp(leaf, "hud_compassfloatinglarge.tex") == 0) textures[0].store(texture);
        else if (_stricmp(leaf, "hud_compassfloating.tex") == 0) textures[1].store(texture);
    }
    inline const void* __fastcall TextureHook(const void* resource)
    {
        const auto texture = originalTexture(resource);
        ObserveTexture(resource, texture);
        return texture;
    }
    inline const void* __fastcall FrameHook(const void* resource, int frame)
    {
        const auto texture = originalFrame(resource, frame);
        ObserveTexture(resource, texture);
        return texture;
    }
    inline void ObserveRect(void* canvas, const float* rect, const float* source, int kind)
    {
        const float textureSize = kind == 0 ? 371.0f : 247.0f;
        const float circleSize = kind == 0 ? 260.0f : 174.0f;
        if (!rect || !source || fabsf(source[0]) > .01f || fabsf(source[1]) > .01f ||
            fabsf(source[2] - textureSize) > .01f || fabsf(source[3] - textureSize) > .01f) return;
        for (int index = 0; index < 4; ++index) if (!std::isfinite(rect[index])) return;
        if (rect[2] < 64 || rect[2] > 4096 || fabsf(rect[2] - rect[3]) > 2) return;
        auto graphics = engineInstance && *engineInstance ? getGraphics(*engineInstance) : nullptr;
        if (!graphics) return;
        const int width = getWidth(graphics), height = getHeight(graphics);
        if (width < 640 || height < 480 || width > 32768 || height > 32768) return;
        const float offsetX = *reinterpret_cast<const float*>(static_cast<unsigned char*>(canvas) + canvasOffsetX);
        const float offsetY = *reinterpret_cast<const float*>(static_cast<unsigned char*>(canvas) + canvasOffsetY);
        const float diameter = rect[2] * circleSize / textureSize;
        const float x = rect[0] + offsetX + (rect[2] - diameter) * .5f;
        const float y = rect[1] + offsetY + (rect[3] - diameter) * .5f;
        if (!std::isfinite(x) || !std::isfinite(y) || x < 0 || y < 0 || x + diameter > width + 2 || y + diameter > height + 2) return;
        AcquireSRWLockExclusive(&lock);
        bounds = { x, y, diameter, diameter, static_cast<float>(width), static_cast<float>(height) };
        sampledAt = GetTickCount64();
        ReleaseSRWLockExclusive(&lock);
    }
    // The compass artwork is a sibling UI draw, outside the map's render call.
    // Sample its stable bounds separately, then project one 3-point affine
    // basis at the end of either the corner minimap or full-screen map pass
    // under a single unified region/session renderContext.
    inline void ObserveMap(void* canvas)
    {
        if (!canvas) return;
        std::array<float, 6> frameBounds{};
        AcquireSRWLockShared(&lock);
        frameBounds = bounds;
        const bool fresh = sampledAt != 0 && GetTickCount64() - sampledAt < 150;
        ReleaseSRWLockShared(&lock);
        std::array<float, 8> transform{};
        void* minimapRegion = nullptr;
        void* mapRegion = nullptr;
        const float offsetX = *reinterpret_cast<const float*>(static_cast<unsigned char*>(canvas) + canvasOffsetX);
        const float offsetY = *reinterpret_cast<const float*>(static_cast<unsigned char*>(canvas) + canvasOffsetY);
        const bool minimapValid = fresh &&
            MinimapProjection::Sample(frameBounds[0], frameBounds[1], frameBounds[2], offsetX, offsetY, transform, &minimapRegion);
        const bool mapValidNow = !minimapValid &&
            MinimapProjection::SampleMap(offsetX, offsetY, transform, &mapRegion);
        void* const activeRegion = minimapValid ? minimapRegion : (mapValidNow ? mapRegion : nullptr);
        AcquireSRWLockExclusive(&lock);
        const auto now = GetTickCount64();
        if (activeRegion)
        {
            const bool minimapBoundsChanged = minimapValid && projectionValid && frameBounds != projectedBounds;
            if (renderContext == 0 || activeRegion != lastRenderRegion || now - lastRenderAt > 300 || minimapBoundsChanged)
                renderContext = ++nextRenderContext;
            lastRenderRegion = activeRegion;
            lastRenderAt = now;
        }
        else if (lastRenderAt != 0 && now - lastRenderAt > 300)
        {
            renderContext = 0;
            projectionValid = false;
            mapValid = false;
            lastRenderRegion = nullptr;
        }
        if (minimapValid)
        {
            projectedBounds = frameBounds;
            projection = transform;
            projectionValid = true;
            projectedAt = now;
        }
        if (mapValidNow)
        {
            mapValid = true;
            mapSampledAt = now;
        }
        const auto context = renderContext;
        ReleaseSRWLockExclusive(&lock);
        if (minimapValid)
        {
            const auto callback = onFrame.load(std::memory_order_acquire);
            if (callback) callback(canvas, frameBounds, transform, context);
        }
        else if (mapValidNow)
        {
            const auto mapCallback = onMapFrame.load(std::memory_order_acquire);
            if (mapCallback) mapCallback(canvas, transform, context);
        }
    }
    inline void __fastcall RectHook(void* canvas, const void* first, const void* second,
        const void* texture, const void* shader, const void* name, const void* color, float value)
    {
        if (texture)
        {
            const int kind = texture == textures[0].load() ? 0 : texture == textures[1].load() ? 1 : -1;
            if (kind >= 0) ObserveRect(canvas, static_cast<const float*>(first), static_cast<const float*>(second), kind);
        }
        originalRect(canvas, first, second, texture, shader, name, color, value);
    }
    inline bool Initialize(HMODULE engine)
    {
        fileName = reinterpret_cast<NameFn>(GetProcAddress(engine, "?GetFileName@Resource@GAME@@QEBAPEBDXZ"));
        engineInstance = reinterpret_cast<void**>(GetProcAddress(engine, "?gEngine@GAME@@3PEAVEngine@1@EA"));
        getGraphics = reinterpret_cast<ObjectFn>(GetProcAddress(engine, "?GetGraphicsEngine@Engine@GAME@@QEBAPEAVGraphicsEngine@2@XZ"));
        getWidth = reinterpret_cast<DimensionFn>(GetProcAddress(engine, "?GetWidth@GraphicsEngine@GAME@@QEBAHXZ"));
        getHeight = reinterpret_cast<DimensionFn>(GetProcAddress(engine, "?GetHeight@GraphicsEngine@GAME@@QEBAHXZ"));
        void* targets[] = {
            reinterpret_cast<void*>(GetProcAddress(engine, "?GetTexture@GraphicsTexture@GAME@@QEBAPEBVRenderTexture@2@XZ")),
            reinterpret_cast<void*>(GetProcAddress(engine, "?GetTexture@GraphicsTexture@GAME@@QEBAPEBVRenderTexture@2@H@Z")),
            reinterpret_cast<void*>(GetProcAddress(engine, "?RenderShadedRect@GraphicsCanvas@GAME@@QEAAXAEBVRect@2@0PEBVRenderTexture@2@PEBVGraphicsShader2@2@AEBVName@2@AEBVColor@2@M@Z"))
        };
        if (!fileName || !engineInstance || !getGraphics || !getWidth || !getHeight || !targets[0] || !targets[1] || !targets[2]) return false;
        // Resolve canvas translation fields from the renderer's actual movss
        // instructions. Unknown layouts disable this optional capability.
        const auto code = static_cast<const unsigned char*>(targets[2]);
        const unsigned char xPattern[] = {0xf3, 0x0f, 0x10, 0x91};
        const unsigned char yPattern[] = {0xf3, 0x0f, 0x10, 0x89};
        if (memcmp(code + 26, xPattern, 4) || memcmp(code + 37, yPattern, 4)) return false;
        memcpy(&canvasOffsetX, code + 30, 4);
        memcpy(&canvasOffsetY, code + 41, 4);
        if (canvasOffsetX > 4096 || canvasOffsetY != canvasOffsetX + 4) return false;
        void* detours[] = { reinterpret_cast<void*>(&TextureHook), reinterpret_cast<void*>(&FrameHook), reinterpret_cast<void*>(&RectHook) };
        void** originals[] = { reinterpret_cast<void**>(&originalTexture), reinterpret_cast<void**>(&originalFrame), reinterpret_cast<void**>(&originalRect) };
        size_t count = 0;
        for (; count < 3; ++count) if (MH_CreateHook(targets[count], detours[count], originals[count]) != MH_OK) break;
        if (count == 3)
        {
            bool queued = true;
            for (size_t index = 0; index < 3; ++index) if (MH_QueueEnableHook(targets[index]) != MH_OK) queued = false;
            if (queued) { available = true; MinimapProjection::onRendered = &ObserveMap; MinimapProjection::Initialize(engine); return true; }
        }
        for (size_t index = 0; index < count; ++index) MH_RemoveHook(targets[index]);
        return false;
    }
    inline bool Read(std::array<float, 6>& result)
    {
        AcquireSRWLockShared(&lock);
        result = bounds;
        const bool fresh = sampledAt != 0 && GetTickCount64() - sampledAt < 300;
        ReleaseSRWLockShared(&lock);
        return fresh;
    }
    inline bool ReadRadarFrame(bool& minimapOpen, bool& mapOpen, std::array<float, 6>& result,
        std::array<float, 8>& transform, unsigned long long& context)
    {
        AcquireSRWLockShared(&lock);
        const auto now = GetTickCount64();
        minimapOpen = projectionValid && projectedAt != 0 && now - projectedAt < 150 &&
            sampledAt != 0 && now - sampledAt < 150 && bounds == projectedBounds;
        mapOpen = mapValid && mapSampledAt != 0 && now - mapSampledAt < 150;
        context = (minimapOpen || mapOpen) ? renderContext : 0;
        if (minimapOpen) { result = projectedBounds; transform = projection; }
        ReleaseSRWLockShared(&lock);
        return context != 0;
    }
    inline bool ReadFrame(std::array<float, 6>& result, std::array<float, 8>& transform, unsigned long long* context = nullptr)
    {
        bool minimapOpen = false, mapOpen = false;
        unsigned long long current = 0;
        ReadRadarFrame(minimapOpen, mapOpen, result, transform, current);
        if (context) *context = minimapOpen ? current : 0;
        return minimapOpen;
    }
}
