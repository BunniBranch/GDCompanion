#pragma once
#include <cstddef>
#include <string>
#include <vector>
#include <algorithm>

namespace ItemAffixes
{
    // Audited Game.dll ItemReplicaInfo constructor (0x40FF0), copier (0x38840),
    // and Item::CreateItem. Use real MSVC strings; no save-file memory writes.
    struct Replica
    {
        unsigned id=0;
        std::string base,prefix,suffix;
        unsigned seed=0;
        std::string relic,relicPrefix,relicSuffix;
        unsigned relicSeed=0;
        std::string augment;
        uint64_t unknownF8=0;
        std::string appearance,unknown120,unknown140;
        unsigned unknown160=0;
        bool unknown164=true;
        uint64_t unknown168=0,unknown170=0,stack=1,unknown180=0;
        unsigned unknown188=0;
    };
    static_assert(sizeof(std::string)==0x20 && sizeof(Replica)==0x190);
    static_assert(offsetof(Replica,base)==8 && offsetof(Replica,prefix)==0x28 && offsetof(Replica,suffix)==0x48);
    static_assert(offsetof(Replica,seed)==0x68 && offsetof(Replica,augment)==0xD8 && offsetof(Replica,stack)==0x178);
    using Create=void*(__cdecl*)(const Replica&);
    using ReadReplica=void(__fastcall*)(void*,Replica&);
    using Give=void(__fastcall*)(void*,void*,bool,bool);
    using Value=const char*(__fastcall*)(const void*,const char*,const char*);
    using Destroy=void(__fastcall*)(void*,void*,const char*,int);
    using LoadTable=const void*(__fastcall*)(void*,const std::string&);
    inline Create create=nullptr;
    inline ReadReplica readReplica=nullptr;
    inline Give give=nullptr;
    inline Value value=nullptr;
    inline Destroy destroy=nullptr;
    inline LoadTable loadTable=nullptr;
    inline bool available=false,blocked=false;
    inline unsigned seedCounter=0;
    inline void Initialize(HMODULE engine,HMODULE game)
    {
        using MasteryAudit::Bind;
        Bind(create,game,"?CreateItem@Item@GAME@@SAPEAV12@AEBUItemReplicaInfo@2@@Z");
        Bind(readReplica,game,"?GetItemReplicaInfo@Item@GAME@@UEBAXAEAUItemReplicaInfo@2@@Z");
        Bind(give,game,"?GiveItemToCharacter@Player@GAME@@UEAAXPEAVItem@2@_N1@Z");
        Bind(value,engine,"?GetValue@LoadTableBinary@GAME@@UEBAPEBDPEBD0@Z");
        Bind(destroy,engine,"?DestroyObjectEx@ObjectManager@GAME@@QEAAXPEAVObject@2@PEBDH@Z");
        Bind(loadTable,engine,"?LoadTableFile@ObjectManager@GAME@@QEAAPEBVLoadTable@2@AEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@@Z");
        available=CharacterResource::available && create && readReplica && give && value && destroy && loadTable;
    }
    inline std::vector<std::string> Split(const std::string& text,char delimiter)
    {
        std::vector<std::string> fields;size_t start=0;
        for(size_t pos=0;pos<=text.size();pos++) if(pos==text.size() || text[pos]==delimiter)
        { fields.push_back(text.substr(start,pos-start));start=pos+1; }
        return fields;
    }
    inline bool Path(const std::string& text)
    {
        return text.size()>12 && text.size()<256 && text.starts_with("records/") && text.ends_with(".dbr") &&
            text.find("..") == std::string::npos && std::all_of(text.begin(),text.end(),[](unsigned char c){return c>=32 && c<127 && c!='|' && c!='\\';});
    }
    inline std::string RecordPath(const char* text)
    {
        std::string path=text ? text : "";
        std::replace(path.begin(),path.end(),'\\','/');
        std::transform(path.begin(),path.end(),path.begin(),[](unsigned char c){return c>='A' && c<='Z' ? c+32 : c;});
        return path;
    }
    inline const void* Table(const std::string& record)
    {
        if(!Path(record)) return nullptr;
        auto manager=CharacterResource::objectManager();
        // GetLoadTable only looks in the cache; distant/unused affix pools may
        // not be loaded yet. LoadTableFile resolves the active database lazily.
        auto table=manager ? loadTable(manager,record) : nullptr;
        return table && *static_cast<void*const*>(table)==CharacterResource::binaryTableVtable ? table : nullptr;
    }
    inline bool Field(const std::string& text,const std::string& stem)
    {
        if(!text.starts_with(stem) || text.size()<=stem.size() || text.size()>stem.size()+4 || text[stem.size()]=='0') return false;
        return std::all_of(text.begin()+stem.size(),text.end(),[](char c){return c>='0' && c<='9';});
    }
    inline bool PositiveWeight(const void* table,const std::string& field)
    {
        auto weight=field;auto at=weight.find("Name");if(at==std::string::npos) return false;
        weight.replace(at,4,"Weight");return CharacterResource::tableInt(table,weight.c_str(),0)>0;
    }
    inline bool Proof(const std::string& item,const std::string& affix,const std::string& proof,bool prefix)
    {
        if(affix.empty()) return proof.empty();
        if(!Path(affix) || proof.size()>1400) return false;
        auto fields=Split(proof,'|');
        if(fields.size()<3 || fields.size()>16 || !Field(fields[1],"lootName")) return false;
        auto table=Table(fields[0]);
        if(!table || !std::string(value(table,"Class","")).starts_with("LootItemTable") ||
            RecordPath(value(table,fields[1].c_str(),""))!=item || !PositiveWeight(table,fields[1])) return false;
        if(prefix ? !(Field(fields[2],"prefixTableName") || Field(fields[2],"rarePrefixTableName")) :
            !(Field(fields[2],"suffixTableName") || Field(fields[2],"rareSuffixTableName"))) return false;
        std::string current;
        for(size_t i=2;i<fields.size();i++)
        {
            if(i>2 && (!Field(fields[i],"randomizerName") || strcmp(value(table,"Class",""),"LootRandomizerTable"))) return false;
            if(!PositiveWeight(table,fields[i])) return false;
            current=RecordPath(value(table,fields[i].c_str(),""));table=Table(current);if(!table) return false;
        }
        return !_stricmp(current.c_str(),affix.c_str()) && !strcmp(value(table,"Class",""),"LootRandomizer");
    }
    inline void RunImpl(void* game,void* player,const char* payload,unsigned quantity,unsigned long long deadline,char* response,size_t size)
    {
        if(!available || blocked) {strcpy_s(response,size,"ERROR ITEM_AFFIX_UNAVAILABLE_OR_UNVERIFIED");return;}
        if(GetTickCount64()>deadline || !game || !player || MasteryAudit::loading(game) || !MasteryRespec::alive(game) || MasteryRespec::attacked(player))
        {strcpy_s(response,size,"ERROR ITEM_AFFIX_CHARACTER_NOT_READY");return;}
        auto engine=MasteryRespec::engineInstance ? *MasteryRespec::engineInstance : nullptr;
        auto info=engine ? MasteryRespec::gameInfo(engine) : nullptr;
        if(!info || !MasteryRespec::singlePlayer(info)) {strcpy_s(response,size,"ERROR ITEM_AFFIX_SINGLE_PLAYER_ONLY");return;}
        auto fields=Split(payload,'\t');
        if(fields.size()!=5 || quantity<1 || quantity>100 || !Path(fields[0]) || (fields[1].empty() && fields[2].empty()))
        {strcpy_s(response,size,"ERROR ITEM_AFFIX_ARGUMENTS");return;}
        auto base=Table(fields[0]);
        if(!base) {strcpy_s(response,size,"ERROR ITEM_AFFIX_MISSING_ITEM");return;}
        const std::string cls=value(base,"Class",""),rarity=value(base,"itemClassification","");
        if((!cls.starts_with("Weapon") && !cls.starts_with("Armor")) || rarity=="Quest" ||
            !Proof(fields[0],fields[1],fields[3],true) || !Proof(fields[0],fields[2],fields[4],false))
        {strcpy_s(response,size,"ERROR ITEM_AFFIX_INCOMPATIBLE_REFRESH_CATALOG");return;}
        Replica wanted;
        wanted.base=fields[0];wanted.prefix=fields[1];wanted.suffix=fields[2];
        unsigned completed=0;
        for(;completed<quantity;completed++)
        {
            if(GetTickCount64()>deadline) break;
            wanted.seed=(++seedCounter*1664525u+static_cast<unsigned>(GetTickCount64())+1013904223u)&0x7FFFFFFF;
            if(!wanted.seed) wanted.seed=1;
            blocked=true;
            auto item=create(wanted);
            if(!item) break;
            Replica actual;readReplica(item,actual);
            if(_stricmp(actual.base.c_str(),wanted.base.c_str()) || actual.prefix!=wanted.prefix || actual.suffix!=wanted.suffix || actual.seed!=wanted.seed)
            { destroy(CharacterResource::objectManager(),item,"Companion affix verification",0);break; }
            // Player's ordinary give path handles inventory insertion / full-inventory drop.
            // Ownership transfers here; never delete the object after this call.
            give(player,item,false,false);
            blocked=false;
        }
        if(completed!=quantity) {blocked=true;sprintf_s(response,size,"ERROR ITEM_AFFIX_PARTIAL_OR_UNVERIFIED completed=%u requested=%u DO_NOT_RETRY",completed,quantity);return;}
        sprintf_s(response,size,"OK ITEM_AFFIX %u",completed);
    }
    inline void Run(void* game,void* player,const char* payload,unsigned quantity,unsigned long long deadline,char* response,size_t size)
    {
        __try {RunImpl(game,player,payload,quantity,deadline,response,size);}
        __except(EXCEPTION_EXECUTE_HANDLER) {blocked=true;strcpy_s(response,size,"ERROR ITEM_AFFIX_UNVERIFIED_DO_NOT_RETRY");}
    }
}
