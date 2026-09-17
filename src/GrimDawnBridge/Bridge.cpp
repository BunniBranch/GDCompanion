#include <windows.h>
#include <MinHook.h>
#include <array>
#include <atomic>
#include <charconv>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include "MinimapTelemetry.h"
#include "MasteryAudit.h"
#include "MasteryRespec.h"
#include "GameplayAssists.h"
#include "PersistentBookmarks.h"
#include "QuestStageCapture.h"
#include "RadarCanvas.h"
#include "CharacterLevel.h"
#include "CharacterResource.h"
#include "ItemAffixes.h"

namespace
{
    constexpr char kUpdateExport[] = "?Update@LuaManager@GAME@@QEAAXH@Z";
    constexpr char kRunCodeExport[] = "?RunCode@LuaManager@GAME@@QEAA_NPEBD@Z";
    constexpr char kGameEngineExport[] = "?gGameEngine@GAME@@3PEAVGameEngine@1@EA";
    constexpr char kGetMainPlayerExport[] = "?GetMainPlayer@GameEngine@GAME@@QEBAPEAVPlayer@2@XZ";
    constexpr char kGetCoordsExport[] = "?GetCoords@Entity@GAME@@QEBA?AVWorldCoords@2@XZ";
    constexpr char kGetRegionContainingXZExport[] = "?GetRegionContainingXZ@World@GAME@@QEBAPEAVRegion@2@PEAV32@MM@Z";
    constexpr char kSetFromWorldPositionExport[] = "?SetFromWorldPosition@WorldVec3@GAME@@QEAA_NAEBVVec3@2@PEAVRegion@2@@Z";
    constexpr char kGetRegionLoadFileNameExport[] = "?GetLoadFileName@Region@GAME@@QEBA?AV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ";
    constexpr char kGetMoneyExport[] = "?GetCurrentMoney@Character@GAME@@QEBA?BIXZ";
    constexpr char kGetSkillPointsExport[] = "?GetSkillPoints@Character@GAME@@QEBA?BIXZ";
    constexpr char kGetModifierPointsExport[] = "?GetModifierPoints@Character@GAME@@QEBA?BIXZ";
    constexpr char kGetDevotionPointsExport[] = "?GetDevotionPoints@Character@GAME@@QEBA?BIXZ";
    constexpr char kHasTokenExport[] = "?HasToken@Player@GAME@@QEAA_NAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@@Z";
    constexpr char kGetDetailMapDataExport[] = "?GetDetailMapData@GameEngine@GAME@@QEAAXAEAV?$vector@UMinimapGameNugget@GAME@@@mem@@AEBVWorldFrustum@2@@Z";
    constexpr char kAppendAreaMapDataExport[] = "?AppendDetailMapData@AreaOfInterest@GAME@@UEAAXAEAV?$vector@UMinimapGameNugget@GAME@@@mem@@@Z";
    constexpr char kIsMarkerUidKnownExport[] = "?IsMarkerUIDKnown@Player@GAME@@QEBA_NAEBVUniqueId@2@@Z";
    constexpr char kGetQuestsExport[] = "?GetQuests@Quest2Repository@GAME@@QEAAXAEAV?$vector@PEAVQuest2@GAME@@@mem@@W4Filter@12@@Z";
    constexpr char kGetQuestFileNameExport[] = "?GetFileName@Quest2@GAME@@QEBAAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ";
    constexpr char kGetNumTasksExport[] = "?GetNumTasks@Quest2@GAME@@QEBAIXZ";
    constexpr char kGetTaskByIndexExport[] = "?GetTaskByIndex@Quest2@GAME@@QEBAPEAVQuest2Task@2@H@Z";
    constexpr char kGetTaskUidExport[] = "?GetUid@Quest2Task@GAME@@QEBAIXZ";
    constexpr char kTaskInProgressExport[] = "?InProgress@Quest2Task@GAME@@QEBA_N_N@Z";
    constexpr char kAscendantAltarInterestExport[] = "?IsOfInterest@AscendantAltar@GAME@@UEBA_NXZ";
    constexpr char kDynamicTeleporterInterestExport[] = "?IsOfInterest@DynamicTeleporter@GAME@@UEBA_NXZ";
    constexpr char kFixedDoorInterestExport[] = "?IsOfInterest@FixedDoor@GAME@@UEBA_NXZ";
    constexpr char kFixedDoorUpdateExport[] = "?UpdateSelf@FixedDoor@GAME@@UEAAXH@Z";
    constexpr char kFixedDoorDescriptionExport[] = "?GetGameDescription@FixedDoor@GAME@@UEBA?AV?$basic_string@GU?$char_traits@G@std@@V?$allocator@G@2@@std@@_N0@Z";
    constexpr char kFixedItemContainerInterestExport[] = "?IsOfInterest@FixedItemContainer@GAME@@UEBA_NXZ";
    constexpr char kFixedItemShrineInterestExport[] = "?IsOfInterest@FixedItemShrine@GAME@@UEBA_NXZ";
    constexpr char kMonsterShrineInterestExport[] = "?IsOfInterest@MonsterShrine@GAME@@UEBA_NXZ";
    constexpr char kStaticShrineInterestExport[] = "?IsOfInterest@StaticShrine@GAME@@UEBA_NXZ";
    constexpr char kStaticTeleporterInterestExport[] = "?IsOfInterest@StaticTeleporter@GAME@@UEBA_NXZ";
    constexpr char kStaticTeleporterAppendExport[] = "?AppendDetailMapData@StaticTeleporter@GAME@@UEAAXAEAV?$vector@UMinimapGameNugget@GAME@@@mem@@@Z";
    constexpr size_t kMinimapNuggetSize = 0xA0;
    constexpr size_t kCommandSize = 4096;
    constexpr size_t kResponseSize = 8192;
    constexpr size_t kQueueSlots = 64;
    constexpr size_t kWorldMarkerSlots = 512;
    constexpr size_t kActiveQuestSlots = 32;
    constexpr size_t kLiveMapMarkerSlots = 64;
    constexpr size_t kQuestPathSize = 192;
    constexpr size_t kLiveMapLabelSize = 96;
    constexpr size_t kForcedTravelObjectSlots = 32;
    constexpr size_t kForcedTravelMarkerSlots = 16;
    constexpr size_t kObservedFixedDoorSlots = 128;

    enum class WorkKind { Lua, ResourceGet, TokenHas, PlayerPosition, PlayerRegion, MasteryAudit, MasteryRespec, Assists, Bookmark, QuestTasks, CharacterLevel, ResourceSet, ItemAffix };
    enum class ResourceKind { Money, Skill, Attribute, Devotion };

    struct WorkRequest
    {
        WorkKind kind = WorkKind::Lua;
        ResourceKind resource = ResourceKind::Money;
        unsigned int amount = 0;
        unsigned int assistMask = 0, assistSpeed = 100;
        unsigned long long masteryToken = 0, deadline = 0;
        std::array<char, kCommandSize> payload{};
        std::array<char, kResponseSize> response{};
        std::mutex mutex;
        std::condition_variable completedCondition;
        bool completed = false;
    };

    struct WorldMarker
    {
        unsigned int type = 3;
        float x = 0;
        float y = 0;
        float z = 0;
        bool relativeToPlayer = false;
    };

    struct LiveMapMarker
    {
        unsigned int type = 0;
        float x = 0;
        float y = 0;
        float z = 0;
        std::array<char, kLiveMapLabelSize> label{};
    };

    struct FixedDoorObservation
    {
        void* object = nullptr;
        bool isTravelUtility = false;
        unsigned long long checkedAt = 0;
        unsigned long long lastSeen = 0;
        LiveMapMarker marker{};
    };

    using UpdateFunction = void(__fastcall*)(void*, int);
    using RunCodeFunction = bool(__fastcall*)(void*, const char*);
    using GetMainPlayerFunction = void*(__fastcall*)(void*);
    using GetCoordsFunction = void*(__fastcall*)(void*, void*);
    using GetRegionContainingXZFunction = void*(__fastcall*)(void*, void*, float, float);
    using SetFromWorldPositionFunction = bool(__fastcall*)(void*, const void*, void*);
    // Region::GetLoadFileName returns std::string by value. For an MSVC x64
    // member function, RCX remains `this` and the hidden result buffer is RDX.
    using GetRegionLoadFileNameFunction = void(__fastcall*)(void*, std::string*);
    using GetResourceFunction = unsigned int(__fastcall*)(void*);
    using HasTokenFunction = bool(__fastcall*)(void*, const std::string&);
    using GetDetailMapDataFunction = void(__fastcall*)(void*, void*, const void*);
    using AppendAreaMapDataFunction = void(__fastcall*)(void*, void*);
    using IsMarkerUidKnownFunction = bool(__fastcall*)(void*, const void*);
    using GetQuestsFunction = void(__fastcall*)(void*, void*, unsigned int);
    using GetQuestFileNameFunction = const std::string&(__fastcall*)(void*);
    using GetTaskByIndexFunction = void*(__fastcall*)(void*, int);
    using TaskInProgressFunction = bool(__fastcall*)(void*, bool);
    using AppendQuestMapDataFunction = bool(__fastcall*)(void*, const void*, const void*, void*);
    using IsOfInterestFunction = bool(__fastcall*)(void*);
    using FixedDoorUpdateFunction = void(__fastcall*)(void*, int);
    // MSVC places a member-function's hidden std::wstring return buffer after
    // `this`: RCX=object, RDX=result, R8/R9=the two bool arguments. Declaring
    // this export as a free function returning std::wstring reverses those
    // first two registers and corrupts the FixedDoor object.
    using GetGameDescriptionFunction = void(__fastcall*)(void*, std::wstring*, bool, bool);
    using VectorAppendFunction = void(__fastcall*)(void*, const void*);

    struct RawVectorView
    {
        unsigned char* begin;
        unsigned char* end;
        unsigned char* capacity;
    };

    UpdateFunction g_originalUpdate = nullptr;
    RunCodeFunction g_runCode = nullptr;
    HMODULE g_bridgeModule = nullptr;
    void* g_updateTarget = nullptr;
    void** g_gameEngine = nullptr;
    GetMainPlayerFunction g_getMainPlayer = nullptr;
    GetCoordsFunction g_getCoords = nullptr;
    GetRegionContainingXZFunction g_getRegionContainingXZ = nullptr;
    SetFromWorldPositionFunction g_setFromWorldPosition = nullptr;
    GetRegionLoadFileNameFunction g_getRegionLoadFileName = nullptr;
    std::array<GetResourceFunction, 4> g_getResource{};
    HasTokenFunction g_hasToken = nullptr;
    GetDetailMapDataFunction g_originalGetDetailMapData = nullptr;
    AppendAreaMapDataFunction g_originalAppendAreaMapData = nullptr;
    IsMarkerUidKnownFunction g_originalIsMarkerUidKnown = nullptr;
    GetQuestsFunction g_originalGetQuests = nullptr;
    GetQuestFileNameFunction g_getQuestFileName = nullptr;
    GetResourceFunction g_getNumTasks = nullptr, g_getTaskUid = nullptr;
    GetTaskByIndexFunction g_getTaskByIndex = nullptr;
    TaskInProgressFunction g_taskInProgress = nullptr;
    bool g_questTasksAvailable = false;
    void** g_questRepository = nullptr;
    bool (*g_questTracked)(void*) = nullptr;
    AppendQuestMapDataFunction g_originalAppendQuestMapData = nullptr;
    std::array<IsOfInterestFunction, 8> g_originalPoiInterest{};
    GetGameDescriptionFunction g_getFixedDoorDescription = nullptr;
    FixedDoorUpdateFunction g_originalFixedDoorUpdate = nullptr;
    void* g_getDetailMapDataTarget = nullptr;
    void* g_appendAreaMapDataTarget = nullptr;
    void* g_isMarkerUidKnownTarget = nullptr;
    void* g_getQuestsTarget = nullptr;
    void* g_appendQuestMapDataTarget = nullptr;
    std::array<void*, 8> g_poiInterestTargets{};
    void* g_fixedDoorUpdateTarget = nullptr;
    void* g_staticTeleporterAppendTarget = nullptr;
    VectorAppendFunction g_vectorAppend = nullptr;
    CRITICAL_SECTION g_queueLock{};
    SRWLOCK g_worldMarkerLock = SRWLOCK_INIT;
    SRWLOCK g_activeQuestLock = SRWLOCK_INIT;
    SRWLOCK g_liveMapMarkerLock = SRWLOCK_INIT;
    SRWLOCK g_verifiedQuestMarkerLock = SRWLOCK_INIT;
    SRWLOCK g_forcedTravelLock = SRWLOCK_INIT;
    std::array<std::shared_ptr<WorkRequest>, kQueueSlots> g_queue{};
    size_t g_readIndex = 0;
    size_t g_writeIndex = 0;
    size_t g_queueCount = 0;
    std::array<WorldMarker, kWorldMarkerSlots> g_worldMarkers{};
    size_t g_worldMarkerCount = 0;
    std::array<std::array<char, kQuestPathSize>, kActiveQuestSlots> g_activeQuestPaths{};
    size_t g_activeQuestCount = 0;
    std::string g_questTaskSnapshot;
    unsigned long long g_questTaskSnapshotTime = 0;
    std::array<LiveMapMarker, kLiveMapMarkerSlots> g_liveMapMarkers{};
    size_t g_liveMapMarkerCount = 0;
    std::array<LiveMapMarker, kLiveMapMarkerSlots> g_verifiedQuestMarkers{};
    size_t g_verifiedQuestMarkerCount = 0;
    std::atomic<bool> g_ready{false};
    std::atomic<bool> g_shutdownRequested{false};
    std::atomic<DWORD> g_error{0};
    std::atomic<unsigned int> g_capabilities{0};
    std::atomic<bool> g_showActiveQuestMarkers{false};
    std::atomic<bool> g_showPointOfInterestMarkers{false};
    std::atomic<bool> g_captureActiveQuests{false};
    std::atomic<bool> g_capturePointOfInterestMarkers{false};
    std::atomic<unsigned long long> g_mapPassCount{0};
    std::atomic<unsigned long long> g_areaAppendCount{0};
    std::atomic<unsigned long long> g_areaNuggetCount{0};
    std::atomic<unsigned long long> g_lastMapNuggetCount{0};
    std::atomic<unsigned long long> g_markerKnownCallCount{0};
    std::atomic<unsigned long long> g_markerKnownOverrideCount{0};
    std::atomic<unsigned long long> g_questListCallCount{0};
    std::atomic<unsigned long long> g_questFilterOverrideCount{0};
    std::atomic<unsigned long long> g_lastQuestCount{0};
    std::atomic<unsigned long long> g_questMarkerCallCount{0};
    std::atomic<unsigned long long> g_questMarkerExpandedCount{0};
    std::atomic<unsigned long long> g_questMarkerAppendCount{0};
    std::atomic<unsigned long long> g_poiInterestOverrideCount{0};
    std::array<std::atomic<unsigned long long>, 7> g_lastMapTypeCounts{};
    std::atomic<unsigned long long> g_syntheticMarkerCount{0};
    std::atomic<unsigned int> g_mapCollectionDepth{0};
    std::atomic<unsigned long long> g_mapCollectionDeadline{0};
    std::array<void*, kForcedTravelObjectSlots> g_forcedTravelObjects{};
    size_t g_forcedTravelObjectCount = 0;
    std::array<LiveMapMarker, kForcedTravelMarkerSlots> g_forcedTravelMarkers{};
    size_t g_forcedTravelMarkerCount = 0;
    std::array<FixedDoorObservation, kObservedFixedDoorSlots> g_fixedDoorObservations{};
    std::atomic<unsigned long long> g_travelScanDeadline{0};
    unsigned int g_areaVisibilityFlagOffset = 0;
    unsigned int g_questMarkerDistanceOffset = 0;

    bool IsCollectingMapData()
    {
        return g_mapCollectionDepth.load(std::memory_order_acquire) != 0 ||
               GetTickCount64() <= g_mapCollectionDeadline.load(std::memory_order_acquire);
    }

    bool TryReadGlobalMapPosition(const unsigned char* nugget, LiveMapMarker& marker)
    {
        if (!nugget) return false;
        const auto coordinates = nugget + 0x58;
        void* region = nullptr;
        memcpy(&region, coordinates, sizeof(region));
        if (!region) return false;
        memcpy(&marker.x, coordinates + 0x08, sizeof(float));
        memcpy(&marker.y, coordinates + 0x0c, sizeof(float));
        memcpy(&marker.z, coordinates + 0x10, sizeof(float));
        int offsetX = 0, offsetY = 0, offsetZ = 0;
        memcpy(&offsetX, static_cast<unsigned char*>(region) + 0x3c, sizeof(offsetX));
        memcpy(&offsetY, static_cast<unsigned char*>(region) + 0x40, sizeof(offsetY));
        memcpy(&offsetZ, static_cast<unsigned char*>(region) + 0x44, sizeof(offsetZ));
        marker.x += static_cast<float>(offsetX);
        marker.y += static_cast<float>(offsetY);
        marker.z += static_cast<float>(offsetZ);
        return std::isfinite(marker.x) && std::isfinite(marker.y) && std::isfinite(marker.z);
    }

    bool TryReadWorldCoords(const void* coordinates, LiveMapMarker& marker)
    {
        if (!coordinates) return false;
        const auto bytes = static_cast<const unsigned char*>(coordinates);
        void* region = nullptr;
        memcpy(&region, bytes, sizeof(region));
        if (!region) return false;
        memcpy(&marker.x, bytes + 0x08, sizeof(float));
        memcpy(&marker.y, bytes + 0x0c, sizeof(float));
        memcpy(&marker.z, bytes + 0x10, sizeof(float));
        int offsetX = 0, offsetY = 0, offsetZ = 0;
        memcpy(&offsetX, static_cast<unsigned char*>(region) + 0x3c, sizeof(offsetX));
        memcpy(&offsetY, static_cast<unsigned char*>(region) + 0x40, sizeof(offsetY));
        memcpy(&offsetZ, static_cast<unsigned char*>(region) + 0x44, sizeof(offsetZ));
        marker.x += static_cast<float>(offsetX);
        marker.y += static_cast<float>(offsetY);
        marker.z += static_cast<float>(offsetZ);
        return std::isfinite(marker.x) && std::isfinite(marker.y) && std::isfinite(marker.z);
    }

    bool TryReadEntityGlobalPosition(void* entity, LiveMapMarker& marker)
    {
        if (!entity || !g_getCoords) return false;
        alignas(16) std::array<unsigned char, 0x40> coordinates{};
        g_getCoords(entity, coordinates.data());
        return TryReadWorldCoords(coordinates.data(), marker);
    }

    void ReadWideStringLabel(const void* stringValue, LiveMapMarker& marker)
    {
        const auto value = static_cast<const unsigned char*>(stringValue);
        if (!value) return;
        size_t length = 0, capacity = 0;
        memcpy(&length, value + 16, sizeof(length));
        memcpy(&capacity, value + 24, sizeof(capacity));
        if (length == 0 || length > 256) return;
        const wchar_t* characters = nullptr;
        if (capacity >= 8) memcpy(&characters, value, sizeof(characters));
        else characters = reinterpret_cast<const wchar_t*>(value);
        if (!characters) return;
        const auto converted = WideCharToMultiByte(CP_UTF8, 0, characters, static_cast<int>(length),
            marker.label.data(), static_cast<int>(marker.label.size() - 1), nullptr, nullptr);
        if (converted <= 0) { marker.label[0] = '\0'; return; }
        marker.label[static_cast<size_t>(converted)] = '\0';
        for (auto& character : marker.label)
            if (character == '\t' || character == '\r' || character == '\n' || character == ',') character = ' ';
    }

    void ReadLiveMapLabel(const unsigned char* nugget, LiveMapMarker& marker)
    {
        ReadWideStringLabel(nugget + 0x10, marker);
    }

    void AddVerifiedQuestMarker(const LiveMapMarker& marker)
    {
        AcquireSRWLockExclusive(&g_verifiedQuestMarkerLock);
        bool duplicate = false;
        for (size_t index = 0; index < g_verifiedQuestMarkerCount; ++index)
        {
            const auto dx = g_verifiedQuestMarkers[index].x - marker.x;
            const auto dz = g_verifiedQuestMarkers[index].z - marker.z;
            if (dx * dx + dz * dz < 0.0625f) { duplicate = true; break; }
        }
        if (!duplicate && g_verifiedQuestMarkerCount < g_verifiedQuestMarkers.size())
            g_verifiedQuestMarkers[g_verifiedQuestMarkerCount++] = marker;
        ReleaseSRWLockExclusive(&g_verifiedQuestMarkerLock);
    }

    void CaptureLiveMapMarkers(const RawVectorView* vector)
    {
        std::array<LiveMapMarker, kLiveMapMarkerSlots> captured{};
        size_t capturedCount = 0;
        if (g_captureActiveQuests.load(std::memory_order_relaxed))
        {
            AcquireSRWLockShared(&g_verifiedQuestMarkerLock);
            for (size_t index = 0; index < g_verifiedQuestMarkerCount && capturedCount < captured.size(); ++index)
                captured[capturedCount++] = g_verifiedQuestMarkers[index];
            ReleaseSRWLockShared(&g_verifiedQuestMarkerLock);
        }
        if (g_capturePointOfInterestMarkers.load(std::memory_order_relaxed))
        {
            AcquireSRWLockShared(&g_forcedTravelLock);
            for (size_t index = 0; index < g_forcedTravelMarkerCount && capturedCount < captured.size(); ++index)
                captured[capturedCount++] = g_forcedTravelMarkers[index];
            const auto now = GetTickCount64();
            for (const auto& observation : g_fixedDoorObservations)
            {
                if (!observation.object || !observation.isTravelUtility ||
                    now - observation.lastSeen > 3000 || capturedCount >= captured.size()) continue;
                captured[capturedCount++] = observation.marker;
            }
            ReleaseSRWLockShared(&g_forcedTravelLock);
        }
        if (vector && vector->begin && vector->end >= vector->begin)
        {
            const auto count = static_cast<size_t>(vector->end - vector->begin) / kMinimapNuggetSize;
            if (count <= 100000)
            {
                for (size_t index = 0; index < count && capturedCount < captured.size(); ++index)
                {
                    const auto nugget = vector->begin + index * kMinimapNuggetSize;
                    LiveMapMarker marker{};
                    memcpy(&marker.type, nugget + 8, sizeof(marker.type));
                    // Area names and the player arrow are not points of interest.
                    if (marker.type == 0 || marker.type == 6 || marker.type == 12) continue;
                    // Types 8 and 14 are generic gold-map glyphs, not proof of
                    // an active quest. Verified quest markers are captured at
                    // the quest builder itself and emitted as companion type 21.
                    if (marker.type == 8 || marker.type == 14) continue;
                    if (!g_capturePointOfInterestMarkers.load(std::memory_order_relaxed)) continue;
                    if (!TryReadGlobalMapPosition(nugget, marker)) continue;
                    ReadLiveMapLabel(nugget, marker);
                    bool duplicate = false;
                    for (size_t existing = 0; existing < capturedCount; ++existing)
                    {
                        const auto dx = captured[existing].x - marker.x;
                        const auto dz = captured[existing].z - marker.z;
                        if (captured[existing].type == marker.type && dx * dx + dz * dz < 0.0625f)
                        { duplicate = true; break; }
                    }
                    if (!duplicate) captured[capturedCount++] = marker;
                }
            }
        }
        AcquireSRWLockExclusive(&g_liveMapMarkerLock);
        g_liveMapMarkers = captured;
        g_liveMapMarkerCount = capturedCount;
        ReleaseSRWLockExclusive(&g_liveMapMarkerLock);
    }

    void __fastcall HookedGetDetailMapData(void* engine, void* output, const void* frustum)
    {
        const bool collect = g_showActiveQuestMarkers.load(std::memory_order_relaxed) ||
                             g_showPointOfInterestMarkers.load(std::memory_order_relaxed) ||
                             g_captureActiveQuests.load(std::memory_order_relaxed) ||
                             g_capturePointOfInterestMarkers.load(std::memory_order_relaxed);
        if (collect)
        {
            g_mapCollectionDeadline.store(GetTickCount64() + 250, std::memory_order_release);
            if (g_capturePointOfInterestMarkers.load(std::memory_order_relaxed))
                g_travelScanDeadline.store(GetTickCount64() + 500, std::memory_order_release);
            const auto previousDepth = g_mapCollectionDepth.fetch_add(1, std::memory_order_acq_rel);
            g_mapPassCount.fetch_add(1, std::memory_order_relaxed);
            if (previousDepth == 0)
            {
                AcquireSRWLockExclusive(&g_verifiedQuestMarkerLock);
                g_verifiedQuestMarkerCount = 0;
                ReleaseSRWLockExclusive(&g_verifiedQuestMarkerLock);
                AcquireSRWLockExclusive(&g_forcedTravelLock);
                g_forcedTravelObjectCount = 0;
                g_forcedTravelMarkerCount = 0;
                ReleaseSRWLockExclusive(&g_forcedTravelLock);
            }
        }
        g_originalGetDetailMapData(engine, output, frustum);
        if (collect && output)
        {
            auto vector = static_cast<RawVectorView*>(output);
            CaptureLiveMapMarkers(vector);
            std::array<WorldMarker, kWorldMarkerSlots> suppliedMarkers{};
            size_t suppliedMarkerCount = 0;
            if (g_showPointOfInterestMarkers.load(std::memory_order_relaxed))
            {
                AcquireSRWLockShared(&g_worldMarkerLock);
                suppliedMarkerCount = g_worldMarkerCount;
                if (suppliedMarkerCount > 0)
                    memcpy(suppliedMarkers.data(), g_worldMarkers.data(), suppliedMarkerCount * sizeof(WorldMarker));
                ReleaseSRWLockShared(&g_worldMarkerLock);
            }
            if (suppliedMarkerCount > 0 && g_vectorAppend &&
                vector->begin && vector->end >= vector->begin)
            {
                alignas(16) std::array<unsigned char, 0x40> playerCoordinates{};
                bool havePlayerCoordinates = false;
                if (g_gameEngine && *g_gameEngine && g_getMainPlayer && g_getCoords)
                {
                    if (auto player = g_getMainPlayer(*g_gameEngine))
                    {
                        g_getCoords(player, playerCoordinates.data());
                        havePlayerCoordinates = true;
                    }
                }
                const auto initialCount = static_cast<unsigned long long>(vector->end - vector->begin) / kMinimapNuggetSize;
                unsigned long long sourceIndex = initialCount;
                for (unsigned long long index = 0; index < initialCount; ++index)
                {
                    auto source = vector->begin + index * kMinimapNuggetSize;
                    unsigned int type = 0;
                    memcpy(&type, source + 8, sizeof(type));
                    if (type == 3) { sourceIndex = index; break; }
                }
                if (sourceIndex < initialCount)
                {
                    for (size_t supplied = 0; supplied < suppliedMarkerCount; ++supplied)
                    {
                        alignas(8) std::array<unsigned char, 0x18> worldOrigin{};
                        bool positioned = false;
                        if (suppliedMarkers[supplied].relativeToPlayer && havePlayerCoordinates)
                        {
                            memcpy(worldOrigin.data(), playerCoordinates.data(), worldOrigin.size());
                            float x = 0, y = 0, z = 0;
                            memcpy(&x, worldOrigin.data() + 0x08, sizeof(float));
                            memcpy(&y, worldOrigin.data() + 0x0c, sizeof(float));
                            memcpy(&z, worldOrigin.data() + 0x10, sizeof(float));
                            x += suppliedMarkers[supplied].x;
                            y += suppliedMarkers[supplied].y;
                            z += suppliedMarkers[supplied].z;
                            memcpy(worldOrigin.data() + 0x08, &x, sizeof(float));
                            memcpy(worldOrigin.data() + 0x0c, &y, sizeof(float));
                            memcpy(worldOrigin.data() + 0x10, &z, sizeof(float));
                            positioned = true;
                        }
                        else if (havePlayerCoordinates && g_getRegionContainingXZ && g_setFromWorldPosition)
                        {
                            void* currentRegion = nullptr;
                            memcpy(&currentRegion, playerCoordinates.data(), sizeof(currentRegion));
                            void* world = nullptr;
                            if (currentRegion)
                                memcpy(&world, static_cast<unsigned char*>(currentRegion) + 0x30, sizeof(world));
                            if (world)
                            {
                                auto region = g_getRegionContainingXZ(world, currentRegion,
                                    suppliedMarkers[supplied].x, suppliedMarkers[supplied].z);
                                if (region)
                                {
                                    const std::array<float, 3> globalPosition{
                                        suppliedMarkers[supplied].x, suppliedMarkers[supplied].y, suppliedMarkers[supplied].z };
                                    positioned = g_setFromWorldPosition(worldOrigin.data(), globalPosition.data(), region);
                                }
                            }
                        }
                        if (!positioned) continue;

                        vector = static_cast<RawVectorView*>(output);
                        auto source = vector->begin + sourceIndex * kMinimapNuggetSize;
                        g_vectorAppend(output, source);
                        vector = static_cast<RawVectorView*>(output);
                        if (!vector->begin || vector->end < vector->begin + kMinimapNuggetSize) break;
                        auto marker = vector->end - kMinimapNuggetSize;
                        memcpy(marker + 0x58, worldOrigin.data(), worldOrigin.size());
                        memcpy(marker + 0x70, playerCoordinates.data() + 0x30, 0x0c);

                        auto label = marker + 0x10;
                        size_t capacity = 0;
                        memcpy(&capacity, label + 24, sizeof(capacity));
                        auto characters = capacity >= 8 ? *reinterpret_cast<wchar_t**>(label) : reinterpret_cast<wchar_t*>(label);
                        if (characters) *characters = L'\0';
                        constexpr size_t empty = 0;
                        memcpy(label + 16, &empty, sizeof(empty));
                        g_syntheticMarkerCount.fetch_add(1, std::memory_order_relaxed);
                    }
                }
            }
            const auto bytes = vector->begin && vector->end >= vector->begin
                ? static_cast<unsigned long long>(vector->end - vector->begin) : 0;
            const auto count = bytes / kMinimapNuggetSize;
            g_lastMapNuggetCount.store(count, std::memory_order_relaxed);

            // Map nugget kind is the 32-bit field at +8. Keep a compact
            // histogram for the concrete navigation records we care about:
            // teleporter, item shrine, area label, player, quest marker,
            // static shrine, and monster shrine.
            constexpr std::array<unsigned int, 7> trackedTypes{3, 5, 6, 12, 14, 16, 19};
            std::array<unsigned long long, trackedTypes.size()> counts{};
            if (vector->begin && count <= 100000)
            {
                for (unsigned long long index = 0; index < count; ++index)
                {
                    unsigned int type = 0;
                    memcpy(&type, vector->begin + index * kMinimapNuggetSize + 8, sizeof(type));
                    for (size_t tracked = 0; tracked < trackedTypes.size(); ++tracked)
                        if (type == trackedTypes[tracked]) { ++counts[tracked]; break; }
                }
            }
            for (size_t index = 0; index < counts.size(); ++index)
                g_lastMapTypeCounts[index].store(counts[index], std::memory_order_relaxed);
        }
        if (collect) g_mapCollectionDepth.fetch_sub(1, std::memory_order_acq_rel);
    }

    void __fastcall HookedAppendAreaMapData(void* area, void* output)
    {
        if (!IsCollectingMapData() || !g_showPointOfInterestMarkers.load(std::memory_order_relaxed) ||
            !area || g_areaVisibilityFlagOffset == 0)
        {
            g_originalAppendAreaMapData(area, output);
            return;
        }
        bool forcedTravel = false;
        AcquireSRWLockShared(&g_forcedTravelLock);
        for (size_t index = 0; index < g_forcedTravelObjectCount; ++index)
            if (g_forcedTravelObjects[index] == area) { forcedTravel = true; break; }
        ReleaseSRWLockShared(&g_forcedTravelLock);
        const auto beforeVector = static_cast<const RawVectorView*>(output);
        const auto beforeCount = beforeVector && beforeVector->begin && beforeVector->end >= beforeVector->begin
            ? static_cast<size_t>(beforeVector->end - beforeVector->begin) / kMinimapNuggetSize : 0;
        auto visibilityFlag = reinterpret_cast<unsigned char*>(area) + g_areaVisibilityFlagOffset;
        const auto previous = *visibilityFlag;
        *visibilityFlag = 0;
        g_areaAppendCount.fetch_add(1, std::memory_order_relaxed);
        g_originalAppendAreaMapData(area, output);
        *visibilityFlag = previous;
        const auto afterVector = static_cast<const RawVectorView*>(output);
        const auto afterCount = afterVector && afterVector->begin && afterVector->end >= afterVector->begin
            ? static_cast<size_t>(afterVector->end - afterVector->begin) / kMinimapNuggetSize : 0;
        if (afterCount > beforeCount)
            g_areaNuggetCount.fetch_add(afterCount - beforeCount, std::memory_order_relaxed);
        if (forcedTravel && g_capturePointOfInterestMarkers.load(std::memory_order_relaxed) &&
            afterVector && afterVector->begin && afterCount > beforeCount)
        {
            AcquireSRWLockExclusive(&g_forcedTravelLock);
            for (size_t index = beforeCount; index < afterCount && g_forcedTravelMarkerCount < g_forcedTravelMarkers.size(); ++index)
            {
                LiveMapMarker marker{};
                marker.type = 20; // Companion-only travel utility; never written back into the game's vector.
                const auto nugget = afterVector->begin + index * kMinimapNuggetSize;
                if (!TryReadGlobalMapPosition(nugget, marker)) continue;
                ReadLiveMapLabel(nugget, marker);
                g_forcedTravelMarkers[g_forcedTravelMarkerCount++] = marker;
            }
            ReleaseSRWLockExclusive(&g_forcedTravelLock);
        }
    }

    bool __fastcall HookedIsMarkerUidKnown(void* player, const void* markerId)
    {
        g_markerKnownCallCount.fetch_add(1, std::memory_order_relaxed);
        if (IsCollectingMapData() && g_showPointOfInterestMarkers.load(std::memory_order_relaxed))
        {
            g_markerKnownOverrideCount.fetch_add(1, std::memory_order_relaxed);
            return true;
        }
        return g_originalIsMarkerUidKnown(player, markerId);
    }

    void __fastcall HookedGetQuests(void* repository, void* output, unsigned int filter)
    {
        g_questListCallCount.fetch_add(1, std::memory_order_relaxed);
        // Preserve Grim Dawn's tracked-only filter. Replacing it with the
        // broader in-progress list can expose dormant objectives from quests
        // that are not on the player's active HUD, producing convincing but
        // incorrect long-range stars. Distance expansion happens later, only
        // for markers the game's own tracked-quest query selected.
        g_originalGetQuests(repository, output, filter);
        // Capture task state only from the game's tracked list, on its own
        // thread. No retained Quest/Task pointers and no quest mutations.
        if (output && filter == 4 && g_captureActiveQuests.load(std::memory_order_relaxed) && g_questTasksAvailable)
        {
            const auto vector = static_cast<const RawVectorView*>(output);
            std::string snapshot;
            bool complete = QuestStageCapture::Read(vector->begin, vector->end,
                {g_getQuestFileName, g_getNumTasks, g_getTaskByIndex, g_getTaskUid, g_taskInProgress}, snapshot);
            AcquireSRWLockExclusive(&g_activeQuestLock);
            complete = complete && g_captureActiveQuests.load(std::memory_order_relaxed);
            g_questTaskSnapshot = complete ? snapshot : "";
            g_questTaskSnapshotTime = complete ? GetTickCount64() : 0;
            ReleaseSRWLockExclusive(&g_activeQuestLock);
        }
        if (output)
        {
            const auto vector = static_cast<const RawVectorView*>(output);
            const auto bytes = vector->begin && vector->end >= vector->begin
                ? static_cast<unsigned long long>(vector->end - vector->begin) : 0;
            g_lastQuestCount.store(bytes / sizeof(void*), std::memory_order_relaxed);
            // The external radar only observes game-owned results. Filter 1 is
            // the active list and filter 4 is the game's tracked subset. No
            // filter or quest object is changed for radar capture.
            if (g_captureActiveQuests.load(std::memory_order_relaxed) && (filter == 1 || filter == 4) && g_getQuestFileName &&
                vector->begin && vector->end >= vector->begin)
            {
                std::array<std::array<char, kQuestPathSize>, kActiveQuestSlots> paths{};
                size_t count = 0;
                const auto questCount = static_cast<size_t>(vector->end - vector->begin) / sizeof(void*);
                for (size_t index = 0; index < questCount && count < paths.size(); ++index)
                {
                    void* quest = nullptr;
                    memcpy(&quest, vector->begin + index * sizeof(void*), sizeof(quest));
                    if (!quest) continue;
                    const auto& path = g_getQuestFileName(quest);
                    if (path.empty() || path.size() >= kQuestPathSize) continue;
                    bool duplicate = false;
                    for (size_t existing = 0; existing < count; ++existing)
                        if (_stricmp(paths[existing].data(), path.c_str()) == 0) { duplicate = true; break; }
                    if (duplicate) continue;
                    strcpy_s(paths[count].data(), paths[count].size(), path.c_str());
                    ++count;
                }
                AcquireSRWLockExclusive(&g_activeQuestLock);
                g_activeQuestPaths = paths;
                g_activeQuestCount = count;
                ReleaseSRWLockExclusive(&g_activeQuestLock);
            }
        }
    }

    bool __fastcall HookedAppendQuestMapData(void* source, const void* coords, const void* name, void* output)
    {
        g_questMarkerCallCount.fetch_add(1, std::memory_order_relaxed);
        if (!IsCollectingMapData() || !g_showActiveQuestMarkers.load(std::memory_order_relaxed) ||
            !source || g_questMarkerDistanceOffset == 0)
            return g_originalAppendQuestMapData(source, coords, name, output);

        const auto beforeVector = static_cast<const RawVectorView*>(output);
        const auto beforeCount = beforeVector && beforeVector->begin && beforeVector->end >= beforeVector->begin
            ? static_cast<size_t>(beforeVector->end - beforeVector->begin) / kMinimapNuggetSize : 0;

        // Quest marker sources carry a squared visibility distance. Lift the
        // threshold to the largest finite value for this call, then restore it
        // immediately so no quest or save state is changed.
        auto distance = reinterpret_cast<float*>(static_cast<unsigned char*>(source) + g_questMarkerDistanceOffset);
        const auto previous = *distance;
        *distance = std::numeric_limits<float>::max();
        g_questMarkerExpandedCount.fetch_add(1, std::memory_order_relaxed);
        const bool result = g_originalAppendQuestMapData(source, coords, name, output);
        *distance = previous;
        const auto afterVector = static_cast<const RawVectorView*>(output);
        const auto afterCount = afterVector && afterVector->begin && afterVector->end >= afterVector->begin
            ? static_cast<size_t>(afterVector->end - afterVector->begin) / kMinimapNuggetSize : 0;
        if (afterCount > beforeCount)
        {
            g_questMarkerAppendCount.fetch_add(afterCount - beforeCount, std::memory_order_relaxed);
            if (g_captureActiveQuests.load(std::memory_order_relaxed))
            {
                // Read the newly emitted minimap nugget rather than assuming a
                // private layout for the builder's coordinate argument. The
                // call site proves this record came from an active quest, and
                // the nugget contains the game's normalized global position.
                for (size_t index = beforeCount; index < afterCount; ++index)
                {
                    LiveMapMarker marker{};
                    marker.type = 21; // Companion-only verified active quest marker.
                    const auto nugget = afterVector->begin + index * kMinimapNuggetSize;
                    if (!TryReadGlobalMapPosition(nugget, marker)) continue;
                    ReadLiveMapLabel(nugget, marker);
                    if (marker.label[0] == '\0') ReadWideStringLabel(name, marker);
                    AddVerifiedQuestMarker(marker);
                }
            }
        }
        return result;
    }

    bool RevealPoiInterest()
    {
        if (!IsCollectingMapData() || !g_showPointOfInterestMarkers.load(std::memory_order_relaxed)) return false;
        g_poiInterestOverrideCount.fetch_add(1, std::memory_order_relaxed);
        return true;
    }

    bool __fastcall HookedPoiInterest0(void* object) { return RevealPoiInterest() || g_originalPoiInterest[0](object); }
    bool __fastcall HookedPoiInterest1(void* object) { return RevealPoiInterest() || g_originalPoiInterest[1](object); }
    bool __fastcall HookedPoiInterest2(void* object) { return RevealPoiInterest() || g_originalPoiInterest[2](object); }
    bool __fastcall HookedPoiInterest3(void* object) { return RevealPoiInterest() || g_originalPoiInterest[3](object); }
    bool __fastcall HookedPoiInterest4(void* object) { return RevealPoiInterest() || g_originalPoiInterest[4](object); }
    bool __fastcall HookedPoiInterest5(void* object) { return RevealPoiInterest() || g_originalPoiInterest[5](object); }
    bool __fastcall HookedPoiInterest6(void* object) { return RevealPoiInterest() || g_originalPoiInterest[6](object); }

    bool __fastcall HookedPoiInterest7(void* object)
    {
        const bool originallyInteresting = g_originalPoiInterest[7](object);
        if (!IsCollectingMapData() || !g_showPointOfInterestMarkers.load(std::memory_order_relaxed) ||
            !object || !g_getFixedDoorDescription) return originallyInteresting;
        std::wstring description;
        g_getFixedDoorDescription(object, &description, false, false);
        if (description.find(L"Boat") == std::wstring::npos && description.find(L"Ferry") == std::wstring::npos)
            return originallyInteresting;
        bool known = false;
        AcquireSRWLockExclusive(&g_forcedTravelLock);
        for (size_t index = 0; index < g_forcedTravelObjectCount; ++index)
            if (g_forcedTravelObjects[index] == object) { known = true; break; }
        if (!known && g_forcedTravelObjectCount < g_forcedTravelObjects.size())
            g_forcedTravelObjects[g_forcedTravelObjectCount++] = object;
        ReleaseSRWLockExclusive(&g_forcedTravelLock);
        g_poiInterestOverrideCount.fetch_add(1, std::memory_order_relaxed);
        return true;
    }

    void __fastcall HookedFixedDoorUpdate(void* object, int argument)
    {
        g_originalFixedDoorUpdate(object, argument);
        if (!object || !g_getFixedDoorDescription || !g_getCoords ||
            !g_capturePointOfInterestMarkers.load(std::memory_order_relaxed) ||
            GetTickCount64() > g_travelScanDeadline.load(std::memory_order_acquire)) return;

        const auto now = GetTickCount64();
        size_t existingIndex = g_fixedDoorObservations.size();
        bool knownTravelUtility = false;
        unsigned long long checkedAt = 0;
        AcquireSRWLockShared(&g_forcedTravelLock);
        for (size_t index = 0; index < g_fixedDoorObservations.size(); ++index)
        {
            if (g_fixedDoorObservations[index].object != object) continue;
            existingIndex = index;
            knownTravelUtility = g_fixedDoorObservations[index].isTravelUtility;
            checkedAt = g_fixedDoorObservations[index].checkedAt;
            break;
        }
        ReleaseSRWLockShared(&g_forcedTravelLock);

        if (!knownTravelUtility && checkedAt != 0 && now - checkedAt < 10000) return;
        bool isTravelUtility = knownTravelUtility;
        if (!isTravelUtility)
        {
            std::wstring description;
            g_getFixedDoorDescription(object, &description, false, false);
            isTravelUtility = description.find(L"Boat") != std::wstring::npos ||
                              description.find(L"boat") != std::wstring::npos ||
                              description.find(L"Ferry") != std::wstring::npos ||
                              description.find(L"ferry") != std::wstring::npos;
        }

        LiveMapMarker marker{};
        if (isTravelUtility)
        {
            marker.type = 20;
            if (!TryReadEntityGlobalPosition(object, marker)) return;
            strcpy_s(marker.label.data(), marker.label.size(), "Row Boat");
        }

        AcquireSRWLockExclusive(&g_forcedTravelLock);
        if (existingIndex >= g_fixedDoorObservations.size() || g_fixedDoorObservations[existingIndex].object != object)
        {
            existingIndex = g_fixedDoorObservations.size();
            size_t oldestIndex = 0;
            for (size_t index = 0; index < g_fixedDoorObservations.size(); ++index)
            {
                if (!g_fixedDoorObservations[index].object) { existingIndex = index; break; }
                if (g_fixedDoorObservations[index].checkedAt < g_fixedDoorObservations[oldestIndex].checkedAt)
                    oldestIndex = index;
            }
            if (existingIndex >= g_fixedDoorObservations.size()) existingIndex = oldestIndex;
        }
        auto& observation = g_fixedDoorObservations[existingIndex];
        observation.object = object;
        observation.isTravelUtility = isTravelUtility;
        observation.checkedAt = now;
        if (isTravelUtility)
        {
            observation.lastSeen = now;
            observation.marker = marker;
        }
        ReleaseSRWLockExclusive(&g_forcedTravelLock);
    }

    void ResetNavigationDiagnostics()
    {
        g_mapPassCount = 0;
        g_areaAppendCount = 0;
        g_areaNuggetCount = 0;
        g_lastMapNuggetCount = 0;
        g_markerKnownCallCount = 0;
        g_markerKnownOverrideCount = 0;
        g_questListCallCount = 0;
        g_questFilterOverrideCount = 0;
        g_lastQuestCount = 0;
        g_questMarkerCallCount = 0;
        g_questMarkerExpandedCount = 0;
        g_questMarkerAppendCount = 0;
        g_poiInterestOverrideCount = 0;
        g_syntheticMarkerCount = 0;
        for (auto& count : g_lastMapTypeCounts) count = 0;
    }

    void ClearNavigationState()
    {
        RadarCanvas::Clear();
        g_showActiveQuestMarkers = false;
        g_showPointOfInterestMarkers = false;
        g_captureActiveQuests = false;
        g_capturePointOfInterestMarkers = false;
        AcquireSRWLockExclusive(&g_worldMarkerLock);
        g_worldMarkerCount = 0;
        ReleaseSRWLockExclusive(&g_worldMarkerLock);
        AcquireSRWLockExclusive(&g_activeQuestLock);
        g_activeQuestCount = 0;
        g_questTaskSnapshot.clear();
        g_questTaskSnapshotTime = 0;
        ReleaseSRWLockExclusive(&g_activeQuestLock);
        AcquireSRWLockExclusive(&g_liveMapMarkerLock);
        g_liveMapMarkerCount = 0;
        ReleaseSRWLockExclusive(&g_liveMapMarkerLock);
        AcquireSRWLockExclusive(&g_verifiedQuestMarkerLock);
        g_verifiedQuestMarkerCount = 0;
        ReleaseSRWLockExclusive(&g_verifiedQuestMarkerLock);
        AcquireSRWLockExclusive(&g_forcedTravelLock);
        g_forcedTravelObjectCount = 0;
        g_forcedTravelMarkerCount = 0;
        g_fixedDoorObservations = {};
        ReleaseSRWLockExclusive(&g_forcedTravelLock);
    }

    bool ParseFloat(const char* text, float& value)
    {
        if (!text || !*text) return false;
        char* end = nullptr;
        value = strtof(text, &end);
        return end && *end == '\0' && value == value && value >= -1000000.0f && value <= 1000000.0f;
    }

    unsigned int ResolveAreaVisibilityFlagOffset(void* function)
    {
        // Current builds gate generic POI collection with:
        //   cmp byte ptr [rdi+fieldOffset], al; jne skip
        // Derive the displacement from the verified export instead of pinning a
        // patch-specific object-layout offset.
        if (!function) return 0;
        const auto bytes = static_cast<const unsigned char*>(function);
        for (size_t index = 0; index + 8 < 256; ++index)
        {
            if (bytes[index] != 0x38 || bytes[index + 1] != 0x87 ||
                bytes[index + 6] != 0x0f || bytes[index + 7] != 0x85) continue;
            int displacement = 0;
            memcpy(&displacement, bytes + index + 2, sizeof(displacement));
            if (displacement > 0 && displacement < 0x10000) return static_cast<unsigned int>(displacement);
        }
        return 0;
    }

    void* ResolveQuestMapDataBuilder(void* areaAppendFunction)
    {
        // The shared quest-marker builder has a distinctive x64 prologue and
        // is called by every quest-aware AppendDetailMapData implementation.
        // Resolve the relative call from the verified AreaOfInterest export so
        // a relocated Game.dll never relies on a fixed address.
        if (!areaAppendFunction) return nullptr;
        constexpr unsigned char prologue[] =
        {
            0x4c, 0x89, 0x4c, 0x24, 0x20, 0x4c, 0x89, 0x44,
            0x24, 0x18, 0x48, 0x89, 0x54, 0x24, 0x10
        };
        const auto bytes = static_cast<const unsigned char*>(areaAppendFunction);
        for (size_t index = 0; index + 5 < 256; ++index)
        {
            if (bytes[index] != 0xe8) continue;
            int relative = 0;
            memcpy(&relative, bytes + index + 1, sizeof(relative));
            auto target = const_cast<unsigned char*>(bytes + index + 5 + relative);
            if (memcmp(target, prologue, sizeof(prologue)) == 0) return target;
        }
        return nullptr;
    }

    unsigned int ResolveQuestMarkerDistanceOffset(void* function)
    {
        // Entry checks `comiss xmm0, dword ptr [rcx+distanceOffset]`.
        if (!function) return 0;
        const auto bytes = static_cast<const unsigned char*>(function);
        for (size_t index = 0; index + 7 < 128; ++index)
        {
            if (bytes[index] != 0x0f || bytes[index + 1] != 0x2f || bytes[index + 2] != 0x81) continue;
            int displacement = 0;
            memcpy(&displacement, bytes + index + 3, sizeof(displacement));
            if (displacement > 0 && displacement < 0x1000) return static_cast<unsigned int>(displacement);
        }
        return 0;
    }

    VectorAppendFunction ResolveMinimapVectorAppend(void* teleporterAppend)
    {
        if (!teleporterAppend) return nullptr;
        constexpr unsigned char callsite[] =
        {
            0x48, 0x8d, 0x54, 0x24, 0x30, 0x48, 0x8b, 0xce, 0xe8
        };
        constexpr unsigned char targetPrologue[] =
        {
            0x40, 0x57, 0x48, 0x83, 0xec, 0x30, 0x48, 0xc7,
            0x44, 0x24, 0x20, 0xfe, 0xff, 0xff, 0xff
        };
        const auto bytes = static_cast<const unsigned char*>(teleporterAppend);
        for (size_t index = 0; index + sizeof(callsite) + 4 < 384; ++index)
        {
            if (memcmp(bytes + index, callsite, sizeof(callsite)) != 0) continue;
            int relative = 0;
            memcpy(&relative, bytes + index + sizeof(callsite), sizeof(relative));
            auto target = const_cast<unsigned char*>(bytes + index + sizeof(callsite) + 4 + relative);
            if (memcmp(target, targetPrologue, sizeof(targetPrologue)) == 0)
                return reinterpret_cast<VectorAppendFunction>(target);
        }
        return nullptr;
    }

    bool InstallNavigationHooks(HMODULE game)
    {
        g_getDetailMapDataTarget = reinterpret_cast<void*>(GetProcAddress(game, kGetDetailMapDataExport));
        g_appendAreaMapDataTarget = reinterpret_cast<void*>(GetProcAddress(game, kAppendAreaMapDataExport));
        g_isMarkerUidKnownTarget = reinterpret_cast<void*>(GetProcAddress(game, kIsMarkerUidKnownExport));
        g_getQuestsTarget = reinterpret_cast<void*>(GetProcAddress(game, kGetQuestsExport));
        g_getFixedDoorDescription = reinterpret_cast<GetGameDescriptionFunction>(GetProcAddress(game, kFixedDoorDescriptionExport));
        g_fixedDoorUpdateTarget = reinterpret_cast<void*>(GetProcAddress(game, kFixedDoorUpdateExport));
        g_staticTeleporterAppendTarget = reinterpret_cast<void*>(GetProcAddress(game, kStaticTeleporterAppendExport));
        constexpr std::array<const char*, 8> poiInterestExports =
        {
            kAscendantAltarInterestExport, kDynamicTeleporterInterestExport, kFixedItemContainerInterestExport,
            kFixedItemShrineInterestExport, kMonsterShrineInterestExport, kStaticShrineInterestExport,
            kStaticTeleporterInterestExport, kFixedDoorInterestExport
        };
        for (size_t index = 0; index < poiInterestExports.size(); ++index)
            g_poiInterestTargets[index] = reinterpret_cast<void*>(GetProcAddress(game, poiInterestExports[index]));
        g_areaVisibilityFlagOffset = ResolveAreaVisibilityFlagOffset(g_appendAreaMapDataTarget);
        g_appendQuestMapDataTarget = ResolveQuestMapDataBuilder(g_appendAreaMapDataTarget);
        g_questMarkerDistanceOffset = ResolveQuestMarkerDistanceOffset(g_appendQuestMapDataTarget);
        g_vectorAppend = ResolveMinimapVectorAppend(g_staticTeleporterAppendTarget);
        if (!g_getDetailMapDataTarget || !g_appendAreaMapDataTarget || !g_isMarkerUidKnownTarget ||
            !g_getQuestsTarget || !g_appendQuestMapDataTarget || g_areaVisibilityFlagOffset == 0 ||
            g_questMarkerDistanceOffset == 0 || !g_staticTeleporterAppendTarget || !g_vectorAppend ||
            !g_getFixedDoorDescription || !g_fixedDoorUpdateTarget) return false;
        for (auto target : g_poiInterestTargets) if (!target) return false;

        std::array<void*, 14> created{};
        size_t createdCount = 0;
        auto cleanup = [&]
        {
            for (size_t index = 0; index < createdCount; ++index)
            {
                MH_DisableHook(created[index]);
                MH_RemoveHook(created[index]);
            }
        };
        if (MH_CreateHook(g_getDetailMapDataTarget, &HookedGetDetailMapData,
            reinterpret_cast<void**>(&g_originalGetDetailMapData)) != MH_OK) return false;
        created[createdCount++] = g_getDetailMapDataTarget;
        if (MH_CreateHook(g_appendAreaMapDataTarget, &HookedAppendAreaMapData,
            reinterpret_cast<void**>(&g_originalAppendAreaMapData)) != MH_OK) { cleanup(); return false; }
        created[createdCount++] = g_appendAreaMapDataTarget;
        if (MH_CreateHook(g_isMarkerUidKnownTarget, &HookedIsMarkerUidKnown,
            reinterpret_cast<void**>(&g_originalIsMarkerUidKnown)) != MH_OK) { cleanup(); return false; }
        created[createdCount++] = g_isMarkerUidKnownTarget;
        if (MH_CreateHook(g_getQuestsTarget, &HookedGetQuests,
            reinterpret_cast<void**>(&g_originalGetQuests)) != MH_OK) { cleanup(); return false; }
        created[createdCount++] = g_getQuestsTarget;
        if (MH_CreateHook(g_appendQuestMapDataTarget, &HookedAppendQuestMapData,
            reinterpret_cast<void**>(&g_originalAppendQuestMapData)) != MH_OK) { cleanup(); return false; }
        created[createdCount++] = g_appendQuestMapDataTarget;
        if (MH_CreateHook(g_fixedDoorUpdateTarget, &HookedFixedDoorUpdate,
            reinterpret_cast<void**>(&g_originalFixedDoorUpdate)) != MH_OK) { cleanup(); return false; }
        created[createdCount++] = g_fixedDoorUpdateTarget;
        constexpr std::array<IsOfInterestFunction, 8> poiDetours =
        {
            HookedPoiInterest0, HookedPoiInterest1, HookedPoiInterest2, HookedPoiInterest3,
            HookedPoiInterest4, HookedPoiInterest5, HookedPoiInterest6, HookedPoiInterest7
        };
        for (size_t index = 0; index < g_poiInterestTargets.size(); ++index)
        {
            if (MH_CreateHook(g_poiInterestTargets[index], reinterpret_cast<void*>(poiDetours[index]),
                reinterpret_cast<void**>(&g_originalPoiInterest[index])) != MH_OK) { cleanup(); return false; }
            created[createdCount++] = g_poiInterestTargets[index];
        }
        for (size_t index = 0; index < createdCount; ++index)
        {
            if (MH_EnableHook(created[index]) != MH_OK) { cleanup(); return false; }
        }
        return true;
    }

    bool Enqueue(const std::shared_ptr<WorkRequest>& request)
    {
        if (!request) return false;
        EnterCriticalSection(&g_queueLock);
        if (g_queueCount == kQueueSlots) { LeaveCriticalSection(&g_queueLock); return false; }
        g_queue[g_writeIndex] = request;
        g_writeIndex = (g_writeIndex + 1) % kQueueSlots;
        ++g_queueCount;
        LeaveCriticalSection(&g_queueLock);
        return true;
    }

    bool Dequeue(std::shared_ptr<WorkRequest>& request)
    {
        EnterCriticalSection(&g_queueLock);
        if (g_queueCount == 0) { LeaveCriticalSection(&g_queueLock); return false; }
        request = std::move(g_queue[g_readIndex]);
        g_readIndex = (g_readIndex + 1) % kQueueSlots;
        --g_queueCount;
        LeaveCriticalSection(&g_queueLock);
        return true;
    }

    void Complete(const std::shared_ptr<WorkRequest>& request, const char* response)
    {
        {
            std::lock_guard lock(request->mutex);
            strcpy_s(request->response.data(), request->response.size(), response);
            request->completed = true;
        }
        request->completedCondition.notify_all();
    }

    void* GetPlayer()
    {
        if (!g_gameEngine || !*g_gameEngine || !g_getMainPlayer) return nullptr;
        return g_getMainPlayer(*g_gameEngine);
    }

    void WriteResourceResponse(const std::shared_ptr<WorkRequest>& request, void* player)
    {
        if (!player) { Complete(request, "ERROR NO_LOCAL_PLAYER"); return; }
        for (auto getter : g_getResource) if (!getter) { Complete(request, "ERROR RESOURCES_UNAVAILABLE"); return; }
        char response[kResponseSize]{};
        sprintf_s(response, "OK RESOURCES %u %u %u %u",
            g_getResource[0](player), g_getResource[1](player), g_getResource[2](player), g_getResource[3](player));
        Complete(request, response);
    }

    bool ReadCurrentQuestTasks(std::string& snapshot)
    {
        // Wrapper keeps SEH outside functions with C++ local destructors.
        __try
        {
            const auto game = g_gameEngine ? *g_gameEngine : nullptr;
            if (!game || !GetPlayer() || !MasteryAudit::loading || MasteryAudit::loading(game) ||
                !g_questRepository || !*g_questRepository) return false;
            return QuestStageCapture::ReadTrackedRepository(*g_questRepository, g_questTracked,
                {g_getQuestFileName, g_getNumTasks, g_getTaskByIndex, g_getTaskUid, g_taskInProgress}, snapshot);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    void ExecuteRequest(void* manager, const std::shared_ptr<WorkRequest>& request)
    {
        if (!request) return;
        if (request->kind == WorkKind::ItemAffix)
        {
            char response[kResponseSize]{};
            ItemAffixes::Run(g_gameEngine ? *g_gameEngine : nullptr,GetPlayer(),request->payload.data(),request->amount,request->deadline,response,sizeof(response));
            Complete(request,response);return;
        }
        if (request->kind == WorkKind::ResourceSet)
        {
            char response[kResponseSize]{};
            CharacterResource::Run(g_gameEngine ? *g_gameEngine : nullptr, GetPlayer(), request->payload.data(),
                static_cast<unsigned>(request->resource), request->amount, request->masteryToken, request->deadline, response, sizeof(response));
            Complete(request,response);return;
        }
        if (request->kind == WorkKind::CharacterLevel)
        {
            char response[kResponseSize]{};
            CharacterLevel::Run(g_gameEngine ? *g_gameEngine : nullptr, GetPlayer(), request->payload.data(),
                request->amount, request->masteryToken, request->deadline, response, sizeof(response));
            Complete(request, response); return;
        }
        if (request->kind == WorkKind::QuestTasks)
        {
            std::string snapshot;
            const bool ready = g_questTasksAvailable && ReadCurrentQuestTasks(snapshot);
            Complete(request, ready ? snapshot.c_str() : "OK QUEST_TASKS WAITING");
            return;
        }
        if (request->kind == WorkKind::Bookmark)
        {
            char response[kResponseSize]{};
            PersistentBookmarks::Run(g_gameEngine ? *g_gameEngine : nullptr, GetPlayer(),
                request->payload.data(), request->deadline, response, sizeof(response));
            Complete(request, response); return;
        }
        if (request->kind == WorkKind::Assists)
        {
            char response[kResponseSize]{};
            GameplayAssists::Run(request->payload.data(), request->masteryToken, request->assistMask,
                request->assistSpeed, request->deadline, response, sizeof(response));
            Complete(request, response); return;
        }
        if (request->kind == WorkKind::MasteryRespec)
        {
            GameplayAssists::Stop(); GameplayAssists::Tick();
            char response[kResponseSize]{};
            MasteryRespec::Run(g_gameEngine ? *g_gameEngine : nullptr, GetPlayer(), request->payload.data(),
                request->masteryToken, request->deadline, response, sizeof(response));
            Complete(request, response); return;
        }
        if (request->kind == WorkKind::Lua)
        {
            Complete(request, g_runCode && manager && g_runCode(manager, request->payload.data()) ? "OK LUA" : "ERROR LUA_EXECUTION_FAILED");
            return;
        }
        auto player = GetPlayer();
        if (!player) { Complete(request, "ERROR NO_LOCAL_PLAYER"); return; }
        if (request->kind == WorkKind::MasteryAudit)
        {
            char response[kResponseSize]{};
            MasteryAudit::Read(g_gameEngine ? *g_gameEngine : nullptr, player, response, sizeof(response));
            Complete(request, response);
            return;
        }
        if (request->kind == WorkKind::ResourceGet) { WriteResourceResponse(request, player); return; }
        if (request->kind == WorkKind::TokenHas)
        {
            if (!g_hasToken) { Complete(request, "ERROR TOKENS_UNAVAILABLE"); return; }
            const std::string token(request->payload.data());
            Complete(request, g_hasToken(player, token) ? "OK TOKEN PRESENT" : "OK TOKEN ABSENT");
            return;
        }
        if (request->kind == WorkKind::PlayerPosition)
        {
            if (!g_getCoords) { Complete(request, "ERROR POSITION_UNAVAILABLE"); return; }
            alignas(16) std::array<unsigned char, 0x40> coordinates{};
            g_getCoords(player, coordinates.data());
            float x = 0, y = 0, z = 0;
            std::memcpy(&x, coordinates.data() + 0x08, sizeof(x));
            std::memcpy(&y, coordinates.data() + 0x0c, sizeof(y));
            std::memcpy(&z, coordinates.data() + 0x10, sizeof(z));
            void* region = nullptr;
            std::memcpy(&region, coordinates.data(), sizeof(region));
            if (region)
            {
                int offsetX = 0, offsetY = 0, offsetZ = 0;
                std::memcpy(&offsetX, static_cast<unsigned char*>(region) + 0x3c, sizeof(offsetX));
                std::memcpy(&offsetY, static_cast<unsigned char*>(region) + 0x40, sizeof(offsetY));
                std::memcpy(&offsetZ, static_cast<unsigned char*>(region) + 0x44, sizeof(offsetZ));
                x += static_cast<float>(offsetX);
                y += static_cast<float>(offsetY);
                z += static_cast<float>(offsetZ);
            }
            if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z))
            {
                Complete(request, "ERROR INVALID_PLAYER_POSITION"); return;
            }
            char response[kResponseSize]{};
            sprintf_s(response, "OK POSITION %.9g %.9g %.9g", x, y, z);
            Complete(request, response);
            return;
        }
        if (request->kind == WorkKind::PlayerRegion)
        {
            if (!g_getCoords || !g_getRegionLoadFileName) { Complete(request, "ERROR REGION_UNAVAILABLE"); return; }
            alignas(16) std::array<unsigned char, 0x40> coordinates{};
            g_getCoords(player, coordinates.data());
            void* region = nullptr;
            std::memcpy(&region, coordinates.data(), sizeof(region));
            if (!region) { Complete(request, "ERROR NO_PLAYER_REGION"); return; }
            std::string path;
            g_getRegionLoadFileName(region, &path);
            if (path.empty() || path.size() >= kResponseSize - 16) { Complete(request, "ERROR INVALID_PLAYER_REGION"); return; }
            char response[kResponseSize]{};
            sprintf_s(response, "OK REGION\t%s", path.c_str());
            Complete(request, response);
        }
    }

    void __fastcall HookedUpdate(void* manager, int argument)
    {
        GameplayAssists::Tick();
        g_originalUpdate(manager, argument);
        std::shared_ptr<WorkRequest> request;
        for (int i = 0; i < 4 && Dequeue(request); ++i) ExecuteRequest(manager, request);
        GameplayAssists::Tick();
    }

    HMODULE WaitForModule(const wchar_t* name)
    {
        for (int i = 0; i < 300; ++i) { if (auto module = GetModuleHandleW(name)) return module; Sleep(100); }
        return nullptr;
    }

    template <typename T> T Resolve(HMODULE module, const char* name) { return reinterpret_cast<T>(GetProcAddress(module, name)); }

    void ResolveGameApi(HMODULE engine, HMODULE game)
    {
        g_gameEngine = Resolve<void**>(game, kGameEngineExport);
        g_getMainPlayer = Resolve<GetMainPlayerFunction>(game, kGetMainPlayerExport);
        g_getCoords = Resolve<GetCoordsFunction>(engine, kGetCoordsExport);
        g_getRegionContainingXZ = Resolve<GetRegionContainingXZFunction>(engine, kGetRegionContainingXZExport);
        g_setFromWorldPosition = Resolve<SetFromWorldPositionFunction>(engine, kSetFromWorldPositionExport);
        g_getRegionLoadFileName = Resolve<GetRegionLoadFileNameFunction>(engine, kGetRegionLoadFileNameExport);
        g_getResource = { Resolve<GetResourceFunction>(game, kGetMoneyExport), Resolve<GetResourceFunction>(game, kGetSkillPointsExport), Resolve<GetResourceFunction>(game, kGetModifierPointsExport), Resolve<GetResourceFunction>(game, kGetDevotionPointsExport) };
        g_hasToken = Resolve<HasTokenFunction>(game, kHasTokenExport);
        g_getQuestFileName = Resolve<GetQuestFileNameFunction>(game, kGetQuestFileNameExport);
        g_getNumTasks = Resolve<GetResourceFunction>(game, kGetNumTasksExport);
        g_getTaskByIndex = Resolve<GetTaskByIndexFunction>(game, kGetTaskByIndexExport);
        g_getTaskUid = Resolve<GetResourceFunction>(game, kGetTaskUidExport);
        g_taskInProgress = Resolve<TaskInProgressFunction>(game, kTaskInProgressExport);
        g_questRepository = Resolve<void**>(game, "?s_instance@?$Singleton@VQuest2Repository@GAME@@@GAME@@0PEAVQuest2Repository@2@EA");
        g_questTracked = Resolve<bool(*)(void*)>(game, "?IsTracked@Quest2@GAME@@QEBA_NXZ");
        // Semantics and ABI audited against this game image; unknown versions
        // retain nearby native stars instead of guessing task states.
        const auto gameBase = reinterpret_cast<const unsigned char*>(game);
        const auto gameDos = reinterpret_cast<const IMAGE_DOS_HEADER*>(gameBase);
        const auto gamePe = reinterpret_cast<const IMAGE_NT_HEADERS64*>(gameBase + gameDos->e_lfanew);
        g_questTasksAvailable = g_questRepository && g_questTracked && g_getQuestFileName && g_getNumTasks && g_getTaskByIndex && g_getTaskUid && g_taskInProgress &&
            gamePe->FileHeader.TimeDateStamp == 0x6A85FBB3 && gamePe->OptionalHeader.SizeOfImage == 0xAB5000;
        const bool common = g_gameEngine && g_getMainPlayer;
        if (common)
        {
            bool resources = true;
            for (auto fn : g_getResource) resources = resources && fn != nullptr;
            if (resources) g_capabilities.fetch_or(1);
            if (g_hasToken) g_capabilities.fetch_or(2);
        }
        g_capabilities.fetch_or(4);
        const bool navigation = InstallNavigationHooks(game);
        if (navigation) g_capabilities.fetch_or(8);
        if (common && g_getCoords) g_capabilities.fetch_or(16);
        if (common && g_getCoords && g_getRegionLoadFileName) g_capabilities.fetch_or(128);
        if (navigation && common && g_getQuestFileName) g_capabilities.fetch_or(32 | 64);
    }

    void Reply(HANDLE pipe, const char* value)
    {
        char line[kResponseSize + 2]{};
        sprintf_s(line, "%s\n", value);
        DWORD written = 0;
        WriteFile(pipe, line, static_cast<DWORD>(std::strlen(line)), &written, nullptr);
    }

    void SubmitAndWait(HANDLE pipe, const std::shared_ptr<WorkRequest>& request)
    {
        if (!Enqueue(request)) { Reply(pipe, "ERROR QUEUE_FULL"); return; }
        std::unique_lock lock(request->mutex);
        if (!request->completedCondition.wait_for(lock, std::chrono::seconds(8), [&] { return request->completed; })) Reply(pipe, "ERROR GAME_THREAD_TIMEOUT");
        else Reply(pipe, request->response.data());
    }

    bool ParseResource(const char* value, ResourceKind& resource)
    {
        if (strcmp(value, "money") == 0) resource = ResourceKind::Money;
        else if (strcmp(value, "skill") == 0) resource = ResourceKind::Skill;
        else if (strcmp(value, "attribute") == 0) resource = ResourceKind::Attribute;
        else if (strcmp(value, "devotion") == 0) resource = ResourceKind::Devotion;
        else return false;
        return true;
    }

    void ServeLine(HANDLE pipe, char* line)
    {
        if (strcmp(line, "PING") == 0)
        {
            if (!g_ready) { char value[64]{}; sprintf_s(value, "ERROR INIT %lu", g_error.load()); Reply(pipe, value); return; }
            char capabilities[512] = "PONG 38 READY CAPS=LUA_SYNC,UNLOAD";
            if (ItemAffixes::available) strcat_s(capabilities, ",ITEM_AFFIXES");
            if ((g_capabilities.load() & 1) != 0) strcat_s(capabilities, ",RESOURCES");
            if ((g_capabilities.load() & 2) != 0) strcat_s(capabilities, ",TOKENS");
            if ((g_capabilities.load() & 8) != 0) strcat_s(capabilities, ",NAVIGATION");
            if ((g_capabilities.load() & 16) != 0) strcat_s(capabilities, ",POSITION");
            if ((g_capabilities.load() & 32) != 0) strcat_s(capabilities, ",QUESTS");
            if ((g_capabilities.load() & 32) != 0 && g_questTasksAvailable) strcat_s(capabilities, ",QUEST_TASKS");
            if ((g_capabilities.load() & 64) != 0) strcat_s(capabilities, ",RADAR");
            if ((g_capabilities.load() & 128) != 0) strcat_s(capabilities, ",REGION");
            if (MinimapTelemetry::available) strcat_s(capabilities, ",MINIMAP");
            if (MinimapProjection::available) strcat_s(capabilities, ",MINIMAP_PROJECTION");
            if (RadarCanvas::available) strcat_s(capabilities, ",RADAR_CANVAS");
            if (g_questTasksAvailable) strcat_s(capabilities, ",QUEST_TASKS_LIVE");
            if (CharacterLevel::available) strcat_s(capabilities, ",CHARACTER_LEVEL");
            if (CharacterResource::available) strcat_s(capabilities, ",RESOURCE_SET,LOADED_PROGRESSION,RESOURCE_CAP_BYPASS");
            if (MasteryAudit::available) strcat_s(capabilities, ",MASTERY_AUDIT");
            if (MasteryRespec::available) strcat_s(capabilities, ",FULL_RESPEC_TEST,FULL_RESPEC_REPEAT");
            if (GameplayAssists::available) strcat_s(capabilities, ",GAMEPLAY_ASSISTS");
            if (PersistentBookmarks::available) strcat_s(capabilities, ",PERSISTENT_BOOKMARKS,SHARED_BOOKMARKS,CROSS_MODE_BOOKMARKS");
            if (PersistentBookmarks::loadingAvailable) strcat_s(capabilities, ",BOOKMARK_LOADING");
            if (PersistentBookmarks::available && PersistentBookmarks::zoneRecord && PersistentBookmarks::zoneManager && PersistentBookmarks::zoneData && PersistentBookmarks::localization && PersistentBookmarks::localize)
                strcat_s(capabilities, ",BOOKMARK_LABELS");
            Reply(pipe, capabilities); return;
        }
        if (!g_ready) { Reply(pipe, "ERROR NOT_READY"); return; }
        if (strncmp(line, "BOOKMARK\t", 9) == 0)
        {
            if (strlen(line + 9) >= kCommandSize) { Reply(pipe, "ERROR BOOKMARK_INVALID_REQUEST"); return; }
            auto request = std::make_shared<WorkRequest>(); request->kind = WorkKind::Bookmark;
            strcpy_s(request->payload.data(), request->payload.size(), line + 9);
            // Bookmark actions must not sit through a loading pause and fire later.
            // STATUS remains pollable and never submits a second movement.
            request->deadline = GetTickCount64() + 500;
            SubmitAndWait(pipe, request); return;
        }
        if (strcmp(line, "SHUTDOWN") == 0)
        {
            GameplayAssists::Stop();
            if (GameplayAssists::speedTouched.load())
            { Reply(pipe, "ERROR DISABLE_ASSISTS_BEFORE_UNLOAD"); return; }
            ClearNavigationState();
            Reply(pipe, "OK UNLOADING");
            g_ready = false;
            g_shutdownRequested = true;
            return;
        }
        if (strncmp(line, "ASSISTS\t", 8) == 0)
        {
            auto request = std::make_shared<WorkRequest>(); request->kind = WorkKind::Assists;
            request->deadline = GetTickCount64() + 2000;
            if (strcmp(line + 8, "STATUS") == 0 || strcmp(line + 8, "OFF") == 0)
            {
                if (strcmp(line + 8, "OFF") == 0) GameplayAssists::Stop();
                strcpy_s(request->payload.data(), request->payload.size(), line + 8);
            }
            else
            {
                const bool set = strncmp(line + 8, "SET\t", 4) == 0;
                const bool pulse = strncmp(line + 8, "PULSE\t", 6) == 0;
                if (!set && !pulse) { Reply(pipe, "ERROR INVALID_ASSIST_COMMAND"); return; }
                const char* cursor = line + (set ? 12 : 14), *end = line + strlen(line);
                auto number = std::from_chars(cursor, end, request->masteryToken);
                if (number.ec != std::errc{} || !request->masteryToken || (set ? number.ptr == end || *number.ptr != '\t' : number.ptr != end))
                { Reply(pipe, "ERROR INVALID_ASSIST_REVISION"); return; }
                if (set)
                {
                    number = std::from_chars(number.ptr + 1, end, request->assistMask);
                    if (number.ec != std::errc{} || number.ptr == end || *number.ptr != '\t')
                    { Reply(pipe, "ERROR INVALID_ASSIST_OPTIONS"); return; }
                    number = std::from_chars(number.ptr + 1, end, request->assistSpeed);
                    if (number.ec != std::errc{} || number.ptr != end || request->assistMask > 15 ||
                        request->assistSpeed < 100 || request->assistSpeed > 200)
                    { Reply(pipe, "ERROR INVALID_ASSIST_OPTIONS"); return; }
                }
                strcpy_s(request->payload.data(), request->payload.size(), set ? "SET" : "PULSE");
            }
            SubmitAndWait(pipe, request); return;
        }
        if (strncmp(line,"ITEM_AFFIX\t",11)==0)
        {
            auto request=std::make_shared<WorkRequest>();request->kind=WorkKind::ItemAffix;
            const auto end=line+strlen(line);
            const auto number=std::from_chars(line+11,end,request->amount);
            if(number.ec!=std::errc{} || number.ptr==end || *number.ptr!='\t' || request->amount<1 || request->amount>100 || strlen(number.ptr+1)>=kCommandSize)
            { Reply(pipe,"ERROR ITEM_AFFIX_ARGUMENTS");return; }
            strcpy_s(request->payload.data(),request->payload.size(),number.ptr+1);
            request->deadline=GetTickCount64()+5000;SubmitAndWait(pipe,request);return;
        }
        if (strncmp(line, "LUA\t", 4) == 0)
        {
            if (strlen(line + 4) >= kCommandSize) { Reply(pipe, "ERROR COMMAND_TOO_LONG"); return; }
            auto request = std::make_shared<WorkRequest>();
            strcpy_s(request->payload.data(), request->payload.size(), line + 4);
            if (Enqueue(request)) Reply(pipe, "QUEUED"); else Reply(pipe, "ERROR QUEUE_FULL_OR_COMMAND_TOO_LONG");
            return;
        }
        if (strncmp(line, "LUA_SYNC\t", 9) == 0)
        {
            if (strlen(line + 9) >= kCommandSize) { Reply(pipe, "ERROR COMMAND_TOO_LONG"); return; }
            auto request = std::make_shared<WorkRequest>(); strcpy_s(request->payload.data(), request->payload.size(), line + 9); SubmitAndWait(pipe, request); return;
        }
        if (strcmp(line, "RESOURCE\tGET") == 0)
        {
            auto request = std::make_shared<WorkRequest>(); request->kind = WorkKind::ResourceGet; SubmitAndWait(pipe, request); return;
        }
        if (strncmp(line,"RESOURCE\t",9)==0)
        {
            if (!CharacterResource::available) { Reply(pipe,"ERROR RESOURCE_SET_UNAVAILABLE");return; }
            const auto parts=RadarCanvas::Split(line,'\t');
            auto request=std::make_shared<WorkRequest>();request->kind=WorkKind::ResourceSet;
            request->deadline=GetTickCount64()+5000;
            if (parts.size()==2 && parts[1]=="LIMITS") strcpy_s(request->payload.data(),request->payload.size(),"LIMITS");
            else if (parts.size()==3 && parts[1]=="COMMIT" && RadarCanvas::Number(parts[2],request->masteryToken) && request->masteryToken)
                strcpy_s(request->payload.data(),request->payload.size(),"COMMIT");
            else if (parts.size()==4 && (parts[1]=="PREPARE" || parts[1]=="PREPARE_BYPASS") && RadarCanvas::Number(parts[3],request->amount))
            {
                const std::string kind(parts[2]);
                if (!ParseResource(kind.c_str(),request->resource)) { Reply(pipe,"ERROR INVALID_RESOURCE");return; }
                strcpy_s(request->payload.data(),request->payload.size(),parts[1]=="PREPARE_BYPASS" ? "PREPARE_BYPASS" : "PREPARE");
            }
            else { Reply(pipe,"ERROR RESOURCE_USE_CAPPED_SET_WORKFLOW");return; }
            SubmitAndWait(pipe,request);return;
        }
        if (strncmp(line, "LEVEL\t", 6) == 0)
        {
            if (!CharacterLevel::available) { Reply(pipe, "ERROR LEVEL_UNAVAILABLE"); return; }
            const auto parts = RadarCanvas::Split(line, '\t');
            unsigned long long value = 0;
            if (parts.size() != 3 || !RadarCanvas::Number(parts[2], value) || !value ||
                (parts[1] != "PREPARE" && parts[1] != "COMMIT") || (parts[1] == "PREPARE" && value > CharacterLevel::HardLimit))
            { Reply(pipe, "ERROR LEVEL_EXCEEDS_GAME_CAP"); return; }
            auto request = std::make_shared<WorkRequest>(); request->kind = WorkKind::CharacterLevel;
            strcpy_s(request->payload.data(), request->payload.size(), parts[1] == "PREPARE" ? "PREPARE" : "COMMIT");
            request->amount = static_cast<unsigned>(value); request->masteryToken = value;
            request->deadline = GetTickCount64() + 5000;
            SubmitAndWait(pipe, request); return;
        }
        if (strcmp(line, "MASTERY\tAUDIT") == 0)
        {
            auto request = std::make_shared<WorkRequest>(); request->kind = WorkKind::MasteryAudit; SubmitAndWait(pipe, request); return;
        }
        if (strcmp(line, "MASTERY\tPREPARE") == 0)
        {
            auto request = std::make_shared<WorkRequest>(); request->kind = WorkKind::MasteryRespec;
            strcpy_s(request->payload.data(), request->payload.size(), "PREPARE"); SubmitAndWait(pipe, request); return;
        }
        if (strncmp(line, "MASTERY\tCOMMIT\t", 15) == 0 || strncmp(line, "MASTERY\tSTATUS\t", 15) == 0)
        {
            unsigned long long token = 0;
            const auto parsed = std::from_chars(line + 15, line + strlen(line), token);
            if (parsed.ec != std::errc{} || *parsed.ptr || !token) { Reply(pipe, "ERROR MASTERY_INVALID_TOKEN"); return; }
            auto request = std::make_shared<WorkRequest>(); request->kind = WorkKind::MasteryRespec;
            strcpy_s(request->payload.data(), request->payload.size(), line[8] == 'C' ? "COMMIT" : "STATUS");
            request->masteryToken = token; request->deadline = GetTickCount64() + 5000;
            SubmitAndWait(pipe, request); return;
        }
        if (strncmp(line, "TOKEN\tHAS\t", 10) == 0)
        {
            const char* token = line + 10;
            if (!*token || strlen(token) >= 160) { Reply(pipe, "ERROR INVALID_TOKEN"); return; }
            auto request = std::make_shared<WorkRequest>(); request->kind = WorkKind::TokenHas;
            strcpy_s(request->payload.data(), request->payload.size(), token); SubmitAndWait(pipe, request); return;
        }
        if (strcmp(line, "PLAYER\tPOSITION") == 0)
        {
            if ((g_capabilities.load() & 16) == 0) { Reply(pipe, "ERROR POSITION_UNAVAILABLE"); return; }
            auto request = std::make_shared<WorkRequest>(); request->kind = WorkKind::PlayerPosition;
            SubmitAndWait(pipe, request); return;
        }
        if (strcmp(line, "PLAYER\tREGION") == 0)
        {
            if ((g_capabilities.load() & 128) == 0) { Reply(pipe, "ERROR REGION_UNAVAILABLE"); return; }
            auto request = std::make_shared<WorkRequest>(); request->kind = WorkKind::PlayerRegion;
            SubmitAndWait(pipe, request); return;
        }
        if (strcmp(line, "QUEST\tTASKS") == 0)
        {
            if (!g_questTasksAvailable) { Reply(pipe, "ERROR QUEST_TASKS_UNAVAILABLE"); return; }
            auto request = std::make_shared<WorkRequest>(); request->kind = WorkKind::QuestTasks;
            SubmitAndWait(pipe, request); return;
        }
        if (strcmp(line, "QUEST\tACTIVE") == 0)
        {
            if ((g_capabilities.load() & 32) == 0) { Reply(pipe, "ERROR QUESTS_UNAVAILABLE"); return; }
            std::array<std::array<char, kQuestPathSize>, kActiveQuestSlots> paths{};
            size_t count = 0;
            AcquireSRWLockShared(&g_activeQuestLock);
            count = g_activeQuestCount;
            paths = g_activeQuestPaths;
            ReleaseSRWLockShared(&g_activeQuestLock);
            std::array<char, kResponseSize> response{};
            sprintf_s(response.data(), response.size(), "OK ACTIVE_QUESTS %zu", count);
            for (size_t index = 0; index < count; ++index)
            {
                if (strlen(response.data()) + strlen(paths[index].data()) + 2 >= response.size()) break;
                strcat_s(response.data(), response.size(), "\t");
                strcat_s(response.data(), response.size(), paths[index].data());
            }
            Reply(pipe, response.data()); return;
        }
        if (strncmp(line, "RADAR\tCANVAS\t", 13) == 0)
        {
            if (!RadarCanvas::available) { Reply(pipe, "ERROR RADAR_CANVAS_UNAVAILABLE"); return; }
            if (strcmp(line + 13, "STATUS") == 0)
            {
                const auto last = RadarCanvas::lastDraw.load();
                char response[96]{};
                sprintf_s(response, "OK RADAR_CANVAS %u %llu", RadarCanvas::drawnCount.load(), last ? GetTickCount64() - last : 999999ULL);
                Reply(pipe, response); return;
            }
            if (strcmp(line + 13, "CLEAR") != 0 && !g_captureActiveQuests.load() && !g_capturePointOfInterestMarkers.load())
            { Reply(pipe, "ERROR RADAR_INACTIVE"); return; }
            Reply(pipe, RadarCanvas::Command(line + 13) ? "OK RADAR_CANVAS" : "ERROR RADAR_CANVAS_FRAME"); return;
        }
        if (strcmp(line, "RADAR\tMARKERS") == 0)
        {
            if ((g_capabilities.load() & 64) == 0) { Reply(pipe, "ERROR RADAR_UNAVAILABLE"); return; }
            std::array<LiveMapMarker, kLiveMapMarkerSlots> markers{};
            size_t count = 0;
            AcquireSRWLockShared(&g_liveMapMarkerLock);
            count = g_liveMapMarkerCount;
            markers = g_liveMapMarkers;
            ReleaseSRWLockShared(&g_liveMapMarkerLock);
            std::array<char, kResponseSize> response{};
            sprintf_s(response.data(), response.size(), "OK RADAR_MARKERS %zu", count);
            for (size_t index = 0; index < count; ++index)
            {
                char entry[224]{};
                sprintf_s(entry, "%u,%.9g,%.9g,%.9g,%s", markers[index].type,
                    markers[index].x, markers[index].y, markers[index].z, markers[index].label.data());
                if (strlen(response.data()) + strlen(entry) + 2 >= response.size()) break;
                strcat_s(response.data(), response.size(), "\t");
                strcat_s(response.data(), response.size(), entry);
            }
            Reply(pipe, response.data()); return;
        }
        if (strncmp(line, "RADAR\tSET\t", 10) == 0)
        {
            if ((g_capabilities.load() & 64) == 0) { Reply(pipe, "ERROR RADAR_UNAVAILABLE"); return; }
            char* context = nullptr;
            const char* quests = strtok_s(line + 10, "\t", &context);
            const char* points = strtok_s(nullptr, "\t", &context);
            const char* extra = strtok_s(nullptr, "\t", &context);
            if (!quests || extra || (strcmp(quests, "0") != 0 && strcmp(quests, "1") != 0) ||
                (points && strcmp(points, "0") != 0 && strcmp(points, "1") != 0))
            { Reply(pipe, "ERROR INVALID_RADAR_OPTION"); return; }
            const bool captureQuests = strcmp(quests, "1") == 0;
            const bool capturePoints = points && strcmp(points, "1") == 0;
            g_captureActiveQuests = captureQuests;
            g_capturePointOfInterestMarkers = capturePoints;
            g_travelScanDeadline.store(capturePoints ? GetTickCount64() + 3000 : 0, std::memory_order_release);
            if (!captureQuests)
            {
                AcquireSRWLockExclusive(&g_activeQuestLock);
                g_activeQuestCount = 0;
                g_questTaskSnapshot.clear();
                g_questTaskSnapshotTime = 0;
                ReleaseSRWLockExclusive(&g_activeQuestLock);
            }
            if (!captureQuests && !capturePoints)
            {
                AcquireSRWLockExclusive(&g_liveMapMarkerLock);
                g_liveMapMarkerCount = 0;
                ReleaseSRWLockExclusive(&g_liveMapMarkerLock);
            }
            if (!capturePoints)
            {
                AcquireSRWLockExclusive(&g_forcedTravelLock);
                g_forcedTravelObjectCount = 0;
                g_forcedTravelMarkerCount = 0;
                g_fixedDoorObservations = {};
                ReleaseSRWLockExclusive(&g_forcedTravelLock);
            }
            char response[96]{};
            sprintf_s(response, "OK RADAR QUESTS=%d POIS=%d", captureQuests ? 1 : 0, capturePoints ? 1 : 0);
            Reply(pipe, response); return;
        }
        if (strcmp(line, "NAVIGATION\tCLEAR") == 0)
        {
            if ((g_capabilities.load() & 8) == 0) { Reply(pipe, "ERROR NAVIGATION_UNAVAILABLE"); return; }
            ClearNavigationState();
            Reply(pipe, "OK NAVIGATION QUESTS=0 POIS=0"); return;
        }
        if (strcmp(line, "NAVIGATION\tMARKERS\tCLEAR") == 0)
        {
            if ((g_capabilities.load() & 8) == 0) { Reply(pipe, "ERROR NAVIGATION_UNAVAILABLE"); return; }
            AcquireSRWLockExclusive(&g_worldMarkerLock);
            g_worldMarkerCount = 0;
            ReleaseSRWLockExclusive(&g_worldMarkerLock);
            Reply(pipe, "OK NAVIGATION MARKERS=0"); return;
        }
        if (strncmp(line, "NAVIGATION\tMARKER\t", 18) == 0)
        {
            if ((g_capabilities.load() & 8) == 0) { Reply(pipe, "ERROR NAVIGATION_UNAVAILABLE"); return; }
            char* context = nullptr;
            strtok_s(line, "\t", &context); strtok_s(nullptr, "\t", &context);
            const char* action = strtok_s(nullptr, "\t", &context);
            const char* typeText = strtok_s(nullptr, "\t", &context);
            const char* xText = strtok_s(nullptr, "\t", &context);
            const char* yText = strtok_s(nullptr, "\t", &context);
            const char* zText = strtok_s(nullptr, "\t", &context);
            const char* extra = strtok_s(nullptr, "\t", &context);
            unsigned int type = 0; float x = 0, y = 0, z = 0;
            const bool relativeToPlayer = action && strcmp(action, "NEAR") == 0;
            if (!action || (!relativeToPlayer && strcmp(action, "ADD") != 0) || !typeText || !xText || !yText || !zText || extra ||
                !ParseFloat(xText, x) || !ParseFloat(yText, y) || !ParseFloat(zText, z))
            { Reply(pipe, "ERROR INVALID_MARKER"); return; }
            const auto parsed = std::from_chars(typeText, typeText + strlen(typeText), type);
            if (parsed.ec != std::errc{} || *parsed.ptr != '\0' || type != 3)
            { Reply(pipe, "ERROR INVALID_MARKER_TYPE"); return; }
            AcquireSRWLockExclusive(&g_worldMarkerLock);
            if (g_worldMarkerCount >= g_worldMarkers.size())
            {
                ReleaseSRWLockExclusive(&g_worldMarkerLock);
                Reply(pipe, "ERROR MARKER_LIMIT"); return;
            }
            g_worldMarkers[g_worldMarkerCount++] = { type, x, y, z, relativeToPlayer };
            const auto count = g_worldMarkerCount;
            ReleaseSRWLockExclusive(&g_worldMarkerLock);
            char response[96]{}; sprintf_s(response, "OK NAVIGATION MARKERS=%zu", count);
            Reply(pipe, response); return;
        }
        if (strcmp(line, "MINIMAP\tFRAME") == 0 || strcmp(line, "MINIMAP\tFRAME2") == 0)
        {
            std::array<float, 6> bounds{};
            std::array<float, 8> transform{};
            unsigned long long context = 0;
            if (!MinimapProjection::available) { Reply(pipe, "ERROR MINIMAP_PROJECTION_UNAVAILABLE"); return; }
            if (!MinimapTelemetry::ReadFrame(bounds, transform, &context)) { Reply(pipe, "OK MINIMAP_FRAME HIDDEN"); return; }
            char response[512]{};
            sprintf_s(response, "OK MINIMAP_FRAME %.9g %.9g %.9g %.9g %.9g %.9g %.9g %.9g %.9g %.9g %.9g %.9g %.9g %.9g",
                bounds[0], bounds[1], bounds[2], bounds[3], bounds[4], bounds[5],
                transform[0], transform[1], transform[2], transform[3], transform[4], transform[5], transform[6], transform[7]);
            if (strcmp(line, "MINIMAP\tFRAME2") == 0)
            {
                char suffix[32]{}; sprintf_s(suffix, " %llu", context); strcat_s(response, suffix);
            }
            Reply(pipe, response); return;
        }
        if (strcmp(line, "MINIMAP\tBOUNDS") == 0)
        {
            std::array<float, 6> bounds{};
            if (!MinimapTelemetry::available) { Reply(pipe, "ERROR MINIMAP_UNAVAILABLE"); return; }
            if (!MinimapTelemetry::Read(bounds)) { Reply(pipe, "OK MINIMAP HIDDEN"); return; }
            char response[256]{};
            sprintf_s(response, "OK MINIMAP %.9g %.9g %.9g %.9g %.9g %.9g",
                bounds[0], bounds[1], bounds[2], bounds[3], bounds[4], bounds[5]);
            Reply(pipe, response); return;
        }
        if (strcmp(line, "NAVIGATION\tSTATUS") == 0)
        {
            if ((g_capabilities.load() & 8) == 0) { Reply(pipe, "ERROR NAVIGATION_UNAVAILABLE"); return; }
            char response[800]{};
            sprintf_s(response,
                "OK NAVIGATION QUESTS=%d POIS=%d MAP=%llu MAP_NUGGETS=%llu AREA=%llu AREA_NUGGETS=%llu POI_FILTERS=%llu SYNTH=%llu MARKER_CALLS=%llu MARKER_OVERRIDES=%llu QUEST_LISTS=%llu QUEST_FILTERS=%llu QUEST_RESULTS=%llu QUEST_MARKER_CALLS=%llu QUEST_EXPANDED=%llu QUEST_MARKERS=%llu T3=%llu T5=%llu T6=%llu T12=%llu T14=%llu T16=%llu T19=%llu",
                g_showActiveQuestMarkers.load() ? 1 : 0, g_showPointOfInterestMarkers.load() ? 1 : 0,
                g_mapPassCount.load(), g_lastMapNuggetCount.load(), g_areaAppendCount.load(), g_areaNuggetCount.load(),
                g_poiInterestOverrideCount.load(), g_syntheticMarkerCount.load(), g_markerKnownCallCount.load(), g_markerKnownOverrideCount.load(), g_questListCallCount.load(),
                g_questFilterOverrideCount.load(), g_lastQuestCount.load(), g_questMarkerCallCount.load(),
                g_questMarkerExpandedCount.load(), g_questMarkerAppendCount.load(), g_lastMapTypeCounts[0].load(), g_lastMapTypeCounts[1].load(),
                g_lastMapTypeCounts[2].load(), g_lastMapTypeCounts[3].load(), g_lastMapTypeCounts[4].load(),
                g_lastMapTypeCounts[5].load(), g_lastMapTypeCounts[6].load());
            Reply(pipe, response); return;
        }
        if (strncmp(line, "NAVIGATION\tSET\t", 15) == 0)
        {
            if ((g_capabilities.load() & 8) == 0) { Reply(pipe, "ERROR NAVIGATION_UNAVAILABLE"); return; }
            char* context = nullptr; strtok_s(line, "\t", &context); strtok_s(nullptr, "\t", &context);
            const char* quests = strtok_s(nullptr, "\t", &context); const char* points = strtok_s(nullptr, "\t", &context);
            const char* extra = strtok_s(nullptr, "\t", &context);
            if (!quests || !points || extra || (strcmp(quests, "0") != 0 && strcmp(quests, "1") != 0) ||
                (strcmp(points, "0") != 0 && strcmp(points, "1") != 0))
            {
                Reply(pipe, "ERROR INVALID_NAVIGATION_OPTIONS"); return;
            }
            g_showActiveQuestMarkers = strcmp(quests, "1") == 0;
            g_showPointOfInterestMarkers = strcmp(points, "1") == 0;
            ResetNavigationDiagnostics();
            char response[96]{};
            sprintf_s(response, "OK NAVIGATION QUESTS=%d POIS=%d", g_showActiveQuestMarkers.load() ? 1 : 0,
                g_showPointOfInterestMarkers.load() ? 1 : 0);
            Reply(pipe, response); return;
        }
        Reply(pipe, "ERROR UNKNOWN_COMMAND");
    }

    void ServeClient(HANDLE pipe)
    {
        GameplayAssists::Stop();
        std::array<char, kCommandSize + 16> buffer{};
        DWORD received = 0;
        while (!g_shutdownRequested && ReadFile(pipe, buffer.data(), static_cast<DWORD>(buffer.size() - 1), &received, nullptr) && received > 0)
        {
            buffer[received] = '\0';
            char* context = nullptr;
            for (char* line = strtok_s(buffer.data(), "\r\n", &context); line && !g_shutdownRequested; line = strtok_s(nullptr, "\r\n", &context)) ServeLine(pipe, line);
            buffer.fill(0);
        }
        GameplayAssists::Stop();
        ClearNavigationState();
    }

    DWORD WINAPI PipeThread(void*)
    {
        wchar_t name[128]{};
        swprintf_s(name, L"\\\\.\\pipe\\GrimDawnCompanion.%lu", GetCurrentProcessId());
        while (!g_shutdownRequested)
        {
            HANDLE pipe = CreateNamedPipeW(name, PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 8192, 8192, 0, nullptr);
            if (pipe == INVALID_HANDLE_VALUE) return GetLastError();
            if (ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED) ServeClient(pipe);
            FlushFileBuffers(pipe); DisconnectNamedPipe(pipe); CloseHandle(pipe);
        }
        if (g_updateTarget)
        {
            ClearNavigationState();
            MH_DisableHook(MH_ALL_HOOKS);
            MH_RemoveHook(MH_ALL_HOOKS);
            MH_Uninitialize();
        }
        DeleteCriticalSection(&g_queueLock);
        FreeLibraryAndExitThread(g_bridgeModule, 0);
        return 0;
    }

    DWORD WINAPI InitializeBridge(void*)
    {
        InitializeCriticalSection(&g_queueLock);
        auto engine = WaitForModule(L"Engine.dll"); auto game = WaitForModule(L"Game.dll");
        if (!engine || !game) g_error = 1;
        else
        {
            auto update = reinterpret_cast<void*>(GetProcAddress(engine, kUpdateExport));
            g_updateTarget = update;
            g_runCode = Resolve<RunCodeFunction>(engine, kRunCodeExport);
            if (!update || !g_runCode) g_error = 2;
            else if (MH_Initialize() != MH_OK) g_error = 3;
            else if (MH_CreateHook(update, &HookedUpdate, reinterpret_cast<void**>(&g_originalUpdate)) != MH_OK) g_error = 4;
            else if (MH_EnableHook(update) != MH_OK) g_error = 5;
            else { ResolveGameApi(engine, game); MinimapTelemetry::Initialize(engine); RadarCanvas::Initialize(engine); MasteryAudit::Initialize(engine, game); MasteryRespec::Initialize(engine, game); CharacterResource::Initialize(engine,game); ItemAffixes::Initialize(engine,game); CharacterLevel::Initialize(game); CharacterLevel::budgetCheck=&CharacterResource::CheckLevel; CharacterLevel::available=CharacterLevel::available && CharacterResource::available; GameplayAssists::Initialize(game, &GetPlayer, g_gameEngine); PersistentBookmarks::Initialize(engine, game); g_ready = true; }
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
