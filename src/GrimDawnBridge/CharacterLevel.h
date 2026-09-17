#pragma once
#include "CharacterLevelState.h"

namespace CharacterLevel
{
    using ReadUnsigned = unsigned(__fastcall*)(void*);
    using Attribute = float(__fastcall*)(void*, int);
    using Action = void(__fastcall*)(void*);
    inline ReadUnsigned xp = nullptr, nextXp = nullptr, attributePoints = nullptr;
    inline Attribute totalAttribute = nullptr;
    inline Action increment = nullptr;
    inline bool available = false;
    inline const char* (*budgetCheck)(void*,void*,unsigned) = nullptr;
    inline Transaction transaction;

    inline void Initialize(HMODULE game)
    {
        MasteryAudit::Bind(xp, game, "?GetExperiencePoints@Character@GAME@@QEBA?BIXZ");
        MasteryAudit::Bind(nextXp, game, "?GetNextLevelExperience@Character@GAME@@QEBA?BIXZ");
        MasteryAudit::Bind(attributePoints, game, "?GetModifierPoints@Character@GAME@@QEBA?BIXZ");
        MasteryAudit::Bind(totalAttribute, game, "?GetTotalCharAttribute@Character@GAME@@QEBAMW4CharAttributeType@2@@Z");
        MasteryAudit::Bind(increment, game, "?IncrementCharLevel@Character@GAME@@QEAAXXZ");
        auto base = reinterpret_cast<const unsigned char*>(game);
        auto pe = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + reinterpret_cast<const IMAGE_DOS_HEADER*>(base)->e_lfanew);
        available = MasteryRespec::available && xp && nextXp && attributePoints && totalAttribute && increment &&
            pe->FileHeader.TimeDateStamp == 0x6A85FBB3 && pe->OptionalHeader.SizeOfImage == 0xAB5000;
    }
    inline const char* Read(void* game, void* player, Snapshot& value)
    {
        if (!available) return "ERROR LEVEL_UNAVAILABLE";
        if (!game || !player || MasteryAudit::loading(game) || !MasteryRespec::alive(game)) return "ERROR LEVEL_NO_READY_CHARACTER";
        auto engine = MasteryRespec::engineInstance ? *MasteryRespec::engineInstance : nullptr;
        auto info = engine ? MasteryRespec::gameInfo(engine) : nullptr;
        if (!info || !MasteryRespec::singlePlayer(info)) return "ERROR LEVEL_SINGLE_PLAYER_ONLY";
        if (MasteryRespec::attacked(player)) return "ERROR LEVEL_IN_COMBAT";
        value.player = reinterpret_cast<uintptr_t>(player);
        value.id = MasteryAudit::objectId(player); value.level = MasteryAudit::level(player); value.xp = xp(player);
        // Exact supported image: IsMaxLevel compares this earned-level cap.
        value.cap = *reinterpret_cast<unsigned*>(static_cast<unsigned char*>(player) + 0x175C);
        value.skill = MasteryAudit::unspent(player); value.attribute = attributePoints(player);
        value.devotion = MasteryRespec::devotionPoints(player);
        value.xpBonus = totalAttribute(player, 0x37); // Same getter/id used by ReceiveExperience.
        auto name = MasteryAudit::playerName(player);
        if (!name || !name[0]) return "ERROR LEVEL_INVALID_CHARACTER";
        size_t i = 0;
        for (; i < 128 && name[i]; ++i) sprintf_s(value.name + i * 4, sizeof(value.name) - i * 4, "%04X", unsigned(name[i]));
        if (i == 128) return "ERROR LEVEL_INVALID_CHARACTER";
        return nullptr;
    }
    inline void Run(void* game, void* player, const char* operation, unsigned target, unsigned long long token,
        unsigned long long deadline, char* response, size_t size)
    {
        __try
        {
            if (GetTickCount64() > deadline) { strcpy_s(response, size, "ERROR LEVEL_REQUEST_EXPIRED"); return; }
            Snapshot value{};
            if (const auto error = Read(game, player, value)) { strcpy_s(response, size, error); return; }
            if (strcmp(operation, "PREPARE") == 0)
            {
                if (!budgetCheck) { strcpy_s(response,size,"ERROR LEVEL_BUDGET_UNAVAILABLE");return; }
                if (const auto error=budgetCheck(game,player,target)) { strcpy_s(response,size,error);return; }
                if (const auto error = transaction.Prepare(value, target, GetTickCount64())) { strcpy_s(response, size, error); return; }
                sprintf_s(response, size, "OK LEVEL_PREPARED %llu %u %u %u %u %s", transaction.token,
                    value.level, target, value.cap, value.id, value.name); return;
            }
            if (strcmp(operation, "COMMIT") != 0) { strcpy_s(response, size, "ERROR LEVEL_COMMAND"); return; }
            if (const auto error = transaction.Consume(value, token, GetTickCount64())) { strcpy_s(response, size, error); return; }
            if (!budgetCheck) { strcpy_s(response,size,"ERROR LEVEL_BUDGET_UNAVAILABLE");return; }
            if (const auto error=budgetCheck(game,player,transaction.target)) { strcpy_s(response,size,error);return; }
            transaction.blocked = true; // Any exception or unverified partial change blocks further writes.
            while (value.level < transaction.target)
            {
                const auto expectedXp = nextXp(player);
                if (GetTickCount64() > deadline || expectedXp <= value.xp) break;
                increment(player); // Native XP, normal point awards, skill/bio refresh and notifications.
                Snapshot after{};
                if (Read(game, player, after) || after.level != value.level + 1 || after.level > HardLimit ||
                    after.xp < expectedXp || after.skill < value.skill || after.attribute < value.attribute ||
                    after.devotion != value.devotion || after.xpBonus != 0 ||
                    after.cap != value.cap || after.player != value.player || after.id != value.id ||
                    budgetCheck(game,player,after.level)) break;
                value = after;
            }
            if (MasteryAudit::level(player) != transaction.target || value.level != transaction.target)
            { strcpy_s(response, size, "ERROR LEVEL_PARTIAL_OR_UNVERIFIED_DO_NOT_RETRY"); return; }
            transaction.blocked = false;
            sprintf_s(response, size, "OK LEVEL_SET %u %u %u", value.level, value.skill, value.attribute);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        { transaction.token = 0; transaction.blocked = true; strcpy_s(response, size, "ERROR LEVEL_UNVERIFIED_DO_NOT_RETRY"); }
    }
}
