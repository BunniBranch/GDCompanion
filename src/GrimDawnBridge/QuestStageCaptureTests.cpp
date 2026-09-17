#include "QuestStageCapture.h"
#include <cstdio>
#include <cstdlib>
#include <vector>

struct Task { unsigned int uid; bool active; };
struct Quest { std::string path; std::vector<Task> tasks; bool tracked = true; };
static void Check(bool value, const char* name)
{
    printf("%s %s\n", value ? "PASS" : "FAIL", name);
    if (!value) std::exit(1);
}
int main()
{
    const QuestStageCapture::Api api{
        [](void* q) -> const std::string& { return static_cast<Quest*>(q)->path; },
        [](void* q) { return static_cast<unsigned int>(static_cast<Quest*>(q)->tasks.size()); },
        [](void* q, int i) -> void* { return &static_cast<Quest*>(q)->tasks.at(i); },
        [](void* t) { return static_cast<Task*>(t)->uid; },
        [](void* t, bool forced) { if (forced) Check(false, "quest capture never forces suppressed tasks active"); return static_cast<Task*>(t)->active; }
    };
    Quest quest{"Quests\\test.qst", {{1, false}, {0xFFFFFFFF, true}, {3, true}}};
    void* pointer = &quest;
    const auto first = reinterpret_cast<const unsigned char*>(&pointer);
    std::string output;
    Check(QuestStageCapture::Read(first, first + sizeof(pointer), api, output) &&
        output == "OK QUEST_TASKS 1\tquests/test.qst|4294967295,3", "native snapshot retains only in-progress task IDs and normalizes paths");
    quest.tasks[1].active = false;
    Check(QuestStageCapture::Read(first, first + sizeof(pointer), api, output) &&
        output == "OK QUEST_TASKS 1\tquests/test.qst|3", "next native snapshot removes a completed stage");
    Check(QuestStageCapture::Read(nullptr, nullptr, api, output) && output == "OK QUEST_TASKS 0", "empty tracked list clears all stages");
    Check(!QuestStageCapture::Read(first, first + 1, api, output) && output.empty(), "partial pointer entries never expose a partial snapshot");
    pointer = nullptr;
    Check(!QuestStageCapture::Read(first, first + sizeof(pointer), api, output) && output.empty(), "null quest fails closed");
    pointer = &quest;
    quest.path = "quests/test.qst|bad";
    Check(!QuestStageCapture::Read(first, first + sizeof(pointer), api, output), "wire delimiters are rejected");
    quest.path = "quests/test.qst";
    quest.tasks.resize(513);
    Check(!QuestStageCapture::Read(first, first + sizeof(pointer), api, output), "unbounded task counts are rejected before traversal");
    quest.tasks.clear();
    void* duplicate[] = { &quest, &quest };
    Check(!QuestStageCapture::Read(reinterpret_cast<unsigned char*>(duplicate),
        reinterpret_cast<unsigned char*>(duplicate) + sizeof(duplicate), api, output), "duplicate quest paths cannot make an ambiguous snapshot");
    auto missing = api; missing.inProgress = nullptr;
    Check(!QuestStageCapture::Read(first, first + sizeof(pointer), missing, output), "missing quest getters are rejected");
    Check(!QuestStageCapture::Fresh(0, 100) && QuestStageCapture::Fresh(100, 3100) &&
        !QuestStageCapture::Fresh(100, 3101) && !QuestStageCapture::Fresh(100, 99), "stale or absent captures cannot resurrect old quest stages");
    struct Node { Node* next; void* previous; void* unused; Quest* quest; };
    struct Repository { void* unused; Node* head; };
    quest.tasks = {{3, true}};
    Quest hidden{"quests/hidden.qst", {{4, true}}, false};
    Node head{}, firstNode{}, secondNode{};
    head.next = &firstNode; firstNode = {&secondNode, nullptr, nullptr, &quest};
    secondNode = {&head, nullptr, nullptr, &hidden};
    Repository repository{nullptr, &head};
    const auto tracked = +[](void* q) { return static_cast<Quest*>(q)->tracked; };
    Check(QuestStageCapture::ReadTrackedRepository(&repository, tracked, api, output) &&
        output == "OK QUEST_TASKS 1\tquests/test.qst|3", "direct quest query needs no map render or previously observed quest list");
    quest.tasks = {{3, false}, {9, true}};
    Check(QuestStageCapture::ReadTrackedRepository(&repository, tracked, api, output) &&
        output == "OK QUEST_TASKS 1\tquests/test.qst|9", "direct query follows task changes while quest targets are unloaded");
    quest.tracked = false;
    Check(QuestStageCapture::ReadTrackedRepository(&repository, tracked, api, output) && output == "OK QUEST_TASKS 0",
        "untracking a quest removes its directions on the next query");
    secondNode.next = &firstNode;
    Check(!QuestStageCapture::ReadTrackedRepository(&repository, tracked, api, output), "cyclic repository list is bounded and fails closed");
    secondNode.next = nullptr;
    Check(!QuestStageCapture::ReadTrackedRepository(&repository, tracked, api, output), "broken repository list fails closed");
    head.next = &head;
    Check(QuestStageCapture::ReadTrackedRepository(&repository, tracked, api, output) && output == "OK QUEST_TASKS 0",
        "empty repository clears objectives without a stale snapshot");
    std::vector<Quest> definitions;
    for (unsigned i = 0; i < 466; ++i)
        definitions.push_back({"quests/definition" + std::to_string(i) + ".qst", {{1000 + i, false}}, true});
    for (unsigned i : {20u, 180u, 300u, 465u}) definitions[i].tasks[0].active = true;
    std::vector<Node> nodes(definitions.size());
    for (size_t i = 0; i < nodes.size(); ++i)
        nodes[i] = {i + 1 < nodes.size() ? &nodes[i + 1] : &head, nullptr, nullptr, &definitions[i]};
    head.next = nodes.data();
    Check(QuestStageCapture::ReadTrackedRepository(&repository, tracked, api, output) &&
        output == "OK QUEST_TASKS 4\tquests/definition20.qst|1020\tquests/definition180.qst|1180\tquests/definition300.qst|1300\tquests/definition465.qst|1465",
        "live-session regression: 466 tracking flags and four active quests produce a complete four-quest snapshot");
    for (auto& definition : definitions) definition.tasks[0].active = false;
    Check(QuestStageCapture::ReadTrackedRepository(&repository, tracked, api, output) && output == "OK QUEST_TASKS 0",
        "hundreds of dormant/completed tracked definitions produce a valid empty snapshot");
    for (size_t i = 0; i < 129; ++i) definitions[i].tasks[0].active = true;
    Check(!QuestStageCapture::ReadTrackedRepository(&repository, tracked, api, output),
        "genuinely oversized active list still fails closed without truncation");
    return 0;
}
