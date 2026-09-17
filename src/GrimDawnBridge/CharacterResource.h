#pragma once
#include "CharacterResourceState.h"

namespace CharacterResource
{
    using ReadUnsigned = unsigned(__fastcall*)(void*);
    using ChangeUnsigned = void(__fastcall*)(void*,unsigned);
    using ChangeSigned = void(__fastcall*)(void*,int);
    using SubtractMoney = unsigned(__fastcall*)(void*,unsigned);
    using ReadSpent = unsigned(__fastcall*)(void*,int);
    using ReadBool = bool(__fastcall*)(void*);
    using ModName = const std::string&(__fastcall*)(void*);
    using Singleton = void*(__cdecl*)();
    using GetTable = const void*(__fastcall*)(void*,const std::string&);
    using TableInt = int(__fastcall*)(const void*,const char*,int);
    using TableArrayInt = int(__fastcall*)(const void*,const char*,unsigned,int);
    inline Singleton objectManager=nullptr;
    inline GetTable getTable=nullptr;
    inline TableInt tableInt=nullptr;
    inline TableArrayInt tableArrayInt=nullptr;
    inline void* binaryTableVtable=nullptr;
    inline ReadBool customDatabase=nullptr;
    inline const std::string progressionRecord="records/creatures/pc/playerlevels.dbr";
    inline ReadUnsigned money=nullptr;
    inline ReadUnsigned attribute=nullptr;
    inline ReadSpent attributeSpent=nullptr;
    inline ChangeUnsigned addMoney=nullptr, addDevotion=nullptr, removeDevotion=nullptr, addEarned=nullptr, removeEarned=nullptr;
    inline SubtractMoney subtractMoney=nullptr;
    inline ChangeSigned changeSkill=nullptr, changeAttribute=nullptr;
    inline std::array<ReadBool,3> expansionLoaded{};
    inline ModName modName=nullptr;
    inline bool available=false;
    inline Transaction transaction;

    inline void Initialize(HMODULE engine, HMODULE game)
    {
        using MasteryAudit::Bind;
        Bind(objectManager,engine,"?Get@?$Singleton@VObjectManager@GAME@@@GAME@@SAPEAVObjectManager@2@XZ");
        Bind(getTable,engine,"?GetLoadTable@ObjectManager@GAME@@QEBAAEBVLoadTable@2@AEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@@Z");
        Bind(tableInt,engine,"?GetInt@LoadTableBinary@GAME@@UEBAHPEBDH@Z");
        Bind(tableArrayInt,engine,"?GetArrayInt@LoadTableBinary@GAME@@UEBAHPEBDIH@Z");
        Bind(binaryTableVtable,engine,"??_7LoadTableBinary@GAME@@6B@");
        Bind(customDatabase,engine,"?HasLoadedCustomDatabase@Engine@GAME@@QEBA_NXZ");
        Bind(money,game,"?GetCurrentMoney@Character@GAME@@QEBA?BIXZ");
        Bind(attribute,game,"?GetModifierPoints@Character@GAME@@QEBA?BIXZ");
        Bind(attributeSpent,game,"?GetPointsSpent@Character@GAME@@QEBA?BIW4CharAttributeType@2@@Z");
        Bind(addMoney,game,"?AddMoney@Character@GAME@@QEAAXI@Z");
        Bind(subtractMoney,game,"?SubtractMoney@Character@GAME@@QEAA?BII@Z");
        Bind(changeSkill,game,"?AddToSkillPoints@Character@GAME@@QEAAXH@Z");
        Bind(changeAttribute,game,"?AddToModifierPoints@Character@GAME@@QEAAXH@Z");
        Bind(addDevotion,game,"?AddDevotionPoints@Character@GAME@@QEAAXI@Z");
        Bind(removeDevotion,game,"?RemoveDevotionPoints@Character@GAME@@QEAAXI@Z");
        Bind(addEarned,game,"?AddTotalDevotionPoints@Character@GAME@@QEAAXI@Z");
        Bind(removeEarned,game,"?RemoveTotalDevotionPoints@Character@GAME@@QEAAXI@Z");
        Bind(expansionLoaded[0],engine,"?IsExpansion1Loaded@Engine@GAME@@QEBA_NXZ");
        Bind(expansionLoaded[1],engine,"?IsExpansion2Loaded@Engine@GAME@@QEBA_NXZ");
        Bind(expansionLoaded[2],engine,"?IsExpansion3Loaded@Engine@GAME@@QEBA_NXZ");
        Bind(modName,engine,"?GetModName@GameInfo@GAME@@QEBAAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ");
        auto base=reinterpret_cast<const unsigned char*>(game);
        auto pe=reinterpret_cast<const IMAGE_NT_HEADERS64*>(base+reinterpret_cast<const IMAGE_DOS_HEADER*>(base)->e_lfanew);
        available=MasteryRespec::available && objectManager && getTable && tableInt && tableArrayInt && binaryTableVtable && customDatabase && money && attribute && attributeSpent && addMoney && subtractMoney &&
            changeSkill && changeAttribute && addDevotion && removeDevotion && addEarned && removeEarned && modName &&
            expansionLoaded[0] && expansionLoaded[1] && expansionLoaded[2] &&
            pe->FileHeader.TimeDateStamp==0x6A85FBB3 && pe->OptionalHeader.SizeOfImage==0xAB5000;
    }
    inline const char* ReadProgression(void* player, Snapshot& s)
    {
        // Audited ExperienceLevelControl at player+0x16E8. ReceiveExperience's
        // level-up loop adds +0x2C attributes and indexes the +0x30 int vector,
        // repeating its final element for levels beyond the table length.
        const auto bytes=static_cast<const unsigned char*>(player);
        s.progression.attributePerLevel=*reinterpret_cast<const unsigned*>(bytes+0x1714);
        const auto begin=*reinterpret_cast<const uintptr_t*>(bytes+0x1718);
        const auto end=*reinterpret_cast<const uintptr_t*>(bytes+0x1720);
        if(end<begin || (end-begin)%4 || (end-begin)/4>MaxSupportedLevel || (!begin && end))
            return "ERROR RESOURCE_INVALID_PROGRESSION_TABLE";
        s.progression.skillCount=static_cast<unsigned>((end-begin)/4);
        auto manager=objectManager();
        auto table=manager ? getTable(manager,progressionRecord) : nullptr;
        if(!table || *static_cast<void*const*>(table)!=binaryTableVtable) return "ERROR RESOURCE_PROGRESSION_RECORD_UNAVAILABLE";
        const int initial=tableInt(table,"initialSkillPoints",0);
        if(initial<0 || tableInt(table,"maxPlayerLevel",-1)!=static_cast<int>(s.levelCap) ||
            tableInt(table,"maxDevotionPoints",-1)!=static_cast<int>(s.cap[3]) ||
            tableInt(table,"characterModifierPoints",-1)!=static_cast<int>(s.progression.attributePerLevel))
            return "ERROR RESOURCE_LOADED_RULES_CHANGED_RELOAD_CHARACTER";
        s.progression.initialSkills=static_cast<unsigned>(initial);
        for(unsigned i=0;i<s.progression.skillCount;i++)
        {
            const int award=*reinterpret_cast<const int*>(begin+i*4);
            if(award<0 || static_cast<unsigned>(award)>MaxPointBudget || tableArrayInt(table,"skillModifierPoints",i,-1)!=award)
                return "ERROR RESOURCE_UNSUPPORTED_SKILL_AWARD";
            s.progression.skills[i]=static_cast<unsigned>(award);
        }
        return ApplyProgression(s);
    }
    inline const char* Capture(void* game, void* player, Snapshot& s)
    {
        if (!available) return "ERROR RESOURCE_SET_UNAVAILABLE";
        if (!game || !player || MasteryAudit::loading(game) || !MasteryRespec::alive(game)) return "ERROR RESOURCE_NO_READY_CHARACTER";
        auto engine=MasteryRespec::engineInstance ? *MasteryRespec::engineInstance : nullptr;
        auto info=engine ? MasteryRespec::gameInfo(engine) : nullptr;
        if (!info || !MasteryRespec::singlePlayer(info)) return "ERROR RESOURCE_SINGLE_PLAYER_ONLY";
        if (!modName(info).empty()) return "ERROR RESOURCE_CUSTOM_CAMPAIGN_REWARDS_UNVERIFIED";
        if (MasteryRespec::attacked(player)) return "ERROR RESOURCE_IN_COMBAT";
        auto manager=MasteryAudit::skillManager(player);
        if (!manager || MasteryAudit::skillSet(manager)!=0) return "ERROR RESOURCE_UNSUPPORTED_SKILL_SET";
        s={};s.player=reinterpret_cast<uintptr_t>(player);s.id=MasteryAudit::objectId(player);s.level=MasteryAudit::level(player);
        s.levelCap=*reinterpret_cast<const unsigned*>(static_cast<const unsigned char*>(player)+0x175C);
        for (unsigned i=0;i<3;i++) if (expansionLoaded[i](engine)) s.expansion=i+1;
        s.value={money(player),MasteryAudit::unspent(player),attribute(player),MasteryRespec::devotionPoints(player)};
        const auto skill=static_cast<uint64_t>(MasteryAudit::regular(manager))+MasteryAudit::mastery(manager);
        const auto attrs=static_cast<uint64_t>(attributeSpent(player,1))+attributeSpent(player,2)+attributeSpent(player,3);
        if (skill>10000 || attrs>10000 || s.value[0]>MoneyCap || s.value[1]>1000000 || s.value[2]>1000000 || s.value[3]>1000000)
            return "ERROR RESOURCE_INVALID_POINT_ACCOUNTING";
        s.spent={0,static_cast<unsigned>(skill),static_cast<unsigned>(attrs),MasteryRespec::devotionSpent(manager)};
        s.cap={MoneyCap,0,0,MasteryRespec::devotionMax(player)};
        s.modded=customDatabase(engine);
        if(auto error=ReadProgression(player,s)) return error;
        s.devotionTotal=MasteryRespec::devotionTotal(player);
        auto name=MasteryAudit::playerName(player);
        if (!name || !name[0] || !s.id || !s.level || s.level>s.levelCap) return "ERROR RESOURCE_INVALID_CHARACTER";
        size_t i=0;for (;i<128 && name[i];i++) sprintf_s(s.name+i*4,sizeof(s.name)-i*4,"%04X",unsigned(name[i]));
        return i==128 ? "ERROR RESOURCE_INVALID_CHARACTER" : nullptr;
    }
    inline const char* CheckLevel(void* game, void* player, unsigned target)
    {
        Snapshot s{};
        if (auto error=Capture(game,player,s)) return error;
        return CanAwardLevels(s,target) ? nullptr : "ERROR LEVEL_REWARDS_EXCEED_POINT_BUDGET";
    }
    inline void Run(void* game, void* player, const char* op, unsigned kind, unsigned target,
        unsigned long long token, unsigned long long deadline, char* response, size_t size)
    {
        __try
        {
            if (GetTickCount64()>deadline) { strcpy_s(response,size,"ERROR RESOURCE_REQUEST_EXPIRED");return; }
            Snapshot s{};
            if (auto error=Capture(game,player,s)) { strcpy_s(response,size,error);return; }
            if (strcmp(op,"LIMITS")==0)
            {
                sprintf_s(response,size,"OK RESOURCE_LIMITS %u %u %u %u %u %u %u %u %u %u %u %u %u %u %u",
                    s.level,s.levelCap,s.expansion,s.cap[0],s.cap[1],s.cap[2],s.cap[3],s.spent[1],s.spent[2],s.spent[3],s.value[1],s.value[2],s.value[3],s.devotionTotal,s.modded ? 1u : 0u);return;
            }
            if (strcmp(op,"PREPARE")==0 || strcmp(op,"PREPARE_BYPASS")==0)
            {
                const bool bypass=strcmp(op,"PREPARE_BYPASS")==0;
                if (auto error=transaction.Prepare(s,kind,target,GetTickCount64(),bypass)) { strcpy_s(response,size,error);return; }
                sprintf_s(response,size,"OK RESOURCE_PREPARED %llu %u %u %u %u %u %u %s",transaction.token,
                    kind,s.value[kind],target,Available(s,kind,bypass),s.spent[kind],EffectiveCap(s,kind,bypass),s.name);return;
            }
            if (strcmp(op,"COMMIT")!=0) { strcpy_s(response,size,"ERROR RESOURCE_COMMAND");return; }
            if (auto error=transaction.Consume(s,token,GetTickCount64())) { strcpy_s(response,size,error);return; }
            kind=transaction.kind;target=transaction.target;
            const unsigned before=s.value[kind], delta=target>before ? target-before : before-target;
            auto expected=s;expected.value[kind]=target;
            if (kind==3) expected.devotionTotal=s.spent[3]+target;
            transaction.blocked=true;
            if (delta)
            {
                if (kind==0) { if (target>before) addMoney(player,delta);else subtractMoney(player,delta); }
                else if (kind==1) changeSkill(player,static_cast<int>(target)-static_cast<int>(before));
                else if (kind==2) changeAttribute(player,static_cast<int>(target)-static_cast<int>(before));
                else if (target>before) { addEarned(player,delta);addDevotion(player,delta); }
                else { removeDevotion(player,delta);removeEarned(player,delta); }
            }
            Snapshot after{};
            if (Capture(game,player,after) || !Same(expected,after))
            { strcpy_s(response,size,"ERROR RESOURCE_PARTIAL_OR_UNVERIFIED_DO_NOT_RETRY");return; }
            transaction.blocked=false;
            sprintf_s(response,size,"OK RESOURCES %u %u %u %u",after.value[0],after.value[1],after.value[2],after.value[3]);
        }
        __except(EXCEPTION_EXECUTE_HANDLER)
        { transaction.token=0;transaction.blocked=true;strcpy_s(response,size,"ERROR RESOURCE_UNVERIFIED_DO_NOT_RETRY"); }
    }
}
