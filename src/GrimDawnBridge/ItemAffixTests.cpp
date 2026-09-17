#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <map>
#include <string>
struct TestTable {void* vtable=reinterpret_cast<void*>(8);std::map<std::string,std::string> fields;};
static std::map<std::string,TestTable> tables;
static bool single=true,loading=false,alive=true,combat=false;
namespace MasteryAudit {
 template<class T> void Bind(T&,HMODULE,const char*){}
 inline auto loading=+[](void*){return ::loading;};
}
namespace MasteryRespec {
 inline void* engine=reinterpret_cast<void*>(1);inline void** engineInstance=&engine;
 inline auto gameInfo=+[](void* p){return p;};inline auto singlePlayer=+[](void*){return single;};
 inline auto alive=+[](void*){return ::alive;};inline auto attacked=+[](void*){return combat;};
}
namespace CharacterResource {
 inline bool available=true;inline void* binaryTableVtable=reinterpret_cast<void*>(8);
 inline auto objectManager=+[]()->void*{return reinterpret_cast<void*>(1);};
 inline auto getTable=+[](void*,const std::string& path)->const void*{auto it=tables.find(path);return it==tables.end() ? nullptr : &it->second;};
 inline auto tableInt=+[](const void* p,const char* key,int fallback){auto& f=static_cast<const TestTable*>(p)->fields;auto it=f.find(key);return it==f.end() ? fallback : atoi(it->second.c_str());};
}
#include "ItemAffixes.h"
static void Check(bool v,const char* label){printf("%s %s\n",v ? "PASS" : "FAIL",label);if(!v)exit(1);}
static unsigned creations=0,deliveries=0,cleanups=0;
static bool corrupt=false;
static ItemAffixes::Replica stored;
int main()
{
 using namespace ItemAffixes;
 const std::string item="records/items/test.dbr",affix="records/items/lootaffixes/prefix/test.dbr",pool="records/items/pool.dbr",rule="records/items/rule.dbr";
 tables[item].fields={{"Class","WeaponMelee_Sword"},{"itemClassification","Rare"}};
 tables[affix].fields={{"Class","LootRandomizer"}};
 tables[pool].fields={{"Class","LootRandomizerTable"},{"randomizerName1",affix},{"randomizerWeight1","1"}};
 tables[rule].fields={{"Class","LootItemTable_DynWeight"},{"lootName1",item},{"lootWeight1","1"},{"prefixTableName1",pool},{"prefixTableWeight1","1"}};
 value=+[](const void* p,const char* key,const char* fallback)->const char*{auto& f=static_cast<const TestTable*>(p)->fields;auto it=f.find(key);return it==f.end() ? fallback : it->second.c_str();};
 loadTable=CharacterResource::getTable;
 create=+[](const Replica& input)->void*{creations++;stored=input;return &stored;};
 readReplica=+[](void*,Replica& output){output=stored;if(corrupt)output.prefix="incorrect";};
 give=+[](void*,void*,bool,bool){deliveries++;};
 destroy=+[](void*,void*,const char*,int){cleanups++;};available=true;
 const std::string proof=rule+"|lootName1|prefixTableName1|randomizerName1";
 Check(Proof(item,affix,proof,true) && !Proof(item,affix,proof,false),"native loot graph checks item, side, pool and affix");
 Check(!Proof(item,affix,rule+"|lootName0|prefixTableName1|randomizerName1",true),"invalid index rejected");
 tables[rule].fields["prefixTableWeight1"]="0";Check(!Proof(item,affix,proof,true),"disabled pool rejected");tables[rule].fields["prefixTableWeight1"]="1";
 Check(!Path("records/../test.dbr") && !Path("records/items/test\n.dbr"),"record path controls and traversal rejected");
 char response[1024]{};
 auto run=[&](unsigned n=1){auto payload=item+"\t"+affix+"\t\t"+proof+"\t";Run(reinterpret_cast<void*>(1),reinterpret_cast<void*>(2),payload.c_str(),n,GetTickCount64()+5000,response,sizeof(response));};
 run(2);Check(!strcmp(response,"OK ITEM_AFFIX 2") && creations==2 && deliveries==2 && !blocked && stored.seed,"verified affixes use normal item delivery, including prefix-only payload");
 for(int i=0;i<5;i++){
  single=i!=0;loading=i==1;alive=i!=2;combat=i==3;tables[item].fields["itemClassification"]=i==4 ? "Quest" : "Rare";
  run();Check(!strncmp(response,"ERROR",5) && creations==2,"unready, multiplayer and incompatible gear cannot create items");
 }
 single=alive=true;loading=combat=false;tables[item].fields["itemClassification"]="Rare";
 run(101);Check(creations==2 && !strncmp(response,"ERROR",5),"bounded quantity rejects oversized request");
 corrupt=true;run();Check(blocked && cleanups==1 && deliveries==2 && !strncmp(response,"ERROR",5),"mismatched replica destroyed before ownership transfer and retries blocked");
 run();Check(creations==3 && deliveries==2,"uncertain item operation cannot automatically retry");
}
