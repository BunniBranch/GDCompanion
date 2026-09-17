#pragma once
#include "MasteryGameUi.h"

// Guarded live reset. All state belongs to the game-thread queue; the
// pipe thread never touches the plan or result. No save-file editing occurs.
namespace MasteryRespec
{
    using namespace MasteryAudit;
    using VectorView = MasteryGameUi::VectorView;
    using ReadList = const VectorView*(__fastcall*)(void*);
    using IsDefault = bool(__fastcall*)(void*, unsigned int);
    using ChangeRank = bool(__fastcall*)(void*, unsigned int);
    using AddPoints = void(__fastcall*)(void*, unsigned int);
    inline ReadList skills = nullptr, itemSkills = nullptr;
    inline ReadUnsigned rank = nullptr, owner = nullptr, devotionSpent = nullptr;
    inline ReadUnsigned devotionPoints = nullptr, devotionTotal = nullptr, devotionMax = nullptr;
    using ReadAffinity = unsigned int(__fastcall*)(void*, unsigned int);
    inline ReadAffinity affinity = nullptr;
    inline auto readUi = MasteryGameUi::Read;
    inline auto resetDevotions = MasteryGameUi::ResetDevotions;
    inline auto devotionsCleared = MasteryGameUi::DevotionsCleared;
    inline auto clearClasses = MasteryGameUi::ClearClasses;
    inline ReadBool isMastery = nullptr, singlePlayer = nullptr, alive = nullptr, attacked = nullptr;
    inline ReadPointer gameInfo = nullptr;
    inline void** engineInstance = nullptr;
    inline IsDefault isDefault = nullptr;
    inline ReadSigned entrySet = nullptr;
    inline ChangeRank decrement = nullptr;
    inline AddPoints refund = nullptr;
    inline bool available = false;

    struct Entry { void* skill; unsigned int id, points, masteryId; bool mastery; int set; };
    struct Snapshot
    {
        void* player;
        unsigned int id, charLevel, points, regularPoints, masteryPoints, activeCount, allowedCount;
        unsigned int devotionUnspent, devotionInvested, devotionEarned, devotionLimit, affinities[5];
        MasteryGameUi::State ui;
        int set;
        wchar_t name[128];
        Entry entries[128];
        size_t count;
    };
    inline Snapshot prepared{};
    inline unsigned long long token = 0, issuedAt = 0;
    inline bool attempted = false;
    inline bool mutationBlocked = false;
    inline unsigned long long issuedToken = 0, recordedToken = 0;
    inline char lastResult[1024] = "ERROR MASTERY_NO_TRANSACTION";
    inline char recordedResult[1024] = "ERROR MASTERY_NO_TRANSACTION";

    inline void Initialize(HMODULE engine, HMODULE game)
    {
        Bind(skills, game, "?GetSkillList@SkillManager@GAME@@QEBAAEBV?$vector@PEAVSkill@GAME@@@mem@@XZ");
        Bind(itemSkills, game, "?GetItemSkillList@SkillManager@GAME@@QEBAAEBV?$vector@PEAVSkill@GAME@@@mem@@XZ");
        Bind(rank, game, "?GetSkillLevel@Skill@GAME@@QEBA?BIXZ");
        Bind(owner, game, "?GetMasteryId@Skill@GAME@@QEBA?BIXZ");
        Bind(isMastery, game, "?IsSkillTheMasterySkill@Skill@GAME@@QEBA?B_NXZ");
        Bind(isDefault, game, "?IsDefaultSkill@SkillManager@GAME@@QEBA_NI@Z");
        Bind(entrySet, game, "?GetSkillSet@Skill@GAME@@QEBAHXZ");
        Bind(devotionSpent, game, "?GetNumDevotionPointsSpent@SkillManager@GAME@@QEBAIXZ");
        Bind(devotionPoints, game, "?GetDevotionPoints@Character@GAME@@QEBA?BIXZ");
        Bind(devotionTotal, game, "?GetTotalDevotionPoints@Character@GAME@@QEBA?BIXZ");
        Bind(devotionMax, game, "?GetMaxDevotionPoints@Character@GAME@@QEBA?BIXZ");
        Bind(affinity, game, "?GetAffinity@Character@GAME@@QEBA?BIW4AffinityType@2@@Z");
        MasteryGameUi::Initialize(game);
        engineInstance = reinterpret_cast<void**>(GetProcAddress(engine, "?gEngine@GAME@@3PEAVEngine@1@EA"));
        Bind(gameInfo, engine, "?GetGameInfo@Engine@GAME@@QEAAPEAVGameInfo@2@XZ");
        Bind(singlePlayer, engine, "?GetIsSinglePlayer@GameInfo@GAME@@QEBA_NXZ");
        Bind(alive, game, "?IsPlayerAlive@GameEngine@GAME@@QEAA_NXZ");
        Bind(attacked, game, "?IsUnderAttack@Character@GAME@@QEBA?B_NXZ");
        Bind(decrement, game, "?DecrementSkillLevel@Skill@GAME@@UEAA_NI@Z");
        Bind(refund, game, "?AddSkillPoints@Character@GAME@@QEAAXI@Z");
        available = MasteryAudit::available && skills && itemSkills && rank && owner && isMastery &&
            isDefault && entrySet && devotionSpent && devotionPoints && devotionTotal && devotionMax && affinity &&
            MasteryGameUi::available && engineInstance && gameInfo && singlePlayer && alive && attacked && decrement && refund;
    }

    inline const char* Capture(void* engine, void* player, Snapshot& out)
    {
        out = {};
        if (!available) return "UNAVAILABLE";
        if (!engine || !player || loading(engine)) return "NO_READY_PLAYER";
        if (!engineInstance || !*engineInstance) return "NO_ENGINE_INSTANCE";
        auto info = gameInfo(*engineInstance);
        if (!info || !singlePlayer(info)) return "REQUIRES_SINGLE_PLAYER_SESSION";
        if (!alive(engine)) return "PLAYER_NOT_ALIVE";
        if (attacked(player)) return "PLAYER_IN_COMBAT";
        auto manager = skillManager(player);
        if (!manager) return "NO_SKILL_MANAGER";
        out.devotionInvested = devotionSpent(manager); out.devotionUnspent = devotionPoints(player);
        out.devotionEarned = devotionTotal(player); out.devotionLimit = devotionMax(player);
        if (out.devotionLimit > 100 || out.devotionInvested > out.devotionLimit || out.devotionUnspent > out.devotionLimit ||
            out.devotionInvested + out.devotionUnspent != out.devotionEarned || out.devotionEarned > out.devotionLimit)
            return "DEVOTION_BUDGET_MISMATCH";
        for (unsigned int i = 0; i < 5; ++i)
        {
            out.affinities[i] = affinity(player, i);
            if (out.affinities[i] > 1000 || (!out.devotionInvested && out.affinities[i])) return "DEVOTION_AFFINITY_MISMATCH";
        }
        const auto items = itemSkills(manager);
        if (Count(items)) return "REMOVE_ITEM_GRANTED_SKILLS_FIRST";
        out.player = player; out.id = objectId(player); out.charLevel = level(player);
        out.points = unspent(player); out.regularPoints = regular(manager); out.masteryPoints = mastery(manager);
        out.activeCount = active(player); out.allowedCount = allowed(player); out.set = skillSet(manager);
        if (out.set != 0 || out.allowedCount > 2 || out.activeCount > 2 || out.points > 10000 ||
            out.regularPoints > 1000 || out.masteryPoints > 100 ||
            out.points + out.regularPoints + out.masteryPoints > 10000 || !out.id) return "UNSUPPORTED_CHARACTER_STATE";
        const auto name = playerName(player);
        if (!name) return "NO_PLAYER_NAME";
        size_t length = 0;
        for (; length < 127 && name[length]; ++length) out.name[length] = name[length];
        if (!length || name[length]) return "INVALID_PLAYER_NAME";
        const auto list = skills(manager);
        const auto count = Count(list);
        if (count > 4096) return "INVALID_SKILL_LIST";
        if (const auto error = readUi(engine, out.id, list, out.devotionInvested, out.ui)) return error;
        if (out.ui.selected < out.activeCount || out.ui.selected > out.allowedCount) return "CLASS_SLOT_AUDIT_MISMATCH";
        unsigned int summedRegular = 0, summedMastery = 0, countedMasteries = 0;
        for (size_t i = 0; i < count; ++i)
        {
            auto skill = list->begin[i];
            if (!skill) return "NULL_SKILL";
            const auto id = objectId(skill), points = rank(skill);
            if (isDefault(manager, id) || !points) continue;
            bool devotion = false;
            for (size_t j = 0; j < out.ui.count; ++j) if (out.ui.stars[j].id == id) { devotion = true; break; }
            if (devotion) continue;
            Entry entry{skill, id, points, owner(skill), isMastery(skill), entrySet(skill)};
            // Game-owned point accounting excludes non-mastery abilities with
            // no mastery owner. Preserve these ranks (e.g. innate actions).
            if (!entry.mastery && !entry.masteryId) continue;
            if (entry.set != out.set) return "ALTERNATE_SKILL_SET_INVESTMENT";
            if (points > 100 || out.count == 128) return "UNSUPPORTED_RANK_COUNT";
            for (size_t j = 0; j < out.count; ++j)
                if (out.entries[j].id == id) return "DUPLICATE_SKILL";
            out.entries[out.count++] = entry;
            if (entry.mastery) { summedMastery += points; ++countedMasteries; }
            else summedRegular += points;
        }
        if (summedRegular != out.regularPoints || summedMastery != out.masteryPoints || countedMasteries != out.activeCount)
            return "RANK_TOTALS_DO_NOT_MATCH_GAME_AUDIT";
        for (size_t i = 0; i < out.count; ++i)
        {
            if (out.entries[i].mastery) continue;
            bool found = false;
            for (size_t j = 0; j < out.count; ++j)
                if (out.entries[j].mastery && out.entries[j].id == out.entries[i].masteryId) found = true;
            if (!found) return "MISSING_MASTERY_OWNER";
        }
        return nullptr;
    }

    inline bool Same(const Snapshot& a, const Snapshot& b)
    {
        if (a.player != b.player || a.id != b.id || a.charLevel != b.charLevel || a.points != b.points ||
            a.regularPoints != b.regularPoints || a.masteryPoints != b.masteryPoints || a.activeCount != b.activeCount ||
            a.allowedCount != b.allowedCount || a.set != b.set || wcscmp(a.name, b.name) || a.count != b.count ||
            a.devotionUnspent != b.devotionUnspent || a.devotionInvested != b.devotionInvested ||
            a.devotionEarned != b.devotionEarned || a.devotionLimit != b.devotionLimit || !MasteryGameUi::Same(a.ui, b.ui)) return false;
        for (size_t i = 0; i < 5; ++i) if (a.affinities[i] != b.affinities[i]) return false;
        for (size_t i = 0; i < a.count; ++i)
        {
            const auto& x = a.entries[i]; const auto& y = b.entries[i];
            if (x.skill != y.skill || x.id != y.id || x.points != y.points || x.masteryId != y.masteryId ||
                x.mastery != y.mastery || x.set != y.set) return false;
        }
        return true;
    }

    inline void Run(void* engine, void* player, const char* operation, unsigned long long requestedToken,
        unsigned long long deadline, char* response, size_t size)
    {
        __try
        {
            if (strcmp(operation, "STATUS") == 0)
            {
                if (recordedToken && recordedToken == requestedToken) strcpy_s(response, size, recordedResult);
                else if (!token || token != requestedToken) strcpy_s(response, size, "ERROR MASTERY_UNKNOWN_TRANSACTION");
                else strcpy_s(response, size, lastResult);
                return;
            }
            if (strcmp(operation, "PREPARE") == 0)
            {
                if (mutationBlocked) { strcpy_s(response, size, "ERROR MASTERY_SESSION_LOCKED_AFTER_UNVERIFIED_RESET"); return; }
                // Cancel an uncommitted plan, but retain the last attempted
                // transaction's outcome even when a later preflight fails.
                if (!attempted) token = 0;
                Snapshot current{};
                if (const auto error = Capture(engine, player, current))
                { sprintf_s(response, size, "ERROR MASTERY_%s", error); return; }
                if (!current.count && !current.devotionInvested && !current.ui.selected)
                { strcpy_s(response, size, "ERROR MASTERY_NOTHING_TO_RESET"); return; }
                prepared = current;
                LARGE_INTEGER counter{}; QueryPerformanceCounter(&counter);
                const auto clockToken = static_cast<unsigned long long>(counter.QuadPart);
                issuedToken = clockToken > issuedToken ? clockToken : issuedToken + 1;
                token = issuedToken;
                attempted = false;
                issuedAt = GetTickCount64();
                sprintf_s(lastResult, "OK FULL_RESPEC_PREPARED %llu %u %u %u %u %u %u %u", token, current.id,
                    current.points, current.regularPoints + current.masteryPoints, current.activeCount,
                    current.devotionUnspent, current.devotionInvested, current.ui.selected);
                strcpy_s(response, size, lastResult); return;
            }
            if (strcmp(operation, "COMMIT") != 0)
            { strcpy_s(response, size, "ERROR MASTERY_UNKNOWN_OPERATION"); return; }
            // Replaying the last attempted token returns its recorded outcome,
            // even after a new plan is prepared. Older tokens are rejected.
            if (recordedToken && recordedToken == requestedToken)
            { strcpy_s(response, size, recordedResult); return; }
            if (!token || token != requestedToken) { strcpy_s(response, size, "ERROR MASTERY_UNKNOWN_TRANSACTION"); return; }
            if (attempted) { strcpy_s(response, size, lastResult); return; } // Never mutate twice.
            if (GetTickCount64() > deadline || GetTickCount64() - issuedAt > 300000)
            { token = 0; strcpy_s(response, size, "ERROR MASTERY_PLAN_EXPIRED_NO_CHANGES"); return; }
            Snapshot current{};
            if (const auto error = Capture(engine, player, current))
            { token = 0; sprintf_s(response, size, "ERROR MASTERY_%s_NO_CHANGES", error); return; }
            if (!Same(current, prepared))
            { token = 0; strcpy_s(response, size, "ERROR MASTERY_CHARACTER_CHANGED_NO_CHANGES"); return; }
            attempted = true;
            mutationBlocked = true;
            strcpy_s(lastResult, "ERROR MASTERY_PARTIAL_OR_UNKNOWN_DO_NOT_RETRY");
            recordedToken = token;
            strcpy_s(recordedResult, lastResult);
            const auto expected = current.points + current.regularPoints + current.masteryPoints;
            if (current.devotionInvested) resetDevotions(current.ui);
            // The game-owned devotion reset refunds its own pool; never add a
            // second refund. Do not touch mastery ranks if this stage fails.
            if (devotionSpent(skillManager(player)) || devotionPoints(player) != current.devotionEarned ||
                devotionTotal(player) != current.devotionEarned || unspent(player) != current.points ||
                !devotionsCleared(current.ui))
            { strcpy_s(response, size, lastResult); return; }
            for (unsigned int i = 0; i < 5; ++i)
                if (affinity(player, i)) { strcpy_s(response, size, lastResult); return; }
            // Regular skills first, mastery bars last. Native decrement runs
            // rank-change notifications and removes zero-rank active entries.
            for (int pass = 0; pass < 2; ++pass)
                for (size_t i = current.count; i-- > 0;)
                {
                    const auto& entry = current.entries[i];
                    if (entry.mastery != (pass == 1)) continue;
                    const auto priorPoints = unspent(player);
                    if (rank(entry.skill) != entry.points || !decrement(entry.skill, entry.points) || rank(entry.skill) != 0)
                    { strcpy_s(response, size, lastResult); return; }
                    // This export is a rank decrement, not the spirit-guide
                    // reclamation command. Refuse any unexpected point change.
                    if (unspent(player) != priorPoints)
                    { strcpy_s(response, size, lastResult); return; }
                    refund(player, entry.points);
                    if (unspent(player) != priorPoints + entry.points)
                    { strcpy_s(response, size, lastResult); return; }
                }
            // Verify the point mutation before invoking the game's Undo Class
            // pane replacement. Never clear a slot with remaining investment.
            auto manager = skillManager(player);
            if (regular(manager) || mastery(manager) || active(player) || unspent(player) != expected ||
                !clearClasses(current.ui))
            { strcpy_s(response, size, lastResult); return; }
            Snapshot after{};
            if (Capture(engine, player, after) || after.count || after.regularPoints || after.masteryPoints ||
                after.activeCount || after.points != expected || after.ui.selected || after.devotionInvested ||
                after.devotionUnspent != current.devotionEarned || after.devotionEarned != current.devotionEarned ||
                after.devotionLimit != current.devotionLimit || after.id != current.id || after.player != current.player ||
                after.charLevel != current.charLevel || after.allowedCount != current.allowedCount || after.set != current.set ||
                wcscmp(after.name, current.name) || !devotionsCleared(current.ui))
            { strcpy_s(response, size, lastResult); return; }
            sprintf_s(lastResult, "OK FULL_RESPEC_RESET %llu %u %u %u 0", token, after.id, after.points, after.devotionUnspent);
            strcpy_s(recordedResult, lastResult);
            // Only complete post-mutation verification permits a new plan.
            mutationBlocked = false;
            strcpy_s(response, size, lastResult);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            // A fault after the first write permanently closes this bridge's
            // mutation gate. Never infer success, save, or add a guessed refund.
            if (mutationBlocked) strcpy_s(response, size, recordedResult);
            else { token = 0; strcpy_s(response, size, "ERROR MASTERY_PREFLIGHT_READ_FAILED_NO_CHANGES"); }
        }
    }
}
