#pragma once
#include "SupportedBuilds.h"

// Build-pinned adapter for the game's devotion-reset and Undo Class UI paths.
// All calls/reads run on the existing game-thread queue, under its SEH guard.
// Offsets identify UI objects only; no field or save-file writes are performed.
namespace MasteryGameUi
{
    using namespace MasteryAudit;
    struct VectorView { void** begin; void** end; void** capacity; };
    struct Star
    {
        void* skill;
        unsigned int id, points, experience, devotionLevel, parent;
        void* parentSkill;
    };
    struct State
    {
        void* ui;
        void* window;
        void* panes[2];
        unsigned int selected;
        Star stars[1024];
        size_t count;
    };
    inline ReadPointer getUi = nullptr;
    inline ReadPointer autoCast = nullptr;
    inline ReadUnsigned rank = nullptr, experience = nullptr, devotionLevel = nullptr, parent = nullptr;
    inline unsigned char* executable = nullptr;
    inline size_t uiVtableRva = 0, skillWindowVtableRva = 0, devotionVtableRva = 0;
    inline size_t selectedPaneVtableRva = 0, emptyPaneVtableRva = 0;
    inline size_t resetRva = 0, setPaneRva = 0;
    inline bool available = false;

    template<typename T> T Field(void* object, size_t offset)
    { return *reinterpret_cast<T*>(static_cast<unsigned char*>(object) + offset); }
    inline void* At(void* object, size_t offset) { return static_cast<unsigned char*>(object) + offset; }
    inline bool Vtable(void* object, size_t rva) { return object && Field<void*>(object, 0) == executable + rva; }
    inline size_t Count(const VectorView* list)
    {
        if (!list) return 4097;
        const auto first = reinterpret_cast<uintptr_t>(list->begin), last = reinterpret_cast<uintptr_t>(list->end);
        const auto capacity = reinterpret_cast<uintptr_t>(list->capacity);
        if (last < first || capacity < last || (last - first) % sizeof(void*) || (last - first) / sizeof(void*) > 4096)
            return 4097;
        return (last - first) / sizeof(void*);
    }
    inline bool Fingerprint(size_t rva, size_t length, unsigned long long expected)
    {
        unsigned long long value = 14695981039346656037ULL;
        for (size_t i = 0; i < length; ++i) value = (value ^ executable[rva + i]) * 1099511628211ULL;
        return value == expected;
    }
    inline void Initialize(HMODULE game)
    {
        Bind(getUi, game, "?GetUI@GameEngine@GAME@@QEBAPEAVGameUIInterface@2@XZ");
        Bind(rank, game, "?GetSkillLevel@Skill@GAME@@QEBA?BIXZ");
        Bind(experience, game, "?GetDevotionExperience@Skill@GAME@@QEBA?BIXZ");
        Bind(devotionLevel, game, "?GetDevotionLevel@Skill@GAME@@QEBA?BIXZ");
        Bind(parent, game, "?GetDevotionParent@Skill@GAME@@QEBA?BIXZ");
        Bind(autoCast, game, "?GetAutoCastSkill@Skill@GAME@@QEBAPEAV12@XZ");
        executable = reinterpret_cast<unsigned char*>(GetModuleHandleW(nullptr));
        __try
        {
            const auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(executable);
            const auto pe = reinterpret_cast<IMAGE_NT_HEADERS64*>(executable + dos->e_lfanew);
            const auto build = SupportedBuilds::Executable(reinterpret_cast<HMODULE>(executable));
            if (build == SupportedBuilds::Build::V1308 &&
                Fingerprint(0x18C9D0, 0x451, 0x696D9AB0E6A06162ULL) &&
                Fingerprint(0x27C580, 0x161, 0x4D8A99E12F71E8A5ULL) &&
                Fingerprint(0x21B060, 0xC, 0xE769D51242C91AF2ULL))
            {
                uiVtableRva = 0x31A680; skillWindowVtableRva = 0x31D8A8; devotionVtableRva = 0x315D80;
                selectedPaneVtableRva = 0x31BD18; emptyPaneVtableRva = 0x31A1D8;
                resetRva = 0x21B060; setPaneRva = 0x27C580;
            }
            else if (build == SupportedBuilds::Build::V1311 &&
                Fingerprint(0x18CB40, 0x451, 0x41B5985FF44476FFULL) &&
                Fingerprint(0x27C720, 0x161, 0xA045AC06411D9023ULL) &&
                Fingerprint(0x21B1F0, 0xC, 0xE2231407770620D2ULL))
            {
                uiVtableRva = 0x31A5B0; skillWindowVtableRva = 0x31D8E8; devotionVtableRva = 0x315DC8;
                selectedPaneVtableRva = 0x31BD48; emptyPaneVtableRva = 0x31A220;
                resetRva = 0x21B1F0; setPaneRva = 0x27C720;
            }
            available = getUi && rank && experience && devotionLevel && parent && autoCast &&
                dos->e_magic == IMAGE_DOS_SIGNATURE && pe->Signature == IMAGE_NT_SIGNATURE &&
                uiVtableRva && skillWindowVtableRva && devotionVtableRva && selectedPaneVtableRva &&
                emptyPaneVtableRva && resetRva && setPaneRva;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { available = false; }
    }

    inline const char* Read(void* engine, unsigned int playerId, const VectorView* skills, unsigned int spent, State& out)
    {
        out = {};
        if (!available) return "UNSUPPORTED_GAME_UI_BUILD";
        out.ui = getUi(engine);
        if (!Vtable(out.ui, uiVtableRva) || Field<void*>(Field<void*>(out.ui, 0), 0xE8) != executable + resetRva)
            return "UNEXPECTED_GAME_UI";
        out.window = At(out.ui, 0x3FB88);
        auto devotion = At(out.ui, 0x81308);
        if (!Vtable(out.window, skillWindowVtableRva) || !Vtable(devotion, devotionVtableRva) ||
            Field<unsigned int>(out.window, 0x98) != playerId || Field<unsigned int>(devotion, 0x2410) != playerId)
            return "OPEN_SKILLS_AND_DEVOTION_FOR_CURRENT_PLAYER_FIRST";
        if (Field<unsigned char>(devotion, 0x2419) || Field<int>(out.window, 0x263C) != -1)
            return "CLOSE_PENDING_SKILL_DIALOG_FIRST";
        for (size_t i = 0; i < 2; ++i)
        {
            auto pane = out.panes[i] = Field<void*>(out.window, 0x100 + 8 * i);
            if (!pane) continue; // A locked/not-yet-created slot is not selected.
            const bool selected = Vtable(pane, selectedPaneVtableRva);
            if ((!selected && !Vtable(pane, emptyPaneVtableRva)) || Field<unsigned int>(pane, 0x38) != playerId ||
                Field<int>(pane, 0x48) != static_cast<int>(i) || Field<void*>(pane, 0x50) != out.window ||
                Field<unsigned char>(pane, 0x60)) return "UNEXPECTED_CLASS_SLOT_STATE";
            if (selected && Field<unsigned char>(pane, 0x1E4D)) return "SKILL_UI_BUSY";
            out.selected += selected;
        }
        const auto constellations = static_cast<const VectorView*>(At(devotion, 0xA8));
        const auto constellationCount = Count(constellations), skillCount = Count(skills);
        if (!constellationCount || constellationCount > 256 || skillCount > 4096) return "INVALID_DEVOTION_UI_LIST";
        unsigned int sum = 0;
        for (size_t i = 0; i < constellationCount; ++i)
        {
            if (!constellations->begin[i]) return "NULL_CONSTELLATION";
            auto stars = static_cast<const VectorView*>(At(constellations->begin[i], 0x78));
            const auto count = Count(stars);
            if (count > 64) return "INVALID_CONSTELLATION";
            for (size_t j = 0; j < count; ++j)
            {
                if (!stars->begin[j]) return "NULL_DEVOTION_STAR";
                const auto id = Field<unsigned int>(stars->begin[j], 0x108);
                if (!id) continue; // No game skill object exists for this unallocated UI star.
                void* skill = nullptr;
                for (size_t k = 0; k < skillCount; ++k)
                    if (skills->begin[k] && objectId(skills->begin[k]) == id) { skill = skills->begin[k]; break; }
                if (!skill || out.count == 1024) return "DEVOTION_UI_SKILL_MISMATCH";
                for (size_t k = 0; k < out.count; ++k)
                    if (out.stars[k].id == id) return "DUPLICATE_DEVOTION_STAR";
                auto& star = out.stars[out.count++];
                star = {skill, id, rank(skill), experience(skill), devotionLevel(skill), parent(skill)};
                if (star.points > 1 || (!star.points && star.parent)) return "UNSUPPORTED_DEVOTION_STAR_STATE";
                if (star.parent)
                {
                    for (size_t k = 0; k < skillCount; ++k)
                        if (skills->begin[k] && objectId(skills->begin[k]) == star.parent)
                        { star.parentSkill = skills->begin[k]; break; }
                    if (!star.parentSkill || autoCast(star.parentSkill) != skill) return "DEVOTION_BINDING_MISMATCH";
                }
                sum += star.points;
            }
        }
        // The UI reset must cover every invested point, not just visible stars.
        if (sum != spent) return "DEVOTION_UI_TOTAL_DOES_NOT_MATCH_GAME";
        return nullptr;
    }
    inline bool Same(const State& a, const State& b)
    {
        if (a.ui != b.ui || a.window != b.window || a.selected != b.selected || a.count != b.count ||
            a.panes[0] != b.panes[0] || a.panes[1] != b.panes[1]) return false;
        for (size_t i = 0; i < a.count; ++i)
        {
            const auto& x = a.stars[i]; const auto& y = b.stars[i];
            if (x.skill != y.skill || x.id != y.id || x.points != y.points || x.experience != y.experience ||
                x.devotionLevel != y.devotionLevel || x.parent != y.parent || x.parentSkill != y.parentSkill) return false;
        }
        return true;
    }
    inline bool DevotionsCleared(const State& before)
    {
        for (size_t i = 0; i < before.count; ++i)
        {
            const auto& star = before.stars[i];
            if (objectId(star.skill) != star.id || rank(star.skill) || parent(star.skill) ||
                experience(star.skill) != star.experience || devotionLevel(star.skill) != star.devotionLevel) return false;
            if (star.parentSkill && (objectId(star.parentSkill) != star.parent || autoCast(star.parentSkill))) return false;
        }
        return true;
    }
    inline void ResetDevotions(const State& state)
    {
        // GameUIInterface::reset devotion (also used by the game's reset item).
        // It clears star ranks/bindings/affinities and refunds points itself.
        using Reset = void(__fastcall*)(void*);
        reinterpret_cast<Reset>(executable + resetRva)(state.ui);
    }
    inline bool ClearClasses(const State& state)
    {
        // Same pane replacement as Undo Class. Called only AFTER rank/refund
        // verification. Game owns deferred disposal of the old UI panes.
        using SetPane = void(__fastcall*)(void*, int, int);
        auto setPane = reinterpret_cast<SetPane>(executable + setPaneRva);
        for (int i = 0; i < 2; ++i)
        {
            auto pane = Field<void*>(state.window, 0x100 + 8 * i);
            if (Vtable(pane, selectedPaneVtableRva)) setPane(state.window, i, 0x50);
            pane = Field<void*>(state.window, 0x100 + 8 * i);
            if (pane && !Vtable(pane, emptyPaneVtableRva)) return false;
        }
        return true;
    }
}
