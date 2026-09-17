#include <windows.h>
#include <array>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include "CharacterResourceState.h"

static void Check(bool value,const char* name)
{ printf("%s %s\n",value ? "PASS" : "FAIL",name);if(!value) std::exit(1); }
static unsigned level=10, expansion=3, skillSet=0, identity=123, calls=0;
static std::array<unsigned,4> balances{100,10,10,5}, spent{0,20,9,3};
static unsigned earned=8;
static unsigned liveDevotionCap=55, liveAttributeAward=1, recordLevelCap=100;
static unsigned liveSkillCount=99;
static std::array<unsigned,1000> liveSkills{};
static void* fakeTableVtable=reinterpret_cast<void*>(8);
static bool loading=false, alive=true, single=true, combat=false, broken=false;
static std::string mod;
namespace MasteryAudit
{
    template<class T> void Bind(T&,HMODULE,const char*) {}
    inline auto loading=+[](void*) {return ::loading;};
    inline auto objectId=+[](void*) {return identity;};
    inline auto level=+[](void*) {return ::level;};
    inline auto unspent=+[](void*) {return balances[1];};
    inline auto playerName=+[](void*) {return L"Test";};
    inline auto skillManager=+[](void* p) {return p;};
    inline auto skillSet=+[](void*) {return ::skillSet;};
    inline auto regular=+[](void*) {return spent[1]-10;};
    inline auto mastery=+[](void*) {return 10u;};
}
namespace MasteryRespec
{
    inline bool available=true;
    inline void* engine=reinterpret_cast<void*>(1);
    inline void** engineInstance=&engine;
    inline auto gameInfo=+[](void* p) {return p;};
    inline auto singlePlayer=+[](void*) {return single;};
    inline auto alive=+[](void*) {return ::alive;};
    inline auto attacked=+[](void*) {return combat;};
    inline auto devotionPoints=+[](void*) {return balances[3];};
    inline auto devotionSpent=+[](void*) {return spent[3];};
    inline auto devotionTotal=+[](void*) {return earned;};
    inline auto devotionMax=+[](void*) {return liveDevotionCap;};
}
#include "CharacterResource.h"
int main()
{
    using namespace CharacterResource;
    Check(Endgame(0).level==85 && Endgame(0).skill==223 && Endgame(0).attribute==90,"base-game endgame budget");
    Check(Endgame(1).skill==244 && Endgame(1).attribute==105,"Ashes of Malmouth endgame budget");
    Check(Endgame(2).skill==248 && Endgame(2).attribute==107,"Forgotten Gods endgame budget");
    Check(Endgame(3).skill==250 && Endgame(3).attribute==109 && !Endgame(4).level,"Fangs of Asterkarn and unsupported profile");
    Snapshot sample{};sample.player=1;sample.id=7;sample.level=10;sample.levelCap=100;
    sample.cap={MoneyCap,250,109,55};sample.spent={0,20,9,3};sample.value={100,10,10,5};sample.devotionTotal=8;strcpy_s(sample.name,"0054");
    sample.progression.attributePerLevel=1;sample.progression.skillCount=99;
    for(unsigned i=0;i<99;i++) sample.progression.skills[i]=liveSkills[i]=i<49 ? 3 : i<89 ? 2 : 1;
    sample.expansion=3;
    auto profile=sample;
    Check(!ApplyProgression(profile) && !profile.modded && profile.cap==sample.cap,"standard loaded rules retain DLC budgets regardless of inactive mod files");
    profile.progression.attributePerLevel=2;profile.progression.skillCount=1;profile.progression.skills.fill(0);profile.progression.skills[0]=3;profile.cap[3]=100;
    Check(!ApplyProgression(profile) && profile.modded && profile.cap[1]==310 && profile.cap[2]==208 && profile.cap[3]==100,"active mod takes priority: repeating skill award, double attributes and native devotion cap");
    profile.levelCap=200;
    Check(!ApplyProgression(profile) && profile.cap[1]==610 && profile.cap[2]==408,"mod level caps above vanilla are honored");
    profile.progression.initialSkills=5;
    Check(!ApplyProgression(profile) && profile.cap[1]==615,"mod initial skill points count toward full endgame budget");
    profile.levelCap=1001;Check(ApplyProgression(profile)!=nullptr,"unsupported extreme mod cap fails closed instead of silently replacing it");
    for(unsigned kind=0;kind<4;kind++)
    {
        Check(!Validate(sample,kind,0) && !Validate(sample,kind,Available(sample,kind)),"zero and allocated-adjusted maximum accepted");
        Check(Validate(sample,kind,Available(sample,kind)+1) && Validate(sample,kind,UINT_MAX),"above-budget and overflow inputs rejected");
    }
    auto changed=sample;changed.spent[1]=251;
    Check(Validate(changed,1,0)!=nullptr,"overallocated builds cannot hide an invalid total by setting zero");
    Transaction tx;tx.Prepare(sample,1,0,100);auto token=tx.token;
    for(unsigned kind=1;kind<4;kind++)
    {
        Check(!Validate(sample,kind,BypassPointBudget-sample.spent[kind],true),"bypass counts spent points in hard ceiling");
        Check(Validate(sample,kind,BypassPointBudget-sample.spent[kind]+1,true) && Validate(sample,kind,UINT_MAX,true),"bypass cannot exceed hard ceiling or overflow");
    }
    Check(EffectiveCap(sample,0,true)==MoneyCap && Validate(sample,0,MoneyCap+1,true),"bypass never changes money cap");
    Check(!tx.Prepare(sample,1,1000,100,true) && !tx.Consume(sample,tx.token,101),"explicit bypass authorization survives one-use commit");
    Check(tx.Prepare(sample,1,1000,102) && !tx.token,"a later normal preparation does not inherit bypass");
    tx.Prepare(sample,1,0,100);token=tx.token;
    Check(!tx.Consume(sample,token,101) && tx.Consume(sample,token,102),"confirmation is single-use");
    for(int scenario=0;scenario<6;scenario++)
    {
        changed=sample;
        if(scenario==0) changed.id++;
        if(scenario==1) changed.value[0]++;
        if(scenario==2) changed.spent[1]++;
        if(scenario==3) changed.cap[1]--;
        if(scenario==4) changed.level++;
        if(scenario==5) changed.progression.skills[0]++;
        tx.Prepare(sample,1,0,100);token=tx.token;
        Check(tx.Consume(changed,token,101) && tx.Consume(sample,token,102),"changed character state invalidates confirmation");
    }
    tx.Prepare(sample,1,0,100);Check(tx.Consume(sample,tx.token,60100)!=nullptr,"expired confirmation rejected");
    Check(CanAwardLevels(sample,100),"legitimate natural level awards fit endgame budget");
    changed=sample;changed.value[1]=230;
    Check(!CanAwardLevels(changed,11),"level awards cannot overfill an endgame skill budget");
    changed=sample;changed.value[2]=100;
    Check(!CanAwardLevels(changed,11),"level awards cannot overfill an endgame attribute budget");
    changed=sample;changed.devotionTotal--;
    Check(Validate(changed,3,0) && !Validate(changed,1,0),"inconsistent devotion accounting blocks devotion writes, not unrelated balances");

    alignas(16) std::array<unsigned char,0x1800> player{};
    auto& levelCap=*reinterpret_cast<unsigned*>(player.data()+0x175C);levelCap=100;
    auto syncRules=[&] {
        *reinterpret_cast<unsigned*>(player.data()+0x1714)=liveAttributeAward;
        *reinterpret_cast<uintptr_t*>(player.data()+0x1718)=reinterpret_cast<uintptr_t>(liveSkills.data());
        *reinterpret_cast<uintptr_t*>(player.data()+0x1720)=reinterpret_cast<uintptr_t>(liveSkills.data()+liveSkillCount);
    };
    syncRules();
    available=true;
    objectManager=+[]()->void* {return reinterpret_cast<void*>(1);};
    getTable=+[](void*,const std::string&)->const void* {return &fakeTableVtable;};
    binaryTableVtable=fakeTableVtable;
    customDatabase=+[](void*) {return false;};
    tableInt=+[](const void*,const char* field,int fallback) {
        if(!strcmp(field,"initialSkillPoints")) return 0;
        if(!strcmp(field,"maxPlayerLevel")) return static_cast<int>(recordLevelCap);
        if(!strcmp(field,"maxDevotionPoints")) return static_cast<int>(liveDevotionCap);
        if(!strcmp(field,"characterModifierPoints")) return static_cast<int>(liveAttributeAward);
        return fallback;
    };
    tableArrayInt=+[](const void*,const char*,unsigned i,int)->int {return static_cast<int>(liveSkills[i]);};
    money=+[](void*) {return balances[0];};attribute=+[](void*) {return balances[2];};
    attributeSpent=+[](void*,int type) {Check(type>=1 && type<=3,"audited attribute identifiers");return type==1 ? spent[2] : 0u;};
    expansionLoaded={+[](void*) {return expansion>=1;},+[](void*) {return expansion>=2;},+[](void*) {return expansion>=3;}};
    modName=+[](void*) -> const std::string& {return mod;};
    addMoney=+[](void*,unsigned n) {calls++;if(!broken) balances[0]+=n;};
    subtractMoney=+[](void*,unsigned n) {calls++;balances[0]-=n;return balances[0];};
    changeSkill=+[](void*,int n) {calls++;balances[1]=static_cast<unsigned>(static_cast<int>(balances[1])+n);};
    changeAttribute=+[](void*,int n) {calls++;balances[2]=static_cast<unsigned>(static_cast<int>(balances[2])+n);};
    addDevotion=+[](void*,unsigned n) {calls++;balances[3]+=n;};
    removeDevotion=+[](void*,unsigned n) {calls++;balances[3]-=n;};
    addEarned=+[](void*,unsigned n) {calls++;earned+=n;};
    removeEarned=+[](void*,unsigned n) {calls++;earned-=n;};
    char response[1024]{};
    auto run=[&](const char* op,unsigned kind=0,unsigned target=0,unsigned long long confirmation=0) {
        Run(reinterpret_cast<void*>(1),player.data(),op,kind,target,confirmation,GetTickCount64()+5000,response,sizeof(response));
    };
    run("LIMITS");Check(strncmp(response,"OK RESOURCE_LIMITS 10 100 3",27)==0 && calls==0,"read-only live limits");
    liveAttributeAward=2;liveSkillCount=1;liveDevotionCap=100;syncRules();
    run("LIMITS");Check(strstr(response,"2000000000 310 208 100")!=nullptr && calls==0,"runtime reads loaded mod schedule without granting points");
    liveAttributeAward=1;liveSkillCount=99;liveDevotionCap=55;syncRules();
    run("LIMITS");Check(strstr(response,"2000000000 250 109 55")!=nullptr && calls==0,"return to standard rules restores DLC limits without stale mod state");
    for(unsigned kind=0;kind<4;kind++)
    {
        const unsigned max=sample.cap[kind]-spent[kind];
        for(unsigned target : {max,0u,5u,5u})
        {
            auto before=balances;auto beforeCalls=calls;
            run("PREPARE",kind,target);token=transaction.token;
            Check(strncmp(response,"OK RESOURCE_PREPARED",20)==0 && balances==before && calls==beforeCalls,"runtime preparation is read-only");
            run("COMMIT",0,0,token);before[kind]=target;
            Check(strncmp(response,"OK RESOURCES",12)==0 && balances==before && spent==sample.spent,"set raises, lowers, zeros and no-ops without changing allocations or other pools");
            Check(earned==spent[3]+balances[3],"devotion earned and unspent totals remain consistent");
            beforeCalls=calls;run("COMMIT",0,0,token);Check(calls==beforeCalls,"runtime rejects repeated commits");
        }
        auto beforeCalls=calls;run("PREPARE",kind,max+1);Check(strncmp(response,"ERROR",5)==0 && calls==beforeCalls,"runtime cap cannot be bypassed");
    }
    for(int scenario=0;scenario<7;scenario++)
    {
        loading=scenario==0;alive=scenario!=1;single=scenario!=2;combat=scenario==3;
        mod=scenario==4 ? "custom" : "";skillSet=scenario==5 ? 1 : 0;levelCap=scenario==6 ? 200 : 100;
        auto beforeCalls=calls;run("PREPARE",0,100);
        Check(strncmp(response,"ERROR",5)==0 && calls==beforeCalls,"unready, multiplayer, combat, mods and unknown caps reject writes");
    }
    loading=combat=false;alive=single=true;mod.clear();skillSet=0;levelCap=100;
    for(unsigned kind=1;kind<4;kind++)
    {
        run("PREPARE_BYPASS",kind,1000);token=transaction.token;
        Check(strncmp(response,"OK RESOURCE_PREPARED",20)==0,"runtime accepts explicit above-natural-cap request");
        run("COMMIT",0,0,token);
        Check(strncmp(response,"OK RESOURCES",12)==0 && balances[kind]==1000 && earned==spent[3]+balances[3],"above-cap runtime mutation preserves accounting");
        run("PREPARE",kind,5);token=transaction.token;run("COMMIT",0,0,token);
        Check(strncmp(response,"OK RESOURCES",12)==0 && balances[kind]==5,"normal mode can lower an over-cap unspent balance");
    }
    run("PREPARE",0,100);token=transaction.token;balances[1]++;
    auto beforeCalls=calls;run("COMMIT",0,0,token);Check(strncmp(response,"ERROR",5)==0 && calls==beforeCalls,"intervening balance change rejects runtime commit");
    Run(reinterpret_cast<void*>(1),player.data(),"PREPARE",0,100,0,0,response,sizeof(response));
    Check(strncmp(response,"ERROR RESOURCE_REQUEST_EXPIRED",30)==0,"expired queued request cannot act");
    run("PREPARE",0,100);token=transaction.token;broken=true;run("COMMIT",0,0,token);
    Check(transaction.blocked && strncmp(response,"ERROR RESOURCE_PARTIAL",22)==0,"failed result verification blocks further mutation");
    beforeCalls=calls;run("PREPARE",0,100);Check(strncmp(response,"ERROR",5)==0 && calls==beforeCalls,"unverified writes cannot automatically retry");
}
