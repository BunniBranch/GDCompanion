#include <windows.h>
#include <MinHook.h>
#include <atomic>
#include <cmath>
#include <algorithm>
#include <cstring>
#include <cstdio>
#include <cstdlib>
// Injectable game bindings let us exercise actual hook and cleanup code without
// starting the game, loading its DLLs, or touching a character/save.
namespace MasteryAudit
{
    inline bool available = true;
    inline bool (*loading)(void*) = nullptr;
    inline unsigned (*objectId)(void*) = nullptr;
    inline void* (*skillManager)(void*) = nullptr;
    template<class T> void Bind(T& target, HMODULE module, const char* name)
    { target = reinterpret_cast<T>(GetProcAddress(module, name)); }
}
namespace MasteryRespec
{
    inline void** engineInstance = nullptr;
    inline void* (*gameInfo)(void*) = nullptr;
    inline bool (*singlePlayer)(void*) = nullptr, (*alive)(void*) = nullptr;
}
#include "GameplayAssists.h"
using namespace GameplayAssists;
struct Player { unsigned id; float energy = 10, limit = 100, reserve = 25, speed = 4, cachedSpeed = 4; bool invincible = false; };
struct Skill { void* manager; int cooldown = 10; };
Player first{42}, other{43};
Player* current = &first;
bool loading = false, alive = true, single = true;
int energyWrites = 0, spent = 0, starts = 0, clears = 0, refreshes = 0, speedUpdates = 0;
void Check(bool condition, const char* label)
{
    if (!condition) { std::fprintf(stderr, "FAIL %s\n", label); std::exit(1); }
    std::printf("PASS %s\n", label);
}
void Apply(unsigned mask, unsigned speed = 100)
{
    Tick(); auto state = lease.Read(GetTickCount64()); char response[256]{};
    Run("SET", state.revision, mask, speed, GetTickCount64() + 2000, response, sizeof(response));
    Check(strncmp(response, "OK ASSISTS", 10) == 0, "guarded runtime apply succeeds");
}
int main()
{
    void* game = &first; gameInstance = &game; MasteryRespec::engineInstance = &game;
    localPlayer = []()->void* { return current; };
    MasteryAudit::loading = [](void*) { return loading; };
    MasteryAudit::objectId = [](void* p) { return static_cast<Player*>(p)->id; };
    MasteryAudit::skillManager = [](void* p) { return p; };
    MasteryRespec::gameInfo = [](void* p) { return p; };
    MasteryRespec::singlePlayer = [](void*) { return single; };
    MasteryRespec::alive = [](void*) { return alive; };
    originalInvincible = [](void* p) { return static_cast<Player*>(p)->invincible; };
    originalSpeed = [](void* p, bool) { return static_cast<Player*>(p)->speed; };
    originalSubtract = [](void*, float) { ++spent; };
    currentMana = [](void* p) { return static_cast<Player*>(p)->energy; };
    manaLimit = [](void* p) { return static_cast<Player*>(p)->limit; };
    manaReserve = [](void* p) { return static_cast<Player*>(p)->reserve; };
    setMana = [](void* p, float value) { ++energyWrites; static_cast<Player*>(p)->energy = value; };
    forceSpeed = [](void* p) { ++speedUpdates; static_cast<Player*>(p)->cachedSpeed = SpeedHook(p, false); };
    skillOwner = [](void* p) { return static_cast<Skill*>(p)->manager; };
    originalStart = [](void* p, bool) { ++starts; static_cast<Skill*>(p)->cooldown = 10; };
    endCooldown = [](void* p, int amount) { Check(amount == 0, "cooldown uses native full-reset argument"); ++clears; static_cast<Skill*>(p)->cooldown = 0; };
    refreshCooldown = [](void* p, int) { Check(p == current, "cooldown refresh only targets current player manager"); ++refreshes; };
    available = true;
    Tick();
    Check(!energyWrites && !speedUpdates && !refreshes, "idle bridge has no assist mutations");
    Check(!InvincibleHook(&first) && SpeedHook(&first, false) == 4, "off hooks preserve normal game behavior");
    Apply(15, 150);
    Check(first.energy == 75 && energyWrites == 1, "energy refill respects reserved portion and avoids repeated writes");
    Check(InvincibleHook(&first) && !InvincibleHook(&other) && !first.invincible, "invincibility is player-only without changing game flag");
    Check(first.cachedSpeed == 6 && SpeedHook(&other, false) == 4, "speed multiplier affects only selected player");
    SubtractHook(&first, 10); SubtractHook(&other, 10); SubtractHook(&first, -5);
    Check(spent == 2, "energy bypass preserves other actors and non-consumption calls");
    Skill own{&first}, foreign{&other};
    StartHook(&own, false); StartHook(&foreign, false);
    Check(starts == 2 && clears == 1 && own.cooldown == 0 && foreign.cooldown == 10, "skill original still runs and only owned cooldown is reset");
    first.reserve = 100; first.energy = 0; Tick();
    Check(first.energy == 0, "fully reserved energy is not made available");
    Stop(); char response[256]{}; Run("OFF", 0, 0, 100, 0, response, sizeof(response));
    Check(first.cachedSpeed == 4 && !speedTouched.load() && !lease.Read(GetTickCount64()).mask, "all-off refreshes cached normal movement before acknowledging cleanup");
    Check(first.energy == 0 && own.cooldown == 0, "all-off does not rewind earlier gameplay outcomes");
    first.invincible = true;
    Check(InvincibleHook(&first), "all-off preserves game-owned invincibility"); first.invincible = false;
    const auto old = lease.Read(GetTickCount64());
    Run("SET", old.revision, 1, 100, GetTickCount64(), response, sizeof(response));
    Check(strncmp(response, "ERROR", 5) == 0 && !lease.Read(GetTickCount64()).mask, "expired queued set never enables later");
    Apply(1); current = &other;
    Check(!InvincibleHook(&other) && !lease.Read(GetTickCount64()).mask, "hook rejects changed player before next update");
    Tick(); Apply(1); single = false;
    Check(!InvincibleHook(&other), "multiplayer immediately rejects assist hook"); Tick(); single = true;
    Apply(1); loading = true;
    Check(!InvincibleHook(&other), "loading immediately rejects assist hook"); Tick(); loading = false;
    Apply(15, 150);
    const auto beforeDeath = lease.Read(GetTickCount64());
    alive = false; other.energy = 1;
    const auto deathWrites = energyWrites, deathRefreshes = refreshes, deathSpeeds = speedUpdates;
    const auto deathSpent = spent, deathClears = clears;
    Skill deadSkill{&other};
    Check(!InvincibleHook(&other) && SpeedHook(&other, false) == 4, "dead player hooks preserve normal game results without revoking assists");
    SubtractHook(&other, 10); StartHook(&deadSkill, false);
    Tick();
    Check(lease.Read(GetTickCount64()).mask == 15 && lease.Read(GetTickCount64()).revision == beforeDeath.revision &&
        lease.Read(GetTickCount64()).speed == 150, "death preserves every assist, speed selection and authorization revision");
    Check(energyWrites == deathWrites && refreshes == deathRefreshes && speedUpdates == deathSpeeds &&
        spent == deathSpent + 1 && clears == deathClears && deadSkill.cooldown == 10,
        "dead player receives no assist writes or cost/cooldown overrides");
    Run("PULSE", beforeDeath.revision, 0, 100, GetTickCount64() + 2000, response, sizeof(response));
    Check(strncmp(response, "OK ASSISTS", 10) == 0 && lease.Read(GetTickCount64()).mask == 15,
        "connection heartbeat keeps all assists enabled while dead");
    other.cachedSpeed = 4; alive = true; Tick();
    Check(lease.Read(GetTickCount64()).mask == 15 && other.energy == 75 && other.cachedSpeed == 6 &&
        InvincibleHook(&other) && refreshes > deathRefreshes, "same-character respawn resumes all effects and refreshes cached movement without another SET");
    alive = false; Stop(); Tick(); alive = true; Tick();
    Check(!lease.Read(GetTickCount64()).mask && other.cachedSpeed == 4 && !speedTouched.load() && !InvincibleHook(&other),
        "explicit stop while dead stays off after respawn and finishes normal-speed cleanup");
    Apply(2); alive = false; Tick();
    auto deadLease = lease.Read(GetTickCount64());
    Check(!lease.Read(deadLease.expires).mask, "dead-player authorization still expires without heartbeat");
    alive = true; Tick();
    Check(!lease.Read(GetTickCount64()).mask, "respawn cannot revive expired assists");
    Apply(2); alive = false; current = &first; Tick();
    Check(!lease.Read(GetTickCount64()).mask, "character change during death still clears authorization");
    alive = true; Tick(); Apply(2); alive = false; single = false; Tick();
    Check(!lease.Read(GetTickCount64()).mask, "leaving single-player during death still clears authorization");
    single = true; alive = true; Tick(); Apply(2); alive = false; loading = true; Tick();
    Check(!lease.Read(GetTickCount64()).mask, "loading still invalidates player context even while dead");
    loading = false; alive = true; Tick();
    std::puts("ALL GAMEPLAY ASSIST RUNTIME TESTS PASSED");
}
