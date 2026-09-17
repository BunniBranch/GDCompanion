#pragma once

// Read-only first stage of live mastery respec. Deliberately resolves no rank,
// point, class, save, or skill-manager mutation functions. A point total alone
// cannot prove that clearing a mastery leaves a valid character.
namespace MasteryAudit
{
    using ReadUnsigned = unsigned int(__fastcall*)(void*);
    using ReadSigned = int(__fastcall*)(void*);
    using ReadPointer = void*(__fastcall*)(void*);
    using ReadName = const wchar_t*(__fastcall*)(void*);
    using ReadBool = bool(__fastcall*)(void*);
    inline ReadPointer skillManager = nullptr;
    inline ReadName playerName = nullptr;
    inline ReadUnsigned objectId = nullptr, level = nullptr, unspent = nullptr;
    inline ReadUnsigned regular = nullptr, mastery = nullptr, active = nullptr, allowed = nullptr;
    inline ReadSigned skillSet = nullptr;
    inline ReadBool loading = nullptr;
    inline bool available = false;

    template <typename T> void Bind(T& target, HMODULE module, const char* symbol)
    {
        target = reinterpret_cast<T>(GetProcAddress(module, symbol));
    }

    inline void Initialize(HMODULE engine, HMODULE game)
    {
        Bind(skillManager, game, "?GetSkillManager@Character@GAME@@QEAAAEAVSkillManager@2@XZ");
        Bind(playerName, game, "?GetPlayerName@Player@GAME@@QEBAPEBGXZ");
        Bind(objectId, engine, "?GetObjectId@Object@GAME@@QEBAIXZ");
        Bind(level, game, "?GetCharLevel@Character@GAME@@QEBA?BIXZ");
        Bind(unspent, game, "?GetSkillPoints@Character@GAME@@QEBA?BIXZ");
        Bind(regular, game, "?GetNumRegularSkillPoints@SkillManager@GAME@@QEBAIXZ");
        Bind(mastery, game, "?GetNumMasteryPoints@SkillManager@GAME@@QEBAIXZ");
        Bind(active, game, "?GetSkillMasteriesActive@Character@GAME@@QEBA?BIXZ");
        Bind(allowed, game, "?GetSkillMasteriesAllowed@Character@GAME@@QEBA?BIXZ");
        Bind(skillSet, game, "?GetCurrentSkillSet@SkillManager@GAME@@QEBAHXZ");
        Bind(loading, game, "?IsGameLoading@GameEngine@GAME@@QEBA_NXZ");
        available = skillManager && playerName && objectId && level && unspent && regular &&
            mastery && active && allowed && skillSet && loading;
    }

    // Called only by the existing game-thread work queue. No cross-thread
    // traversal, game allocator ownership, structure offsets, or game writes.
    inline void Read(void* gameEngine, void* player, char* response, size_t size)
    {
        if (!available) { strcpy_s(response, size, "ERROR MASTERY_AUDIT_UNAVAILABLE"); return; }
        __try
        {
            if (!gameEngine || !player || loading(gameEngine))
            { strcpy_s(response, size, "ERROR MASTERY_AUDIT_NO_READY_PLAYER"); return; }
            auto manager = skillManager(player);
            auto name = playerName(player);
            if (!manager || !name || !name[0])
            { strcpy_s(response, size, "ERROR MASTERY_AUDIT_INVALID_PLAYER"); return; }
            // Hex UTF-16 keeps names out of the line-based protocol grammar.
            char encodedName[513]{};
            size_t length = 0;
            for (; length < 128 && name[length]; ++length)
                sprintf_s(encodedName + length * 4, sizeof(encodedName) - length * 4, "%04X", static_cast<unsigned int>(name[length]));
            if (length == 128)
            { strcpy_s(response, size, "ERROR MASTERY_AUDIT_NAME_TOO_LONG"); return; }
            sprintf_s(response, size, "OK MASTERY_AUDIT 1 %u %u %u %u %u %u %u %d %s",
                objectId(player), level(player), unspent(player), regular(manager), mastery(manager),
                active(player), allowed(player), skillSet(manager), encodedName);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            strcpy_s(response, size, "ERROR MASTERY_AUDIT_READ_FAILED");
        }
    }
}
