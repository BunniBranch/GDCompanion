#pragma once
#include <array>
#include <charconv>
#include <cmath>
#include <string>
#include <string_view>
#include <vector>

namespace RadarCanvas
{
    constexpr size_t MaxMarkers = 112;
    constexpr float PoiEdgeRatio = .91f;
    constexpr float QuestEdgeRatio = 1.05f;
    struct Marker { float x = 0, z = 0; unsigned kind = 0; std::string label; };
    struct Frame { unsigned long long context = 0, updated = 0; std::vector<Marker> markers; };
    inline std::vector<std::string_view> Split(std::string_view value, char separator)
    {
        std::vector<std::string_view> fields;
        for (;;)
        {
            const auto end = value.find(separator);
            fields.push_back(value.substr(0, end));
            if (end == value.npos) return fields;
            value.remove_prefix(end + 1);
        }
    }
    template<class T> bool Number(std::string_view value, T& result)
    {
        if (value.empty()) return false;
        const auto parsed = std::from_chars(value.data(), value.data() + value.size(), result);
        return parsed.ec == std::errc{} && parsed.ptr == value.data() + value.size();
    }
    inline bool ParseMarker(std::string_view input, Marker& marker)
    {
        const auto fields = Split(input, ',');
        if (fields.size() != 4 || !Number(fields[0], marker.x) || !Number(fields[1], marker.z) ||
            !Number(fields[2], marker.kind) || !std::isfinite(marker.x) || !std::isfinite(marker.z) ||
            fabsf(marker.x) > 1000000 || fabsf(marker.z) > 1000000 || marker.kind > 6 ||
            fields[3].empty() || fields[3].size() > 384 || fields[3].size() % 2 != 0) return false;
        marker.label.clear();
        for (size_t i = 0; i < fields[3].size(); i += 2)
        {
            unsigned int byte = 0;
            const auto parsed = std::from_chars(fields[3].data() + i, fields[3].data() + i + 2, byte, 16);
            if (parsed.ec != std::errc{} || parsed.ptr != fields[3].data() + i + 2 || byte < 32 || byte == 127) return false;
            marker.label.push_back(static_cast<char>(byte));
        }
        return true;
    }
    struct State
    {
        Frame active, pending;
        size_t expected = 0;
        bool building = false;
        void Clear() { active = {}; pending = {}; expected = 0; building = false; }
        bool Command(std::string_view input, unsigned long long now)
        {
            const auto parts = Split(input, '\t');
            if (parts.size() == 1 && parts[0] == "CLEAR") { Clear(); return true; }
            if (parts.size() == 3 && parts[0] == "BEGIN")
            {
                pending = {}; building = false;
                if (!Number(parts[1], pending.context) || !pending.context || !Number(parts[2], expected) || expected > MaxMarkers) return false;
                pending.updated = now; building = true; return true;
            }
            if (!building || now < pending.updated || now - pending.updated > 1000) { building = false; return false; }
            if (parts.size() >= 2 && parts[0] == "ADD")
            {
                for (size_t i = 1; i < parts.size(); ++i)
                {
                    Marker marker;
                    if (pending.markers.size() >= expected || !ParseMarker(parts[i], marker)) { building = false; return false; }
                    pending.markers.push_back(std::move(marker));
                }
                return true;
            }
            if (parts.size() == 1 && parts[0] == "COMMIT" && pending.markers.size() == expected)
            {
                pending.updated = now; active = std::move(pending); building = false; return true;
            }
            building = false; return false;
        }
    };
    inline bool Fresh(const Frame& frame, unsigned long long context, unsigned long long now)
    { return context && frame.context == context && frame.updated && now >= frame.updated && now - frame.updated < 1500; }

    // Identical world-to-minimap transform and rim clamping to the external renderer.
    inline std::array<float, 2> Project(const Marker& marker, const std::array<float, 8>& transform)
    {
        const float dx = marker.x - transform[0], dz = marker.z - transform[1];
        const float x = transform[2] + dx * transform[4] + dz * transform[6];
        const float y = transform[3] + dx * transform[5] + dz * transform[7];
        const float radius = hypotf(x, y);
        // Keep visible quest locations exact; only off-map directions use
        // the outer star lane. POIs retain their existing inner rim.
        const float scale = marker.kind == 0
            ? (radius > 1.f ? QuestEdgeRatio / radius : 1.f)
            : (radius > PoiEdgeRatio ? PoiEdgeRatio / radius : 1.f);
        return { x * scale, y * scale };
    }
}
