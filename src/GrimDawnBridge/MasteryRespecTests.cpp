#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include "MasteryAudit.h"
#include "MasteryRespec.h"

namespace
{
    struct Skill { unsigned int id, points, owner; bool mastery; };
    Skill entries[5]{{1,1,0,true},{2,1,0,true},{3,1,1,false},{4,1,2,false},{5,1,0,false}};
    void* pointers[5]{&entries[0], &entries[1], &entries[2], &entries[3], &entries[4]};
    MasteryRespec::VectorView list{pointers,pointers+5,pointers+5}, empty{};
    unsigned int playerId = 42, points = 51, changed = 0;
    unsigned int devotionPoints = 2, devotionSpent = 3, devotionEarned = 5, selected = 2;
    unsigned int devotionChanges = 0, classChanges = 0, affinityValue = 1, celestialXp = 1234, binding = 3;
    unsigned int cycleStartChanges = 0, cycleStartClasses = 0;
    bool failDevotion = false, failClass = false, failExperience = false, failBinding = false, uiMismatch = false;
    bool fail = false;
    void Require(bool ok, const char* message) { if (!ok) { printf("FAIL %s\n",message); ExitProcess(1); } printf("PASS %s\n",message); }
    unsigned int __fastcall Id(void* p) { return *static_cast<unsigned int*>(p); }
    unsigned int __fastcall Rank(void* p) { return static_cast<Skill*>(p)->points; }
    unsigned int __fastcall Owner(void* p) { return static_cast<Skill*>(p)->owner; }
    bool __fastcall IsMastery(void* p) { return static_cast<Skill*>(p)->mastery; }
    unsigned int __fastcall Regular(void*) { return entries[2].points + entries[3].points; }
    unsigned int __fastcall Mastery(void*) { return entries[0].points + entries[1].points; }
    unsigned int __fastcall Active(void*) { return (entries[0].points != 0) + (entries[1].points != 0); }
    unsigned int __fastcall Points(void*) { return points; }
    bool __fastcall Decrement(void* p, unsigned int n) { ++changed; if (fail) return false; static_cast<Skill*>(p)->points -= n; return true; }
    void __fastcall Refund(void*, unsigned int n) { points += n; }
    void Reset()
    {
        for (auto& entry : entries) entry.points = 1;
        points = 51; changed = 0; fail = false;
        devotionPoints = 2; devotionSpent = 3; devotionEarned = 5; selected = 2;
        devotionChanges = classChanges = 0; affinityValue = 1; celestialXp = 1234; binding = 3;
        failDevotion = failClass = failExperience = failBinding = uiMismatch = false;
        MasteryRespec::attempted = false; MasteryRespec::token = 0;
        MasteryRespec::mutationBlocked = false; MasteryRespec::recordedToken = MasteryRespec::issuedToken = 0;
        cycleStartChanges = cycleStartClasses = 0;
        MasteryAudit::available = MasteryRespec::available = true;
    }
    void Reinvest()
    {
        // Model the user choosing and investing in a new build, without a bridge reset.
        for (auto& entry : entries) entry.points = 1;
        points -= 4; devotionPoints -= 3; devotionSpent = 3;
        selected = 2; affinityValue = 1; binding = 3;
        cycleStartChanges = changed; cycleStartClasses = classChanges;
    }
    void Run(const char* operation, char* output, unsigned long long deadline = GetTickCount64() + 5000)
    { MasteryRespec::Run(&playerId,&playerId,operation,MasteryRespec::token,deadline,output,1024); }
}
int main()
{
    using namespace MasteryAudit;
    skillManager = [](void* p)->void* { return p; };
    playerName = [](void*)->const wchar_t* { return L"Disposable"; };
    objectId = Id; level = [](void*)->unsigned int { return 18; };
    unspent = Points; regular = Regular; mastery = Mastery; active = Active;
    allowed = [](void*)->unsigned int { return 2; };
    skillSet = [](void*)->int { return 0; };
    loading = [](void*)->bool { return false; };
    MasteryRespec::skills = [](void*)->const MasteryRespec::VectorView* { return &list; };
    MasteryRespec::itemSkills = [](void*)->const MasteryRespec::VectorView* { return &empty; };
    MasteryRespec::rank = Rank; MasteryRespec::owner = Owner; MasteryRespec::isMastery = IsMastery;
    MasteryRespec::isDefault = [](void*, unsigned int)->bool { return false; };
    MasteryRespec::entrySet = [](void*)->int { return 0; };
    MasteryRespec::devotionSpent = [](void*)->unsigned int { return devotionSpent; };
    MasteryRespec::devotionPoints = [](void*)->unsigned int { return devotionPoints; };
    MasteryRespec::devotionTotal = [](void*)->unsigned int { return devotionEarned; };
    MasteryRespec::devotionMax = [](void*)->unsigned int { return 55; };
    MasteryRespec::affinity = [](void*, unsigned int)->unsigned int { return affinityValue; };
    MasteryRespec::readUi = [](void*, unsigned int, const MasteryRespec::VectorView*, unsigned int, MasteryGameUi::State& state)->const char*
    {
        state = {}; state.selected = selected; state.count = 1;
        state.stars[0] = {nullptr, 99, devotionSpent, celestialXp, 2, binding};
        return uiMismatch ? "DEVOTION_UI_TOTAL_DOES_NOT_MATCH_GAME" : nullptr;
    };
    MasteryRespec::resetDevotions = [](const MasteryGameUi::State&)
    {
        ++devotionChanges;
        Require(changed == cycleStartChanges && classChanges == cycleStartClasses, "devotion stage precedes skill/class removal");
        devotionSpent = 0; affinityValue = 0;
        if (!failDevotion) devotionPoints = devotionEarned;
        if (failExperience) celestialXp = 0;
        if (!failBinding) binding = 0;
    };
    MasteryRespec::devotionsCleared = [](const MasteryGameUi::State& state)->bool
    { return !devotionSpent && !binding && celestialXp == state.stars[0].experience; };
    MasteryRespec::clearClasses = [](const MasteryGameUi::State&)->bool
    {
        ++classChanges;
        Require(!Regular(nullptr) && !Mastery(nullptr) && points == 55 && devotionPoints == devotionEarned,
            "class slots are cleared only after both point pools are verified");
        if (failClass) return false;
        selected = 0; return true;
    };
    MasteryRespec::attacked = loading;
    MasteryRespec::alive = [](void*)->bool { return true; };
    void* instance = &playerId;
    MasteryRespec::engineInstance = &instance;
    MasteryRespec::gameInfo = skillManager;
    MasteryRespec::singlePlayer = MasteryRespec::alive;
    MasteryRespec::decrement = Decrement; MasteryRespec::refund = Refund;
    char output[1024]{};
    Reset(); Run("PREPARE",output); Require(strstr(output,"OK FULL_RESPEC_PREPARED") == output && changed == 0 && !devotionChanges && !classChanges,"native preflight performs no writes");
    Run("COMMIT",output); Require(strstr(output,"OK FULL_RESPEC_RESET") == output && points == 55 && changed == 4 && devotionPoints == 5 && selected == 0,"native full reset conserves both pools and clears classes");
    Require(entries[4].points == 1,"innate non-mastery actions are neither cleared nor refunded");
    Run("COMMIT",output); Require(points == 55 && changed == 4 && devotionChanges == 1 && classChanges == 1,"repeated transaction never duplicates a refund");
    Run("STATUS",output); Require(strstr(output,"OK FULL_RESPEC_RESET") == output && changed == 4,"status query returns recorded outcome without writes");
    const auto firstToken = MasteryRespec::token;
    Run("PREPARE",output); Require(strstr(output,"NOTHING_TO_RESET") != nullptr && changed == 4 && points == 55,
        "repeated respec without a new build is a no-op rather than a session lock");
    MasteryRespec::Run(&playerId,&playerId,"STATUS",firstToken,0,output,sizeof(output));
    Require(strstr(output,"OK FULL_RESPEC_RESET"), "nothing-to-reset preflight retains the last successful outcome");
    Reinvest(); uiMismatch = true; Run("PREPARE",output); uiMismatch = false;
    Require(strstr(output,"DEVOTION_UI_TOTAL") && changed == 4, "later preflight failure cannot mutate a new build");
    MasteryRespec::Run(&playerId,&playerId,"STATUS",firstToken,0,output,sizeof(output));
    Require(strstr(output,"OK FULL_RESPEC_RESET"), "failed later preflight retains the last successful outcome");
    Run("PREPARE",output);
    const auto secondToken = MasteryRespec::token;
    Require(strstr(output,"OK FULL_RESPEC_PREPARED") && secondToken > firstToken && !MasteryRespec::attempted,
        "a fresh verified plan is allowed after reinvesting in the same session with a unique token");
    MasteryRespec::Run(&playerId,&playerId,"COMMIT",firstToken,GetTickCount64()+5000,output,sizeof(output));
    Require(strstr(output,"OK FULL_RESPEC_RESET") && points == 51 && changed == 4 && MasteryRespec::token == secondToken,
        "replay of first commit returns its outcome without touching the new build or plan");
    Run("COMMIT",output);
    Require(strstr(output,"OK FULL_RESPEC_RESET") && points == 55 && devotionPoints == 5 && changed == 8 &&
        devotionChanges == 2 && classChanges == 2 && !selected && !MasteryRespec::mutationBlocked,
        "second respec in the same session independently conserves both budgets and verifies class removal");
    Run("COMMIT",output);
    Require(points == 55 && changed == 8 && devotionChanges == 2, "second commit is also idempotent");
    MasteryRespec::Run(&playerId,&playerId,"COMMIT",firstToken,GetTickCount64()+5000,output,sizeof(output));
    Require(strstr(output,"UNKNOWN_TRANSACTION") && changed == 8, "older superseded commit tokens cannot mutate");
    Reinvest(); Run("PREPARE",output); fail = true; Run("COMMIT",output);
    Require(strstr(output,"PARTIAL_OR_UNKNOWN") && MasteryRespec::mutationBlocked, "a later partial respec still locks mutation after earlier successes");
    const auto partialToken = MasteryRespec::token;
    const auto partialCalls = changed;
    Run("PREPARE",output);
    Require(strstr(output,"SESSION_LOCKED_AFTER_UNVERIFIED_RESET") && changed == partialCalls && MasteryRespec::token == partialToken,
        "preparing again cannot bypass a partial-outcome lock or discard its token");
    Run("STATUS",output);
    Require(strstr(output,"PARTIAL_OR_UNKNOWN"), "partial outcome remains available for checking");
    Reset(); Run("PREPARE",output); Run("COMMIT",output,GetTickCount64()-1); Require(strstr(output,"EXPIRED_NO_CHANGES") && changed == 0,"expired queue work cannot mutate later");
    Reset(); Run("PREPARE",output); MasteryRespec::issuedAt -= 400000; Run("COMMIT",output); Require(strstr(output,"EXPIRED_NO_CHANGES") && changed == 0,"stale confirmation plan cannot mutate");
    Reset(); Run("PREPARE",output); ++points; Run("COMMIT",output); Require(strstr(output,"CHARACTER_CHANGED_NO_CHANGES") && changed == 0,"changed point budget blocks reset");
    Reset(); Run("PREPARE",output); fail = true; Run("COMMIT",output); Require(strstr(output,"PARTIAL_OR_UNKNOWN") && points == 51,"failed decrement never grants a guessed refund");
    const auto calls = changed; Run("COMMIT",output); Require(changed == calls,"partial outcome is permanently locked against retries");
    Run("PREPARE",output); Require(strstr(output,"SESSION_LOCKED_AFTER_UNVERIFIED_RESET") && changed == calls,
        "a failed first attempt also blocks all later preparation");
    Reset(); Run("PREPARE",output); const auto validToken = MasteryRespec::token; MasteryRespec::Run(&playerId,&playerId,"COMMIT",validToken+1,GetTickCount64()+5000,output,sizeof(output));
    Require(strstr(output,"UNKNOWN_TRANSACTION") && changed == 0,"unknown transaction cannot mutate");
    Reset(); Run("PREPARE",output); Run("BOGUS",output);
    Require(strstr(output,"UNKNOWN_OPERATION") && !changed && !devotionChanges,"unknown operation cannot mutate");
    Reset(); ++devotionEarned; Run("PREPARE",output);
    Require(strstr(output,"DEVOTION_BUDGET_MISMATCH") && !devotionChanges,"inconsistent earned devotion budget blocks preflight");
    Reset(); uiMismatch = true; Run("PREPARE",output);
    Require(strstr(output,"DEVOTION_UI_TOTAL") && !devotionChanges,"incomplete devotion UI coverage blocks all mutations");
    Reset(); Run("PREPARE",output); ++celestialXp; Run("COMMIT",output);
    Require(strstr(output,"CHARACTER_CHANGED_NO_CHANGES") && !devotionChanges,"changed celestial power experience invalidates confirmation");
    Reset(); Run("PREPARE",output); ++binding; Run("COMMIT",output);
    Require(strstr(output,"CHARACTER_CHANGED_NO_CHANGES") && !devotionChanges,"changed devotion binding invalidates confirmation");
    Reset(); Run("PREPARE",output); failDevotion = true; Run("COMMIT",output);
    Require(strstr(output,"PARTIAL_OR_UNKNOWN") && !changed && !classChanges && devotionPoints == 2,"failed devotion refund stops before skill writes");
    Run("COMMIT",output); Require(devotionChanges == 1,"partial devotion reset cannot run again");
    Reset(); Run("PREPARE",output); failExperience = true; Run("COMMIT",output);
    Require(strstr(output,"PARTIAL_OR_UNKNOWN") && !changed,"celestial experience loss stops the transaction");
    Reset(); Run("PREPARE",output); failBinding = true; Run("COMMIT",output);
    Require(strstr(output,"PARTIAL_OR_UNKNOWN") && !changed,"leftover devotion bindings stop the transaction");
    Reset(); Run("PREPARE",output); failClass = true; Run("COMMIT",output);
    Require(strstr(output,"PARTIAL_OR_UNKNOWN") && selected == 2,"uncleared class slots are never reported as success");
    Reset(); devotionSpent = 0; devotionPoints = devotionEarned; affinityValue = binding = 0;
    Run("PREPARE",output); Run("COMMIT",output);
    Require(strstr(output,"OK FULL_RESPEC_RESET") && !devotionChanges && classChanges == 1,"already unassigned devotions are preserved without a second refund");
    return 0;
}
