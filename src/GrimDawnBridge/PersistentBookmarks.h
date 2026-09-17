#pragma once
#include <sstream>
#include <locale>
#include <algorithm>
#include <charconv>

// All calls execute on the game's update thread. Disk/protocol data never
// contains game pointers. Unloaded surface destinations use the game's own
// asynchronous teleport activity, never a direct move into an unloaded level.
namespace PersistentBookmarks
{
    using Pointer = void*(__fastcall*)(void*);
    using Boolean = bool(__fastcall*)(void*);
    using Unsigned = unsigned(__fastcall*)(void*);
    using Coords = void*(__fastcall*)(void*, void*);
    using RegionAt = void*(__fastcall*)(void*, void*, float, float);
    using Convert = bool(__fastcall*)(void*, const void*, void*);
    using Path = void(__fastcall*)(void*, std::string*);
    using FileName = const char*(__fastcall*)(void*);
    using StringRef = const std::string&(__fastcall*)(void*);
    using Teleport = void(__fastcall*)(void*, const void*);
    using Singleton = void*(__fastcall*)();
    using Travel = void(__fastcall*)(void*, int, int, int, int, bool);
    using RegionPoint = void*(__fastcall*)(void*, const int*);
    inline Travel travel = nullptr;
    inline RegionPoint regionPoint = nullptr;
    inline Singleton activityManager = nullptr;
    inline Boolean activityBusy = nullptr;
    inline bool loadingAvailable = false;
    using ZoneData = const void*(__fastcall*)(void*, const std::string&);
    using Localize = const wchar_t*(__fastcall*)(void*, const char*);
    inline StringRef zoneRecord = nullptr;
    inline Singleton zoneManager = nullptr, localization = nullptr;
    inline ZoneData zoneData = nullptr;
    inline Localize localize = nullptr;
    inline Boolean hardcore = nullptr, loaded = nullptr, finished = nullptr, underground = nullptr, teleporting = nullptr;
    inline Unsigned difficulty = nullptr, mode = nullptr;
    inline Coords coords = nullptr;
    inline RegionAt regionAt = nullptr;
    inline Convert convert = nullptr;
    inline Path regionPath = nullptr;
    inline FileName worldFile = nullptr;
    inline StringRef modName = nullptr;
    inline Teleport teleport = nullptr;
    inline bool available = false;

    inline bool KnownImage(HMODULE module, DWORD timestamp, DWORD imageSize)
    {
        auto bytes = reinterpret_cast<unsigned char*>(module);
        auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(bytes);
        auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(bytes + dos->e_lfanew);
        return nt->FileHeader.TimeDateStamp == timestamp && nt->OptionalHeader.SizeOfImage == imageSize;
    }
    inline void Initialize(HMODULE engine, HMODULE game)
    {
        using MasteryAudit::Bind;
        Bind(hardcore, game, "?IsHardcore@Player@GAME@@QEBA_NXZ");
        Bind(difficulty, game, "?GetGameDifficulty@GameEngine@GAME@@QEBA?AW4GameDifficulty@2@XZ");
        Bind(teleporting, game, "?IsTeleporting@Character@GAME@@QEBA_NXZ");
        Bind(mode, engine, "?GetMode@GameInfo@GAME@@QEBAIXZ");
        Bind(modName, engine, "?GetModName@GameInfo@GAME@@QEBAAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ");
        Bind(coords, engine, "?GetCoords@Entity@GAME@@QEBA?AVWorldCoords@2@XZ");
        Bind(regionAt, engine, "?GetRegionContainingXZ@World@GAME@@QEBAPEAVRegion@2@PEAV32@MM@Z");
        Bind(convert, engine, "?SetFromWorldPosition@WorldVec3@GAME@@QEAA_NAEBVVec3@2@PEAVRegion@2@@Z");
        Bind(regionPath, engine, "?GetLoadFileName@Region@GAME@@QEBA?AV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ");
        Bind(worldFile, engine, "?GetFileName@World@GAME@@QEBAPEBDXZ");
        Bind(loaded, engine, "?IsLevelLoaded@Region@GAME@@QEBA_NXZ");
        Bind(finished, engine, "?IsLoadingFinished@Region@GAME@@QEBA_NXZ");
        Bind(underground, engine, "?IsUnderground@Region@GAME@@QEBA_NXZ");
        Bind(teleport, game, "?TeleportToLocation@Character@GAME@@UEAAXAEBVWorldCoords@2@@Z");
        Bind(travel, game, "?InitiatePlayerTeleport@GameEngine@GAME@@QEAAXHHHW4TeleportEffect@2@_N@Z");
        Bind(regionPoint, engine, "?GetRegionContainingPoint@World@GAME@@QEBAPEAVRegion@2@AEBVIntVec3@2@@Z");
        Bind(activityManager, game, "?Get@ActivityManager@GAME@@SAPEAV12@XZ");
        Bind(activityBusy, game, "?IsAlreadyTeleporting@ActivityManager@GAME@@QEBA_NXZ");
        Bind(zoneRecord, engine, "?GetZoneRecord@Region@GAME@@QEBAAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ");
        Bind(zoneManager, engine, "?Get@ZoneManager@GAME@@SAPEAV12@XZ");
        Bind(zoneData, engine, "?GetZoneData@ZoneManager@GAME@@QEBAPEBUZoneData@12@AEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@@Z");
        Bind(localization, engine, "?Instance@LocalizationManager@GAME@@SAAEAV12@XZ");
        Bind(localize, engine, "?LocalizeWithoutParams@LocalizationManager@GAME@@QEAAPEBGPEBD@Z");
        // WorldCoords layout and Region world/origin offsets
        // were checked against these installed binaries. Fail closed on patches.
        available = KnownImage(engine, 0x6A85FB5B, 0x450000) && KnownImage(game, 0x6A85FBB3, 0xAB5000) &&
            MasteryAudit::available && MasteryRespec::engineInstance && MasteryRespec::gameInfo &&
            MasteryRespec::singlePlayer && MasteryRespec::alive && MasteryRespec::attacked &&
            hardcore && difficulty && teleporting && mode && modName && coords && regionAt && convert &&
            regionPath && worldFile && loaded && finished && underground && teleport;
        loadingAvailable = available && travel && regionPoint && activityManager && activityBusy;
    }

    inline std::string Hex(const void* bytes, size_t count)
    {
        constexpr char digits[] = "0123456789ABCDEF";
        std::string result; result.reserve(count * 2);
        auto data = static_cast<const unsigned char*>(bytes);
        for (size_t i = 0; i < count; ++i) { result += digits[data[i] >> 4]; result += digits[data[i] & 15]; }
        return result;
    }
    inline std::string Normalize(std::string value)
    {
        for (auto& c : value) { if (c == '\\') c = '/'; else if (c >= 'A' && c <= 'Z') c += 'a' - 'A'; }
        return value;
    }
    inline bool HexToken(const std::string& value, size_t max, size_t exact = 0)
    {
        return !value.empty() && value.size() <= max && value.size() % 2 == 0 && (!exact || value.size() == exact) &&
            std::all_of(value.begin(), value.end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'); });
    }
    struct Snapshot
    {
        std::string uid, name, world, region;
        unsigned diff = 0, hc = 0;
        float x = 0, y = 0, z = 0;
    };
    inline bool Valid(const Snapshot& s)
    {
        return HexToken(s.uid, 32, 32) && HexToken(s.name, 508) && s.name.size() % 4 == 0 &&
            HexToken(s.world, 1024) && HexToken(s.region, 1024) && s.diff <= 2 && s.hc <= 1 &&
            std::isfinite(s.x) && std::isfinite(s.y) && std::isfinite(s.z) &&
            std::abs(s.x) < 1000000 && std::abs(s.y) < 1000000 && std::abs(s.z) < 1000000;
    }
    inline std::string Serialize(const Snapshot& s)
    {
        std::ostringstream out; out.imbue(std::locale::classic()); out.precision(9);
        out << s.uid << ' ' << s.name << ' ' << s.diff << ' ' << s.hc << ' ' << s.world << ' ' << s.region << ' ' << s.x << ' ' << s.y << ' ' << s.z;
        return out.str();
    }
    inline bool Parse(const char* input, Snapshot& s)
    {
        std::istringstream in(input); in.imbue(std::locale::classic());
        if (!(in >> s.uid >> s.name >> s.diff >> s.hc >> s.world >> s.region >> s.x >> s.y >> s.z)) return false;
        in >> std::ws; return in.eof() && Valid(s);
    }
    inline void* Region(const void* c) { void* value; memcpy(&value, c, sizeof(value)); return value; }
    inline void* World(void* region) { void* value; memcpy(&value, static_cast<unsigned char*>(region) + 0x30, sizeof(value)); return value; }
    inline void Position(const void* c, Snapshot& s)
    {
        float local[3]; int offset[3];
        memcpy(local, static_cast<const unsigned char*>(c) + 8, sizeof(local));
        memcpy(offset, static_cast<unsigned char*>(Region(c)) + 0x3c, sizeof(offset));
        s.x = local[0] + offset[0]; s.y = local[1] + offset[1]; s.z = local[2] + offset[2];
    }
    inline void* ResolveDestination(void* source, const Snapshot& target)
    {
        auto world = source ? World(source) : nullptr;
        if (!world) return nullptr;
        int origin[3];
        memcpy(origin, static_cast<unsigned char*>(source) + 0x3c, sizeof(origin));
        // GetRegionContainingXZ adds the supplied source region's origin to
        // its X/Z arguments (Engine RVA 0x21CF80). Saved positions are global,
        // so remove that origin first. SetFromWorldPosition below still takes
        // the original global XYZ and performs the destination-local conversion.
        return regionAt(world, source, target.x - origin[0], target.z - origin[2]);
    }
    inline std::string RegionHex(void* region)
    {
        std::string path; regionPath(region, &path); path = Normalize(path);
        return Hex(path.data(), path.size());
    }
    inline const char* Capture(void* game, void* player, Snapshot& out, void* buffer)
    {
        if (!available) return "UNAVAILABLE_RECONNECT_OR_UNSUPPORTED_PATCH";
        if (!game || !player || MasteryAudit::loading(game)) return "NO_READY_PLAYER";
        auto engine = *MasteryRespec::engineInstance;
        auto info = engine ? MasteryRespec::gameInfo(engine) : nullptr;
        if (!info || !MasteryRespec::singlePlayer(info)) return "SINGLE_PLAYER_ONLY";
        if (!MasteryRespec::alive(game) || MasteryRespec::attacked(player) || teleporting(player)) return "PLAYER_NOT_READY_OR_IN_COMBAT";
        if (mode(info) != 0 || !modName(info).empty()) return "CAMPAIGN_ONLY";
        coords(player, buffer);
        auto region = Region(buffer);
        if (!region || !loaded(region) || !finished(region) || underground(region)) return "LOADED_OUTDOOR_AREA_REQUIRED";
        auto world = World(region);
        auto path = world ? worldFile(world) : nullptr;
        if (!path || strnlen_s(path, 513) > 512) return "INVALID_WORLD";
        auto normalized = Normalize(path);
        if (!normalized.ends_with("world001.map")) return "CAMPAIGN_ONLY";
        auto name = MasteryAudit::playerName(player);
        if (!name || wcsnlen_s(name, 128) == 128) return "INVALID_CHARACTER";
        // Retain the legacy field for stored-bookmark compatibility, but never
        // read or require the game's often-unset save ID. Locations are shared.
        out.uid = std::string(32, '0'); out.name = Hex(name, wcslen(name) * sizeof(wchar_t));
        out.diff = difficulty(game); out.hc = hardcore(player) ? 1 : 0;
        out.world = Hex(normalized.data(), normalized.size()); out.region = RegionHex(region);
        Position(buffer, out);
        return Valid(out) ? nullptr : "INVALID_SNAPSHOT";
    }
    inline bool SameContext(const Snapshot& a, const Snapshot& b)
    { return a.world == b.world; } // Difficulty and hardcore are informational metadata.

    // Region::IsUnderground itself calls LoadLevel, so NEVER use that getter on
    // an unloaded destination. Limit streaming to numbered campaign surface
    // tiles; unknown layouts retain the already-loaded return path only.
    inline bool SurfaceTile(void* region)
    {
        std::string path; regionPath(region, &path); path = Normalize(path);
        const auto slash = path.find_last_of('/');
        const auto name = slash == std::string::npos ? path : path.substr(slash + 1);
        return name.size() > 12 && name.starts_with("region0") && name[7] >= 'a' && name[7] <= 'z' &&
            name.ends_with(".lvl") && std::all_of(name.begin() + 8, name.end() - 4, [](char c) { return c >= '0' && c <= '9'; });
    }
    inline bool TravelBusy()
    {
        if (!loadingAvailable) return false;
        auto manager = activityManager();
        return manager && activityBusy(manager);
    }
    struct PendingTravel
    {
        unsigned long long token = 0, started = 0;
        unsigned playerId = 0;
        void* game = nullptr;
        void* player = nullptr; // Identity comparison only; never dereferenced later.
        Snapshot target;
        std::string name;
        bool active = false;
        const char* result = "ERROR BOOKMARK_TRAVEL_UNKNOWN_CHECK_GAME_DO_NOT_RETRY";
    };
    inline PendingTravel pending;
    inline unsigned long long nextTravelToken = 0;
    inline bool Arrived(const Snapshot& actual, const Snapshot& target)
    {
        return SameContext(actual, target) && actual.region == target.region &&
            std::abs(actual.x - target.x) <= 1 && std::abs(actual.y - target.y) <= 2 && std::abs(actual.z - target.z) <= 1;
    }
    inline void RefreshTravel(void* game, void* player)
    {
        if (!pending.active) return;
        if (game != pending.game || GetTickCount64() - pending.started >= 120000)
        { pending.active = false; return; }
        // Let the game finish its own activity. Do not touch its ownership,
        // loading flags, region loader, cancellation or rift-unlock state.
        if (TravelBusy() || (game && MasteryAudit::loading(game)) || (player && teleporting(player))) return;
        pending.active = false;
        if (!player || player != pending.player || MasteryAudit::objectId(player) != pending.playerId) return;
        alignas(16) unsigned char buffer[0x40]{}; Snapshot actual;
        if (!Capture(game, player, actual, buffer) && actual.name == pending.name && Arrived(actual, pending.target))
            pending.result = "OK BOOKMARK RETURNED";
    }

    inline std::string AreaName(void* region)
    {
        if (!zoneRecord || !zoneManager || !zoneData || !localization || !localize) return {};
        auto manager = zoneManager();
        auto translator = localization();
        if (!manager || !translator) return {};
        auto data = zoneData(manager, zoneRecord(region));
        if (!data) return {};
        // ZoneData begins with the zone-name tag std::string, as used by
        // ZoneManager::RenderZoneKey in the fingerprint-guarded Engine build.
        const auto& tag = *static_cast<const std::string*>(data);
        if (tag.empty() || tag.size() > 256) return {};
        auto text = localize(translator, tag.c_str());
        if (!text) return {};
        auto length = wcsnlen_s(text, 257);
        if (!length || length > 256 || wcsncmp(text, L"tag", 3) == 0 || wcsncmp(text, L"Tag not found", 13) == 0) return {};
        return Hex(text, length * sizeof(wchar_t));
    }

    inline void Execute(void* game, void* player, const char* command, unsigned long long deadline, char* response, size_t size, bool& moved)
    {
        if (strncmp(command, "STATUS ", 7) == 0)
        {
            unsigned long long token = 0;
            const auto end = command + strlen(command);
            auto parsed = std::from_chars(command + 7, end, token);
            if (parsed.ec != std::errc{} || parsed.ptr != end || !token || token != pending.token)
            { strcpy_s(response, size, "ERROR BOOKMARK_INVALID_TRAVEL_TOKEN"); return; }
            moved = true; // Status failures after submission have an unknown outcome.
            RefreshTravel(game, player);
            if (pending.active) sprintf_s(response, size, "OK BOOKMARK LOADING %llu", token);
            else strcpy_s(response, size, pending.result);
            return;
        }
        Snapshot target;
        const bool read = strcmp(command, "READ") == 0;
        const bool label = strncmp(command, "LABEL ", 6) == 0;
        const bool allowLoad = strncmp(command, "TRAVEL ", 7) == 0;
        unsigned expectedPlayerId = 0;
        if (label)
        {
            if (!Parse(command + 6, target)) { strcpy_s(response, size, "ERROR BOOKMARK_INVALID_REQUEST"); return; }
        }
        else if (!read)
        {
            if (!allowLoad && strncmp(command, "RETURN ", 7) != 0) { strcpy_s(response, size, "ERROR BOOKMARK_INVALID_REQUEST"); return; }
            const auto end = command + strlen(command);
            const auto parsed = std::from_chars(command + 7, end, expectedPlayerId);
            if (parsed.ec != std::errc{} || parsed.ptr == end || *parsed.ptr != ' ' || !expectedPlayerId || !Parse(parsed.ptr + 1, target))
            { strcpy_s(response, size, "ERROR BOOKMARK_INVALID_REQUEST"); return; }
        }
        if (GetTickCount64() > deadline) { strcpy_s(response, size, "ERROR BOOKMARK_EXPIRED_NO_MOVE"); return; }
        if (allowLoad && !loadingAvailable) { strcpy_s(response, size, "ERROR BOOKMARK_LOADING_UNAVAILABLE"); return; }
        if (!read && !label)
        {
            RefreshTravel(game, player);
            if (pending.active || TravelBusy()) { strcpy_s(response, size, "ERROR BOOKMARK_TRAVEL_IN_PROGRESS"); return; }
        }
        alignas(16) unsigned char currentCoords[0x40]{};
        Snapshot current;
        if (auto error = Capture(game, player, current, currentCoords))
        { sprintf_s(response, size, "ERROR BOOKMARK_%s", error); return; }
        const auto playerId = MasteryAudit::objectId(player);
        if (!playerId) { strcpy_s(response, size, "ERROR BOOKMARK_NO_READY_PLAYER"); return; }
        if (read) { sprintf_s(response, size, "OK BOOKMARK 2 %u %s", playerId, Serialize(current).c_str()); return; }
        if (label)
        {
            auto source = Region(currentCoords);
            auto destination = SameContext(current, target) ? ResolveDestination(source, target) : nullptr;
            if (!destination || !loaded(destination) || !finished(destination) || RegionHex(destination) != target.region)
            { strcpy_s(response, size, "OK BOOKMARK LABEL -"); return; }
            auto name = AreaName(destination);
            sprintf_s(response, size, "OK BOOKMARK LABEL %s", name.empty() ? "-" : name.c_str()); return;
        }
        // Shared destination does not authorize moving a different character
        // if the user changed characters while the confirmation was open.
        if (playerId != expectedPlayerId) { strcpy_s(response, size, "ERROR BOOKMARK_CHARACTER_CHANGED_NO_MOVE"); return; }
        if (!SameContext(current, target)) { strcpy_s(response, size, "ERROR BOOKMARK_WORLD_MISMATCH"); return; }
        auto sourceRegion = Region(currentCoords);
        auto destination = ResolveDestination(sourceRegion, target);
        if (!destination || RegionHex(destination) != target.region)
        { strcpy_s(response, size, "ERROR BOOKMARK_DESTINATION_UNAVAILABLE_TRAVEL_NEARBY_FIRST"); return; }
        if (!loaded(destination) || !finished(destination))
        {
            if (!allowLoad) { strcpy_s(response, size, "ERROR BOOKMARK_DESTINATION_UNAVAILABLE_TRAVEL_NEARBY_FIRST"); return; }
            if (!SurfaceTile(destination)) { strcpy_s(response, size, "ERROR BOOKMARK_STREAMING_SURFACE_ONLY"); return; }
            // Native teleport uses integer GLOBAL XYZ (Game RVA 0x56BD50).
            // Verify the exact same 3-D region lookup it will use; rounding must
            // not cross a tile boundary or select an overlapping interior.
            const int position[3]{static_cast<int>(std::round(target.x)), static_cast<int>(std::round(target.y)), static_cast<int>(std::round(target.z))};
            if (regionPoint(World(sourceRegion), position) != destination)
            { strcpy_s(response, size, "ERROR BOOKMARK_DESTINATION_UNRESOLVED"); return; }
            if (GetTickCount64() > deadline) { strcpy_s(response, size, "ERROR BOOKMARK_EXPIRED_NO_MOVE"); return; }
            pending = { ++nextTravelToken, GetTickCount64(), playerId, game, player, target, current.name, true };
            moved = true; // The game owns travel from this point, including on disconnect.
            travel(game, position[0], position[1], position[2], 0, false);
            sprintf_s(response, size, "OK BOOKMARK LOADING %llu", pending.token);
            return;
        }
        if (underground(destination)) { strcpy_s(response, size, "ERROR BOOKMARK_DESTINATION_UNAVAILABLE_TRAVEL_NEARBY_FIRST"); return; }
        // Preserve live orientation, replace only the position with a freshly
        // resolved region. No persisted WorldCoords or cached pointers are used.
        alignas(16) unsigned char destinationCoords[0x40]; memcpy(destinationCoords, currentCoords, sizeof(destinationCoords));
        float position[3]{target.x, target.y, target.z};
        if (!convert(destinationCoords, position, destination) || Region(destinationCoords) != destination)
        { strcpy_s(response, size, "ERROR BOOKMARK_DESTINATION_UNRESOLVED"); return; }
        Snapshot resolved; Position(destinationCoords, resolved);
        if (std::abs(resolved.x - target.x) > 0.05f || std::abs(resolved.y - target.y) > 0.05f || std::abs(resolved.z - target.z) > 0.05f)
        { strcpy_s(response, size, "ERROR BOOKMARK_DESTINATION_UNRESOLVED"); return; }
        if (GetTickCount64() > deadline) { strcpy_s(response, size, "ERROR BOOKMARK_EXPIRED_NO_MOVE"); return; }
        moved = true; // No retry after this boundary, including an exception.
        teleport(player, destinationCoords);
        Snapshot after; alignas(16) unsigned char afterCoords[0x40]{};
        if (Capture(game, player, after, afterCoords) || MasteryAudit::objectId(player) != expectedPlayerId || !SameContext(after, target) || after.region != target.region ||
            std::abs(after.x - target.x) > 1 || std::abs(after.y - target.y) > 2 || std::abs(after.z - target.z) > 1)
        { strcpy_s(response, size, "ERROR BOOKMARK_MOVE_UNVERIFIED_CHECK_GAME_DO_NOT_RETRY"); return; }
        strcpy_s(response, size, "OK BOOKMARK RETURNED");
    }
    inline void Run(void* game, void* player, const char* command, unsigned long long deadline, char* response, size_t size)
    {
        bool moved = false;
        __try { Execute(game, player, command, deadline, response, size, moved); }
        __except (EXCEPTION_EXECUTE_HANDLER)
        { strcpy_s(response, size, moved ? "ERROR BOOKMARK_MOVE_UNKNOWN_CHECK_GAME_DO_NOT_RETRY" : "ERROR BOOKMARK_READ_FAILED_NO_MOVE"); }
    }
}
