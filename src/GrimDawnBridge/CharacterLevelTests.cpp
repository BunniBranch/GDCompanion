#include <windows.h>
#include <array>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include "CharacterLevelState.h"

static void Check(bool value, const char* name)
{ printf("%s %s\n", value ? "PASS" : "FAIL", name); if (!value) std::exit(1); }

static unsigned level = 1, experience = 0, skills = 0, attributes = 0, devotion = 3, calls = 0;
static float bonus = 0;
static bool loading = false, alive = true, single = true, combat = false, brokenIncrement = false;
namespace MasteryAudit
{
    template<class T> void Bind(T&, HMODULE, const char*) {}
    inline auto loading = +[](void*) { return ::loading; };
    inline auto objectId = +[](void*) { return 123u; };
    inline auto level = +[](void*) { return ::level; };
    inline auto unspent = +[](void*) { return ::skills; };
    inline auto playerName = +[](void*) { return L"Test"; };
}
namespace MasteryRespec
{
    inline bool available = true;
    inline void* engine = reinterpret_cast<void*>(1);
    inline void** engineInstance = &engine;
    inline auto gameInfo = +[](void* p) { return p; };
    inline auto singlePlayer = +[](void*) { return single; };
    inline auto alive = +[](void*) { return ::alive; };
    inline auto attacked = +[](void*) { return combat; };
    inline auto devotionPoints = +[](void*) { return devotion; };
}
#include "CharacterLevel.h"
int main()
{
    using namespace CharacterLevel;
    Snapshot sample{1, 7, 10, 100, 100, 10, 10, 3, 0};
    for (unsigned target : {0u, 9u, 10u, 101u, 4294967295u}) Check(Validate(sample, target) != nullptr, "level validation rejects lowering, no-op, and unsupported levels");
    Check(!Validate(sample, 100), "expansion cap accepts exactly 100");
    sample.cap = 200;
    Check(!Validate(sample, 200), "loaded mod level cap can exceed vanilla");
    sample.cap = 1001;
    Check(Validate(sample, 100) != nullptr, "unsupported extreme caps fail closed");
    sample.cap = 85;
    Check(!Validate(sample, 85) && Validate(sample, 86), "base-game cap is checked before changing XP");
    sample.cap = 100; sample.xpBonus = 10;
    Check(Validate(sample, 20) != nullptr, "XP bonuses cannot cause target overshoot");
    sample.xpBonus = 0;
    Transaction tx;
    Check(!tx.Prepare(sample, 20, 100), "level preparation is read-only");
    auto token = tx.token;
    auto changed = sample; changed.id++;
    Check(tx.Consume(changed, token, 101) != nullptr && tx.Consume(sample, token, 102) != nullptr, "changed character consumes and rejects the confirmation");
    tx.Prepare(sample, 20, 100); token = tx.token; changed = sample; changed.xp++;
    Check(tx.Consume(changed, token, 101) != nullptr, "intervening XP changes require a new inspection");
    tx.Prepare(sample, 20, 100); token = tx.token;
    Check(tx.Consume(sample, token, 60100) != nullptr, "expired level confirmation cannot mutate later");

    alignas(16) std::array<unsigned char, 0x1800> player{};
    *reinterpret_cast<unsigned*>(player.data() + 0x175C) = 100;
    available = true;
    budgetCheck = +[](void*, void*, unsigned) -> const char* { return nullptr; };
    xp = +[](void*) { return experience; };
    nextXp = +[](void*) { return level * 100u; };
    attributePoints = +[](void*) { return attributes; };
    totalAttribute = +[](void*, int id) { Check(id == 0x37, "XP bonus uses audited native attribute id"); return bonus; };
    increment = +[](void*) { calls++; experience = level * 100; if (!brokenIncrement) { level++; skills += 3; attributes++; } };
    char response[1024]{};
    const auto game = reinterpret_cast<void*>(1);
    auto run = [&](const char* op, unsigned target, unsigned long long confirmation) {
        Run(game, player.data(), op, target, confirmation, GetTickCount64() + 5000, response, sizeof(response));
    };
    run("PREPARE", 5, 0); token = transaction.token;
    Check(strncmp(response, "OK LEVEL_PREPARED", 17) == 0 && calls == 0, "runtime preparation never changes level or points");
    run("COMMIT", 0, token);
    Check(strcmp(response, "OK LEVEL_SET 5 12 4") == 0 && calls == 4 && devotion == 3, "runtime advances one native level at a time and verifies results");
    run("COMMIT", 0, token);
    Check(calls == 4, "replayed confirmation cannot grant extra levels or points");
    for (int scenario = 0; scenario < 4; ++scenario)
    {
        loading = scenario == 0; alive = scenario != 1; single = scenario != 2; combat = scenario == 3;
        run("PREPARE", 6, 0);
        Check(strncmp(response, "ERROR", 5) == 0 && calls == 4, "loading, death, multiplayer and combat reject level changes");
    }
    loading = combat = false; alive = single = true;
    run("PREPARE", 201, 0); Check(calls == 4 && strncmp(response, "ERROR", 5) == 0, "native runtime rejects level 201 without mutation");
    run("PREPARE", 6, 0); token = transaction.token; brokenIncrement = true;
    run("COMMIT", 0, token);
    Check(transaction.blocked && calls == 5 && strncmp(response, "ERROR", 5) == 0, "unverified mutation stops and blocks subsequent attempts");
    run("PREPARE", 6, 0); Check(calls == 5 && strncmp(response, "ERROR", 5) == 0, "partial failures cannot silently retry");
}
