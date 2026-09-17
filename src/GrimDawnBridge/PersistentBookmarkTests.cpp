#include <windows.h>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <cmath>
#include "MasteryAudit.h"
#include "MasteryRespec.h"
#include "PersistentBookmarks.h"

namespace B = PersistentBookmarks;
namespace
{
    unsigned char regions[3][128]{}, playerCoords[64]{};
    void* engine = reinterpret_cast<void*>(1);
    bool loading = false, alive = true, combat = false, single = true, ready = true, under = false, hc = false;
    bool wrongRegion = false, unresolved = false, noMove = false, fault = false;
    bool destinationReady = true, destinationUnderground = false, missingDestination = false;
    bool surfacePath = false, nativeBusy = false, pointMismatch = false, travelFault = false;
    unsigned travelCalls = 0, unloadedUndergroundCalls = 0;
    int travelPosition[3]{};
    int destinationIndex = 1;
    bool checkLookupCoordinates = false;
    float expectedWorldX = 0, expectedWorldZ = 0;
    unsigned diff = 0, mode = 0, moves = 0, playerId = 42;
    std::string mod, zoneRecord = "records/ui/test.dbr", zoneTag = "tagRiftDevilsCrossing";
    void Require(bool good, const char* message) { printf("%s %s\n", good ? "PASS" : "FAIL", message); if (!good) ExitProcess(1); }
    void* __fastcall GetCoords(void*, void* output) { memcpy(output, playerCoords, 64); return output; }
    void __fastcall GetPath(void* region, std::string* output) { *output = wrongRegion ? "wrong.lvl" : region == regions[0] ? "levels/start.lvl" : surfacePath ? "levels/region0a001.lvl" : "levels/target.lvl"; }
    bool __fastcall Convert(void* output, const void* position, void* region)
    {
        if (unresolved) return false;
        int origin[3]; float global[3], local[3];
        memcpy(origin, static_cast<unsigned char*>(region) + 0x3c, sizeof(origin));
        memcpy(global, position, sizeof(global));
        for (int i = 0; i < 3; ++i) local[i] = global[i] - origin[i];
        memcpy(output, &region, sizeof(region)); memcpy(static_cast<char*>(output) + 8, local, sizeof(local)); return true;
    }
    void __fastcall Move(void*, const void* destination)
    {
        ++moves;
        if (fault) RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, nullptr);
        if (!noMove) memcpy(playerCoords, destination, 64);
    }
    std::string Run(const std::string& command, unsigned long long deadline = GetTickCount64() + 5000)
    {
        char response[8192]{}; B::Run(engine, engine, command.c_str(), deadline, response, sizeof(response)); return response;
    }
}
int main()
{
    MasteryAudit::available = B::available = true;
    MasteryAudit::loading = [](void*) { return loading; };
    MasteryAudit::playerName = [](void*) -> const wchar_t* { return playerId == 42 ? L"Test Player" : L"Second Player"; };
    MasteryAudit::objectId = [](void*) { return playerId; };
    MasteryRespec::engineInstance = &engine;
    MasteryRespec::gameInfo = [](void*) { return engine; };
    MasteryRespec::singlePlayer = [](void*) { return single; };
    MasteryRespec::alive = [](void*) { return alive; };
    MasteryRespec::attacked = [](void*) { return combat; };
    B::hardcore = [](void*) { return hc; };
    B::difficulty = [](void*) { return diff; };
    B::teleporting = [](void*) { return false; };
    B::mode = [](void*) { return mode; };
    B::modName = [](void*) -> const std::string& { return mod; };
    B::coords = GetCoords; B::convert = Convert; B::regionPath = GetPath; B::teleport = Move;
    B::loaded = [](void* p) { return ready && (p == regions[0] || destinationReady); }; B::finished = B::loaded;
    B::underground = [](void* p) { if (p != regions[0] && !destinationReady) ++unloadedUndergroundCalls; return under || (p != regions[0] && destinationUnderground); };
    B::worldFile = [](void*) { return "Levels/World001.map"; };
    B::zoneRecord = [](void*) -> const std::string& { return zoneRecord; };
    B::zoneManager = []() -> void* { return engine; };
    B::localization = B::zoneManager;
    B::zoneData = [](void*, const std::string& record) -> const void* { return record == zoneRecord ? &zoneTag : nullptr; };
    B::localize = [](void*, const char* tag) -> const wchar_t* { return strcmp(tag, zoneTag.c_str()) == 0 ? L"Devil's Crossing" : L""; };
    B::regionAt = [](void* world, void* source, float x, float z) -> void* {
        if (missingDestination) return nullptr;
        if (checkLookupCoordinates)
        {
            // Engine GetRegionContainingXZ adds the supplied source region's
            // origin to its X/Z arguments. They must be source-local, not global.
            int origin[3]; memcpy(origin, static_cast<unsigned char*>(source) + 0x3c, sizeof(origin));
            if (world != engine || std::abs(x + origin[0] - expectedWorldX) > 0.001f ||
                std::abs(z + origin[2] - expectedWorldZ) > 0.001f) return nullptr;
        }
        return regions[destinationIndex];
    };
    for (auto& region : regions) memcpy(region + 0x30, &engine, sizeof(engine));
    void* region = regions[0]; memcpy(playerCoords, &region, sizeof(region));
    auto result = Run("READ"); B::Snapshot target;
    const std::string prefix = "OK BOOKMARK 2 42 ";
    Require(result.starts_with(prefix) && B::Parse(result.c_str() + prefix.size(), target) && target.uid == std::string(32, '0') && moves == 0,
        "bookmark capture succeeds without a save ID and keeps live confirmation identity separate");
    const std::string destinationPath = "levels/target.lvl";
    target.region = B::Hex(destinationPath.data(), destinationPath.size()); target.x = 12; target.y = 2; target.z = -8;
    const wchar_t* area = L"Devil's Crossing";
    Require(Run("LABEL " + B::Serialize(target)) == "OK BOOKMARK LABEL " + B::Hex(area, wcslen(area) * sizeof(wchar_t)) && moves == 0,
        "localized destination label is read without moving the player");
    destinationReady = false;
    Require(Run("LABEL " + B::Serialize(target)) == "OK BOOKMARK LABEL -" && moves == 0, "unknown labels never force-load a region");
    destinationReady = true;
    auto localize = B::localize; B::localize = nullptr;
    Require(Run("LABEL " + B::Serialize(target)) == "OK BOOKMARK LABEL -", "missing localization is honest rather than a guessed label");
    B::localize = localize;
    Require(Run("LABEL malformed") == "ERROR BOOKMARK_INVALID_REQUEST" && moves == 0, "malformed label lookup never moves");
    auto command = "RETURN 42 " + B::Serialize(target);
    Require(Run(command) == "OK BOOKMARK RETURNED" && moves == 1, "return resolves loaded destination using a fresh region and verifies coordinates");
    // New runtime region addresses, same saved data: no stale pointer can survive.
    region = regions[0]; memcpy(playerCoords, &region, sizeof(region));
    destinationIndex = 2;
    playerId = 43;
    Require(Run("READ").starts_with("OK BOOKMARK 2 43 "), "another character receives their own live confirmation identity");
    target.uid = std::string(32, 'A'); // A legacy saved character ID is metadata only.
    command = "RETURN 43 " + B::Serialize(target);
    Require(Run(command) == "OK BOOKMARK RETURNED" && moves == 2, "a second character can use a legacy bookmark with freshly resolved region addresses");
    auto expectNoMove = [&](const char* label) { region = regions[0]; memcpy(playerCoords, &region, sizeof(region)); auto before = moves; auto reply = Run(command); Require(reply.starts_with("ERROR BOOKMARK_") && moves == before, label); };
    playerId = 44; expectNoMove("changing character during confirmation is rejected before moving"); playerId = 43;
    Require(Run("RETURN 0 " + B::Serialize(target)) == "ERROR BOOKMARK_INVALID_REQUEST" && moves == 2,
        "missing live confirmation identity cannot move any character");
    for (unsigned savedDifficulty = 0; savedDifficulty < 3; ++savedDifficulty)
    for (unsigned savedHardcore = 0; savedHardcore < 2; ++savedHardcore)
    for (unsigned currentDifficulty = 0; currentDifficulty < 3; ++currentDifficulty)
    for (unsigned currentHardcore = 0; currentHardcore < 2; ++currentHardcore)
    {
        target.diff = savedDifficulty; target.hc = savedHardcore;
        diff = currentDifficulty; hc = currentHardcore != 0;
        region = regions[0]; memcpy(playerCoords, &region, sizeof(region));
        auto before = moves;
        Require(Run("LABEL " + B::Serialize(target)).starts_with("OK BOOKMARK LABEL ") && moves == before,
            "location lookup supports every difficulty and hardcore combination without moving");
        Require(Run("RETURN 43 " + B::Serialize(target)) == "OK BOOKMARK RETURNED" && moves == before + 1,
            "compatible map return supports every saved/current difficulty and hardcore combination");
    }
    // Reproduce the reported saved positions with representative negative,
    // zero and positive map section origins. Never touches a live game.
    const auto originalTarget = target;
    const int origins[][3]{{0,0,0},{64,0,-128},{-448,0,-1152},{512,16,1024}};
    const float savedPositions[][3]{{80.60111f,8.101041f,38.498863f},{-72.41554f,2.546954f,-288.68866f}};
    checkLookupCoordinates = true; destinationIndex = 1;
    for (const auto& sourceOrigin : origins)
    for (unsigned saved = 0; saved < 2; ++saved)
    {
        memcpy(regions[0] + 0x3c, sourceOrigin, sizeof(sourceOrigin));
        memcpy(regions[1] + 0x3c, origins[saved + 1], sizeof(origins[0]));
        region = regions[0]; memcpy(playerCoords, &region, sizeof(region));
        target.x = expectedWorldX = savedPositions[saved][0];
        target.y = savedPositions[saved][1];
        target.z = expectedWorldZ = savedPositions[saved][2];
        const auto before = moves;
        Require(Run("LABEL " + B::Serialize(target)) == "OK BOOKMARK LABEL " + B::Hex(area, wcslen(area) * sizeof(wchar_t)) && moves == before,
            "saved global positions resolve labels from nonzero source origins without movement");
        command = "RETURN 43 " + B::Serialize(target);
        Require(Run(command) == "OK BOOKMARK RETURNED" && moves == before + 1,
            "cross-region bookmark return converts source-local lookup and destination-local coordinates exactly once");
        Require(Run(command) == "OK BOOKMARK RETURNED" && moves == before + 2,
            "same-region repeated return does not add the destination origin twice");
        region = regions[0]; memcpy(playerCoords, &region, sizeof(region));
        destinationReady = false;
        Require(Run(command) == "ERROR BOOKMARK_DESTINATION_UNAVAILABLE_TRAVEL_NEARBY_FIRST" && moves == before + 2,
            "corrected coordinate lookup still refuses an unloaded destination");
        destinationReady = true;
    }
    checkLookupCoordinates = false;
    for (auto& value : regions) memset(value + 0x3c, 0, sizeof(origins[0]));
    target = originalTarget;
    target.diff = 0; target.hc = 0;
    diff = 2; hc = true; // Exercise remaining rejection paths across modes too.
    auto compatibleWorld = target.world;
    const std::string otherWorld = "other/world001.map";
    target.world = B::Hex(otherWorld.data(), otherWorld.size());
    command = "RETURN 43 " + B::Serialize(target);
    expectNoMove("cross-mode return still rejects incompatible campaign worlds");
    Require(Run("LABEL " + B::Serialize(target)) == "OK BOOKMARK LABEL -", "cross-mode label lookup rejects incompatible worlds");
    target.world = compatibleWorld;
    command = "RETURN 43 " + B::Serialize(target);
    const auto successfulMoves = moves;
    single = false; expectNoMove("multiplayer is rejected before moving"); single = true;
    loading = true; expectNoMove("loading player is rejected before moving"); loading = false;
    Require(Run("READ",1)=="ERROR BOOKMARK_EXPIRED_NO_MOVE", "late readiness reads cannot arm hotkeys after a loading pause");
    alive = false; expectNoMove("dead player is rejected before moving"); alive = true;
    combat = true; expectNoMove("combat blocks bookmark travel"); combat = false;
    ready = false; expectNoMove("unloaded area blocks bookmark travel"); ready = true;
    destinationReady = false; expectNoMove("unloaded destination is rejected while source is ready"); destinationReady = true;
    destinationUnderground = true; expectNoMove("underground destination is rejected while source is outdoors"); destinationUnderground = false;
    missingDestination = true; expectNoMove("unresolvable destination is rejected before conversion"); missingDestination = false;
    under = true; expectNoMove("underground areas are excluded"); under = false;
    mod = "custom"; expectNoMove("custom games are excluded"); mod.clear();
    mode = 1; expectNoMove("challenge modes are excluded"); mode = 0;
    wrongRegion = true; expectNoMove("overlapping world coordinates cannot select a different level path"); wrongRegion = false;
    unresolved = true; expectNoMove("coordinate conversion failure does not move"); unresolved = false;
    Require(Run(command, 1) == "ERROR BOOKMARK_EXPIRED_NO_MOVE" && moves == successfulMoves, "expired queued return cannot execute late");
    Require(Run(command + " extra") == "ERROR BOOKMARK_INVALID_REQUEST" && moves == successfulMoves, "extra protocol fields are rejected");
    B::Snapshot parsed;
    Require(!B::Parse("bogus", parsed), "malformed snapshot is rejected");
    target.x = std::numeric_limits<float>::infinity(); Require(!B::Valid(target), "non-finite coordinates are rejected"); target.x = 12;
    // Post-move failure and exception never report success or retry automatically.
    region = regions[0]; memcpy(playerCoords, &region, sizeof(region)); noMove = true;
    Require(Run(command) == "ERROR BOOKMARK_MOVE_UNVERIFIED_CHECK_GAME_DO_NOT_RETRY" && moves == successfulMoves + 1, "unverified movement is reported without retry");
    noMove = false; fault = true;
    Require(Run(command) == "ERROR BOOKMARK_MOVE_UNKNOWN_CHECK_GAME_DO_NOT_RETRY" && moves == successfulMoves + 2, "exception at movement boundary reports unknown outcome without retry");

    // Streaming tests use a fake native activity, never the live game.
    fault = false; destinationReady = false;
    region = regions[0]; memcpy(playerCoords, &region, sizeof(region));
    B::loadingAvailable = true;
    B::activityManager = []() -> void* { return engine; };
    B::activityBusy = [](void*) { return nativeBusy; };
    B::regionPoint = [](void* world, const int* point) -> void* {
        Require(world == engine && point[0] == 12 && point[1] == 2 && point[2] == -8,
            "native 3-D lookup receives rounded global XYZ, not source-local values");
        return pointMismatch ? regions[0] : regions[destinationIndex];
    };
    B::travel = [](void*, int x, int y, int z, int effect, bool option) {
        ++travelCalls; travelPosition[0] = x; travelPosition[1] = y; travelPosition[2] = z;
        Require(effect == 0 && !option, "native travel starts without extra portal effects or flags");
        if (travelFault) RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, nullptr);
        nativeBusy = true;
    };
    auto travelCommand = "TRAVEL 43 " + B::Serialize(target);
    Require(Run(travelCommand) == "ERROR BOOKMARK_STREAMING_SURFACE_ONLY" && travelCalls == 0,
        "unloaded unknown/interior layout cannot start game loading");
    surfacePath = true;
    const std::string surface = "levels/region0a001.lvl";
    target.region = B::Hex(surface.data(), surface.size()); target.x = 12.4f; target.y = 2.2f; target.z = -8.4f;
    travelCommand = "TRAVEL 43 " + B::Serialize(target);
    B::loadingAvailable = false;
    Require(Run(travelCommand) == "ERROR BOOKMARK_LOADING_UNAVAILABLE" && travelCalls == 0, "missing loading APIs fail closed");
    B::loadingAvailable = true;
    Require(Run(travelCommand, 1) == "ERROR BOOKMARK_EXPIRED_NO_MOVE" && travelCalls == 0, "expired loading request cannot start later");
    pointMismatch = true;
    Require(Run(travelCommand) == "ERROR BOOKMARK_DESTINATION_UNRESOLVED" && travelCalls == 0, "rounded 3-D region mismatch blocks loading");
    pointMismatch = false;
    const auto beforeTravel = moves;
    auto begin = Run(travelCommand);
    const auto loadingReply = "OK BOOKMARK LOADING " + std::to_string(B::pending.token);
    const auto statusCommand = "STATUS " + std::to_string(B::pending.token);
    Require(begin == loadingReply && travelCalls == 1 && moves == beforeTravel, "unloaded destination starts exactly one native activity without direct teleport");
    Require(Run(travelCommand) == "ERROR BOOKMARK_TRAVEL_IN_PROGRESS" && travelCalls == 1, "duplicate travel is rejected before another activity starts");
    loading = true;
    Require(Run(statusCommand) == loadingReply && travelCalls == 1 && moves == beforeTravel, "status waits through game loading without movement or resubmission");
    loading = false;
    Require(Run("STATUS 0") == "ERROR BOOKMARK_INVALID_TRAVEL_TOKEN" && Run(statusCommand + " extra") == "ERROR BOOKMARK_INVALID_TRAVEL_TOKEN",
        "invalid or malformed status tokens cannot inspect another operation");
    destinationReady = true; nativeBusy = false;
    float arrived[3]{static_cast<float>(travelPosition[0]), static_cast<float>(travelPosition[1]), static_cast<float>(travelPosition[2])};
    Convert(playerCoords, arrived, regions[destinationIndex]);
    Require(Run(statusCommand) == "OK BOOKMARK RETURNED" && Run(statusCommand) == "OK BOOKMARK RETURNED" && moves == beforeTravel && travelCalls == 1,
        "arrival is verified with stable repeatable read-only completion status");
    Require(Run(travelCommand) == "OK BOOKMARK RETURNED" && moves == beforeTravel + 1 && travelCalls == 1,
        "already-loaded TRAVEL preserves the precise existing return path");
    auto restartTravel = [&]() {
        B::pending = {}; nativeBusy = false; destinationReady = false;
        region = regions[0]; memcpy(playerCoords, &region, sizeof(region));
        auto started = Run(travelCommand);
        Require(started.starts_with("OK BOOKMARK LOADING "), "next deliberate travel can start after the previous activity finishes");
        return "STATUS " + std::to_string(B::pending.token);
    };
    auto poll = restartTravel(); nativeBusy = false; playerId = 44;
    Require(Run(poll) == "ERROR BOOKMARK_TRAVEL_UNKNOWN_CHECK_GAME_DO_NOT_RETRY", "character change during loading cannot report verified arrival"); playerId = 43;
    poll = restartTravel(); B::pending.started = GetTickCount64() - 120001;
    Require(Run(poll) == "ERROR BOOKMARK_TRAVEL_UNKNOWN_CHECK_GAME_DO_NOT_RETRY" && nativeBusy,
        "verification timeout never cancels or alters the game's ongoing travel activity");
    const auto startedCalls = travelCalls;
    Require(Run(travelCommand) == "ERROR BOOKMARK_TRAVEL_IN_PROGRESS" && travelCalls == startedCalls,
        "ongoing native travel blocks further submissions even after verification timeout");
    poll = restartTravel(); nativeBusy = false; destinationReady = true;
    Require(Run(poll) == "ERROR BOOKMARK_TRAVEL_UNKNOWN_CHECK_GAME_DO_NOT_RETRY", "wrong arrival is never reported as success or retried");
    B::pending = {}; destinationReady = false; travelFault = true;
    Require(Run(travelCommand) == "ERROR BOOKMARK_MOVE_UNKNOWN_CHECK_GAME_DO_NOT_RETRY", "exception starting native travel reports unknown outcome without retry");
    Require(unloadedUndergroundCalls == 0, "unloaded destinations never call the implicitly loading underground getter");
}
