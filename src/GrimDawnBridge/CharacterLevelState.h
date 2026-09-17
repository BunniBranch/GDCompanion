#pragma once
#include <algorithm>
#include <cstdint>
#include <cmath>
#include <cstring>

namespace CharacterLevel
{
    constexpr unsigned HardLimit = 1000; // Decoder safety bound; only the loaded progression sets the cap.
    struct Snapshot
    {
        uintptr_t player = 0;
        unsigned id = 0, level = 0, xp = 0, cap = 0, skill = 0, attribute = 0, devotion = 0;
        float xpBonus = 0;
        char name[513]{};
    };
    inline const char* Validate(const Snapshot& value, unsigned target)
    {
        if (!value.player || !value.id || !value.level || value.level > HardLimit || !value.cap || value.cap > HardLimit)
            return "ERROR LEVEL_INVALID_CHARACTER";
        if (target <= value.level) return "ERROR LEVEL_RAISE_ONLY";
        if (target > value.cap) return "ERROR LEVEL_EXCEEDS_GAME_CAP";
        // IncrementCharLevel routes through ReceiveExperience, which applies
        // this bonus. Refuse it instead of risking overshoot or altering buffs.
        if (!std::isfinite(value.xpBonus) || value.xpBonus != 0) return "ERROR LEVEL_REMOVE_XP_BONUSES";
        return nullptr;
    }
    struct Transaction
    {
        Snapshot prepared{};
        unsigned target = 0;
        unsigned long long nextToken = 0, token = 0, expires = 0;
        bool blocked = false;
        const char* Prepare(const Snapshot& value, unsigned desired, unsigned long long now)
        {
            token = 0;
            if (blocked) return "ERROR LEVEL_UNVERIFIED_RESTART_REQUIRED";
            if (const auto error = Validate(value, desired)) return error;
            prepared = value; target = desired; token = ++nextToken; expires = now + 60000;
            return nullptr;
        }
        const char* Consume(const Snapshot& value, unsigned long long supplied, unsigned long long now)
        {
            if (blocked) return "ERROR LEVEL_UNVERIFIED_RESTART_REQUIRED";
            if (!token || supplied != token) return "ERROR LEVEL_STALE_CONFIRMATION";
            token = 0; // Consume before any game mutation; replay is never accepted.
            if (now >= expires) return "ERROR LEVEL_CONFIRMATION_EXPIRED";
            if (value.player != prepared.player || value.id != prepared.id || value.level != prepared.level ||
                value.xp != prepared.xp || value.cap != prepared.cap || value.skill != prepared.skill ||
                value.attribute != prepared.attribute || value.devotion != prepared.devotion || strcmp(value.name, prepared.name) != 0)
                return "ERROR LEVEL_CHARACTER_CHANGED_INSPECT_AGAIN";
            return Validate(value, target);
        }
    };
}
