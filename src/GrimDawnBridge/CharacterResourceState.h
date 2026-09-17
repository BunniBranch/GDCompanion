#pragma once
#include <array>
#include <cstdint>
#include <cstring>

namespace CharacterResource
{
    constexpr unsigned MoneyCap = 2000000000;
    constexpr unsigned MaxSupportedLevel = 1000, MaxPointBudget = 1000000;
    // Conservative tool ceiling, not a claim that every mod supports these totals.
    constexpr unsigned BypassPointBudget = 10000;
    struct Progression
    {
        unsigned attributePerLevel=0, initialSkills=0, skillCount=0;
        std::array<unsigned,MaxSupportedLevel> skills{};
        bool operator==(const Progression&) const = default;
        unsigned SkillAward(unsigned level) const
        { return skillCount && level>=2 ? skills[(level-2)<skillCount ? level-2 : skillCount-1] : 0; }
    };
    struct Budget { unsigned level, skill, attribute; };
    // Supported campaign data: level awards plus all one-time quest rewards
    // across Normal/Elite/Ultimate. See docs/character-resource-limits.md.
    inline Budget Endgame(unsigned expansion)
    {
        constexpr Budget budgets[]{{85,223,90},{100,244,105},{100,248,107},{100,250,109}};
        return expansion < 4 ? budgets[expansion] : Budget{};
    }
    struct Snapshot
    {
        uintptr_t player = 0;
        unsigned id = 0, level = 0, levelCap = 0, expansion = 0;
        std::array<unsigned,4> value{}, spent{}, cap{};
        unsigned devotionTotal = 0;
        Progression progression{};
        bool modded = false;
        char name[513]{};
    };
    inline unsigned EffectiveCap(const Snapshot& s, unsigned kind, bool bypass=false)
    { return kind>=4 ? 0 : bypass && kind && s.cap[kind]<BypassPointBudget ? BypassPointBudget : s.cap[kind]; }
    inline unsigned Available(const Snapshot& s, unsigned kind, bool bypass=false)
    { return kind < 4 && s.spent[kind] <= EffectiveCap(s,kind,bypass) ? EffectiveCap(s,kind,bypass) - s.spent[kind] : 0; }
    inline const char* Validate(const Snapshot& s, unsigned kind, unsigned target, bool bypass=false)
    {
        if (!s.player || !s.id || !s.level || s.level > s.levelCap || !s.name[0] || kind >= 4)
            return "ERROR RESOURCE_INVALID_CHARACTER";
        if (s.spent[kind] > EffectiveCap(s,kind,bypass) || target > Available(s, kind,bypass)) return "ERROR RESOURCE_EXCEEDS_POINT_BUDGET";
        if (kind==3 && static_cast<uint64_t>(s.spent[3])+s.value[3]!=s.devotionTotal)
            return "ERROR RESOURCE_INVALID_DEVOTION_ACCOUNTING";
        return nullptr;
    }
    inline bool Same(const Snapshot& a, const Snapshot& b)
    {
        return a.player==b.player && a.id==b.id && a.level==b.level && a.levelCap==b.levelCap &&
            a.expansion==b.expansion && a.value==b.value && a.spent==b.spent && a.cap==b.cap &&
            a.devotionTotal==b.devotionTotal && a.progression==b.progression && a.modded==b.modded && strcmp(a.name,b.name)==0;
    }
    inline bool CanAwardLevels(const Snapshot& s, unsigned target)
    {
        if (target<s.level || target>s.levelCap) return false;
        uint64_t skills=static_cast<uint64_t>(s.value[1])+s.spent[1];
        for (unsigned level=s.level+1;level<=target;level++) skills+=s.progression.SkillAward(level);
        return skills<=s.cap[1] && static_cast<uint64_t>(s.value[2])+s.spent[2]+
            static_cast<uint64_t>(target-s.level)*s.progression.attributePerLevel<=s.cap[2];
    }
    inline const char* ApplyProgression(Snapshot& s)
    {
        if (!s.levelCap || s.levelCap>MaxSupportedLevel || s.expansion>3 || s.cap[3]>MaxPointBudget ||
            s.progression.skillCount>MaxSupportedLevel || s.progression.attributePerLevel>MaxPointBudget ||
            s.progression.initialSkills>MaxPointBudget) return "ERROR RESOURCE_UNSUPPORTED_PROGRESSION";
        constexpr unsigned skillQuest[]{6,7,11,13}, attributeQuest[]{6,6,8,10};
        uint64_t skill=s.progression.initialSkills+skillQuest[s.expansion];
        uint64_t attrs=static_cast<uint64_t>(s.levelCap-1)*s.progression.attributePerLevel+attributeQuest[s.expansion];
        bool normal=s.levelCap==Endgame(s.expansion).level && s.cap[3]==(s.expansion ? 55u : 50u) &&
            s.progression.attributePerLevel==1 && s.progression.initialSkills==0;
        for(unsigned level=2;level<=s.levelCap;level++)
        {
            const auto award=s.progression.SkillAward(level);
            skill+=award;
            normal=normal && award==(level<=50 ? 3u : level<=90 ? 2u : 1u);
        }
        if(skill>MaxPointBudget || attrs>MaxPointBudget) return "ERROR RESOURCE_UNSUPPORTED_PROGRESSION";
        s.cap[1]=static_cast<unsigned>(skill);s.cap[2]=static_cast<unsigned>(attrs);
        s.modded=s.modded || !normal;
        return nullptr;
    }
    struct Transaction
    {
        Snapshot prepared{};
        unsigned kind = 0, target = 0;
        unsigned long long token = 0, next = 0, expires = 0;
        bool blocked = false;
        bool bypass = false;
        const char* Prepare(const Snapshot& s, unsigned k, unsigned desired, unsigned long long now, bool allowBypass=false)
        {
            token=0;
            if (blocked) return "ERROR RESOURCE_UNVERIFIED_RESTART_REQUIRED";
            if (auto error=Validate(s,k,desired,allowBypass)) return error;
            prepared=s;kind=k;target=desired;bypass=allowBypass;token=++next;expires=now+60000;return nullptr;
        }
        const char* Consume(const Snapshot& s, unsigned long long supplied, unsigned long long now)
        {
            if (blocked) return "ERROR RESOURCE_UNVERIFIED_RESTART_REQUIRED";
            if (!token || token!=supplied) return "ERROR RESOURCE_STALE_CONFIRMATION";
            token=0;
            if (now>=expires) return "ERROR RESOURCE_CONFIRMATION_EXPIRED";
            if (!Same(s,prepared)) return "ERROR RESOURCE_CHARACTER_CHANGED_INSPECT_AGAIN";
            return Validate(s,kind,target,bypass);
        }
    };
}
