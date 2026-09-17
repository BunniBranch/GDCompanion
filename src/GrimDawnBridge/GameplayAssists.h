#pragma once
#include "GameplayAssistState.h"

namespace GameplayAssists
{
    using Pointer = void*(__fastcall*)(void*);
    using LocalPlayer = void*(*)();
    using Boolean = bool(__fastcall*)(void*);
    using Scalar = float(__fastcall*)(void*);
    using SetScalar = void(__fastcall*)(void*, float);
    using SpeedFunction = float(__fastcall*)(void*, bool);
    using StartFunction = void(__fastcall*)(void*, bool);
    using RefreshFunction = void(__fastcall*)(void*, int);
    using Action = void(__fastcall*)(void*);
    inline Lease lease;
    inline std::atomic<bool> available{false};
    inline LocalPlayer localPlayer = nullptr;
    inline void** gameInstance = nullptr;
    inline Boolean originalInvincible = nullptr;
    inline SpeedFunction originalSpeed = nullptr;
    inline StartFunction originalStart = nullptr;
    inline SetScalar originalSubtract = nullptr, setMana = nullptr;
    inline Scalar manaLimit = nullptr, manaReserve = nullptr, currentMana = nullptr;
    inline Pointer skillOwner = nullptr;
    inline RefreshFunction endCooldown = nullptr, refreshCooldown = nullptr;
    inline Action forceSpeed = nullptr;
    inline std::atomic<void*> speedTouched{nullptr};
    inline unsigned lastSpeed = 100;
    inline bool refreshSpeedOnRespawn = false;

    inline void Stop() { lease.Stop(); }
    // Re-resolve the live player on every hook entry. Never dereference a stored
    // player/skill pointer after a load, death, or session transition.
    inline void* EligiblePlayer()
    {
        if (!available) return nullptr;
        __try
        {
            auto game = gameInstance ? *gameInstance : nullptr;
            auto engine = MasteryRespec::engineInstance ? *MasteryRespec::engineInstance : nullptr;
            if (!game || !engine || MasteryAudit::loading(game)) return nullptr;
            auto info = MasteryRespec::gameInfo(engine);
            // Death does not change authorization for this same loaded player.
            // Alive state gates effects separately, never the lease identity.
            if (!info || !MasteryRespec::singlePlayer(info)) return nullptr;
            return localPlayer();
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { Stop(); return nullptr; }
    }
    inline bool Applies(void* player, unsigned flag)
    {
        auto state = lease.Read(GetTickCount64());
        if (!(state.mask & flag)) return false;
        auto current = EligiblePlayer();
        if (!current || reinterpret_cast<uintptr_t>(current) != state.player)
        { Stop(); return false; }
        __try
        {
            if (MasteryAudit::objectId(current) != state.id) { Stop(); return false; }
            return current == player && MasteryRespec::alive(*gameInstance);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { Stop(); return false; }
    }
    inline bool __fastcall InvincibleHook(void* player)
    {
        // Preserve game-owned invincibility, including travel protection.
        return originalInvincible(player) || Applies(player, Invincible);
    }
    inline float __fastcall SpeedHook(void* player, bool uncapped)
    {
        const float normal = originalSpeed(player, uncapped);
        if (!Applies(player, Speed) || !std::isfinite(normal) || normal <= 0) return normal;
        const auto state = lease.Read(GetTickCount64());
        if (!(state.mask & Speed)) return normal;
        speedTouched = player;
        return normal * (static_cast<float>(state.speed) / 100.0f);
    }
    inline void __fastcall SubtractHook(void* player, float amount)
    {
        if (std::isfinite(amount) && amount > 0 && Applies(player, Energy)) return;
        originalSubtract(player, amount);
    }
    inline bool OwnsSkill(void* skill)
    {
        auto player = EligiblePlayer();
        if (!player || !Applies(player, Cooldown)) return false;
        __try { return skillOwner(skill) == MasteryAudit::skillManager(player); }
        __except (EXCEPTION_EXECUTE_HANDLER) { Stop(); return false; }
    }
    inline void __fastcall StartHook(void* skill, bool argument)
    {
        originalStart(skill, argument);
        if (OwnsSkill(skill)) endCooldown(skill, 0);
    }
    // Only called on the existing game-thread queue/update path.
    inline void Tick()
    {
        if (!available) return;
        __try
        {
            auto player = EligiblePlayer();
            const auto id = player ? MasteryAudit::objectId(player) : 0;
            lease.Context(reinterpret_cast<uintptr_t>(player), id, GetTickCount64());
            auto state = lease.Read(GetTickCount64());
            const auto speed = (state.mask & Speed) ? state.speed : 100;
            auto touched = speedTouched.load();
            if (touched && touched != player) speedTouched = nullptr;
            if (!player) { lastSpeed = 100; refreshSpeedOnRespawn = false; return; }
            if (!MasteryRespec::alive(*gameInstance))
            {
                // Continue STATUS/PULSE for the same character while dead, but
                // do not refill energy, clear cooldowns or refresh corpse speed.
                refreshSpeedOnRespawn = true;
                return;
            }
            if ((speed != lastSpeed) || (touched == player && speed == 100) ||
                (refreshSpeedOnRespawn && ((state.mask & Speed) || touched == player)))
            {
                forceSpeed(player);
                if (speed == 100) speedTouched = nullptr;
            }
            refreshSpeedOnRespawn = false;
            lastSpeed = speed;
            if (player && (state.mask & Energy))
            {
                const float limit = manaLimit(player), reserve = manaReserve(player), current = currentMana(player);
                if (!std::isfinite(limit) || !std::isfinite(reserve) || !std::isfinite(current) || limit < 0 || reserve < 0)
                { Stop(); return; }
                const float usable = (std::max)(0.0f, limit - reserve);
                if (current < usable) setMana(player, usable);
            }
            if (player && (state.mask & Cooldown)) refreshCooldown(MasteryAudit::skillManager(player), 0);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { Stop(); }
    }
    inline void Run(const char* operation, uint64_t revision, unsigned mask, unsigned speed,
        uint64_t deadline, char* response, size_t size)
    {
        Tick();
        if (!available) { strcpy_s(response, size, "ERROR ASSISTS_UNSUPPORTED_BUILD"); return; }
        const auto now = GetTickCount64();
        if (strcmp(operation, "SET") == 0 && (now >= deadline || !lease.Set(revision, mask, speed, now)))
        { strcpy_s(response, size, "ERROR ASSISTS_CONTEXT_CHANGED_OR_EXPIRED"); return; }
        if (strcmp(operation, "PULSE") == 0 && (now >= deadline || !lease.Pulse(revision, now)))
        { strcpy_s(response, size, "ERROR ASSISTS_EXPIRED_OR_STOPPED"); return; }
        Tick();
        auto state = lease.Read(GetTickCount64());
        if (strcmp(operation, "OFF") == 0 && speedTouched.load())
        { strcpy_s(response, size, "ERROR ASSISTS_SPEED_CLEANUP_PENDING"); return; }
        sprintf_s(response, size, "OK ASSISTS %llu %u %u %u", state.revision, state.id, state.mask, state.speed);
    }
    inline bool Fingerprint(HMODULE module, void* target, unsigned rva, uint32_t expected)
    {
        if (target != reinterpret_cast<unsigned char*>(module) + rva) return false;
        uint32_t hash = 2166136261u;
        for (unsigned i = 0; i < 32; ++i) hash = (hash ^ reinterpret_cast<unsigned char*>(target)[i]) * 16777619u;
        return hash == expected;
    }
    inline void Initialize(HMODULE game, LocalPlayer player, void** instance)
    {
        localPlayer = player; gameInstance = instance;
        auto bytes = reinterpret_cast<unsigned char*>(game);
        auto pe = reinterpret_cast<IMAGE_NT_HEADERS64*>(bytes + reinterpret_cast<IMAGE_DOS_HEADER*>(bytes)->e_lfanew);
        if (pe->FileHeader.TimeDateStamp != 0x6A85FBB3 || pe->OptionalHeader.SizeOfImage != 0xAB5000 ||
            !MasteryAudit::available || !MasteryRespec::engineInstance || !MasteryRespec::gameInfo ||
            !MasteryRespec::singlePlayer || !MasteryRespec::alive) return;
        using MasteryAudit::Bind;
        Bind(setMana, game, "?SetCurrentMana@Character@GAME@@QEAAXM@Z");
        Bind(manaLimit, game, "?GetManaLimit@Character@GAME@@QEBA?BMXZ");
        Bind(manaReserve, game, "?GetReserveMana@Character@GAME@@QEBA?BMXZ");
        Bind(currentMana, game, "?GetCurrentMana@Character@GAME@@QEBA?BMXZ");
        Bind(skillOwner, game, "?GetManager@Skill@GAME@@QEAAPEAVSkillManagerBase@2@XZ");
        Bind(endCooldown, game, "?EndCooldown@Skill@GAME@@UEAAXH@Z");
        Bind(refreshCooldown, game, "?RefreshCooldown@SkillManager@GAME@@UEAAXH@Z");
        Bind(forceSpeed, game, "?ForceSpeedUpdate@Character@GAME@@QEAAXXZ");
        if (!setMana || !manaLimit || !manaReserve || !currentMana || !skillOwner || !endCooldown || !refreshCooldown || !forceSpeed) return;
        struct Hook { const char* symbol; void* replacement; void** original; unsigned rva; uint32_t hash; };
        const Hook hooks[] = {
            { "?IsInvincible@Player@GAME@@UEBA_NXZ", reinterpret_cast<void*>(&InvincibleHook), reinterpret_cast<void**>(&originalInvincible), 0x3BA2B0, 0x7278700C },
            { "?GetRunSpeed@Character@GAME@@QEAA?BM_N@Z", reinterpret_cast<void*>(&SpeedHook), reinterpret_cast<void**>(&originalSpeed), 0x68EE0, 0xE269847B },
            { "?StartCooldown@Skill@GAME@@QEAAX_N@Z", reinterpret_cast<void*>(&StartHook), reinterpret_cast<void**>(&originalStart), 0x47CBF0, 0xF14F8020 },
            { "?SubtractMana@Character@GAME@@QEAAXM@Z", reinterpret_cast<void*>(&SubtractHook), reinterpret_cast<void**>(&originalSubtract), 0x68A40, 0xDF88BBF1 }
        };
        void* targets[4]{};
        for (unsigned i = 0; i < 4; ++i)
        {
            targets[i] = reinterpret_cast<void*>(GetProcAddress(game, hooks[i].symbol));
            if (!Fingerprint(game, targets[i], hooks[i].rva, hooks[i].hash)) return;
        }
        unsigned created = 0;
        for (; created < 4; ++created)
            if (MH_CreateHook(targets[created], hooks[created].replacement, hooks[created].original) != MH_OK) break;
        bool enabled = created == 4;
        if (enabled) for (auto target : targets) if (MH_EnableHook(target) != MH_OK) { enabled = false; break; }
        if (!enabled)
        {
            for (unsigned i = 0; i < created; ++i) { MH_DisableHook(targets[i]); MH_RemoveHook(targets[i]); }
            return;
        }
        available = true;
    }
}
