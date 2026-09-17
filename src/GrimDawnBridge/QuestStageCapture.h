#pragma once
#include <cstdint>
#include <cstring>
#include <string>
#include <unordered_set>
#include <vector>

namespace QuestStageCapture
{
    struct Api
    {
        const std::string& (*fileName)(void*);
        unsigned int (*numTasks)(void*);
        void* (*taskByIndex)(void*, int);
        unsigned int (*taskUid)(void*);
        bool (*inProgress)(void*, bool);
    };

    // Called synchronously while the game owns the quest vector. Serialize
    // values only, never retain pointers or expose a truncated snapshot.
    inline bool Read(const unsigned char* first, const unsigned char* last, const Api& api, std::string& output)
    {
        output.clear();
        const auto begin = reinterpret_cast<uintptr_t>(first), end = reinterpret_cast<uintptr_t>(last);
        if (!api.fileName || !api.numTasks || !api.taskByIndex || !api.taskUid || !api.inProgress ||
            end < begin || (end - begin) % sizeof(void*) != 0 || (end - begin) / sizeof(void*) > 128 ||
            (!begin && end)) return false;
        const auto count = (end - begin) / sizeof(void*);
        std::string entries;
        std::unordered_set<std::string> paths;
        for (size_t i = 0; i < count; ++i)
        {
            void* quest = nullptr;
            memcpy(&quest, first + i * sizeof(void*), sizeof(quest));
            if (!quest) return false;
            const auto& path = api.fileName(quest);
            const auto taskCount = api.numTasks(quest);
            if (path.empty() || path.size() >= 192 || path.find_first_of("\t\r\n|,") != std::string::npos || taskCount > 512)
                return false;
            auto normalized = path;
            for (auto& ch : normalized)
            {
                if (ch == '\\') ch = '/';
                else if (ch >= 'A' && ch <= 'Z') ch += 'a' - 'A';
            }
            if (!paths.insert(normalized).second) return false;
            entries += "\t" + normalized + "|";
            bool firstTask = true;
            for (unsigned int j = 0; j < taskCount; ++j)
            {
                auto task = api.taskByIndex(quest, static_cast<int>(j));
                if (!task) return false;
                // Ordinary, non-forced query retains per-task suppression.
                if (!api.inProgress(task, false)) continue;
                if (!firstTask) entries += ',';
                entries += std::to_string(api.taskUid(task));
                firstTask = false;
            }
            if (entries.size() > 8192 - 64) return false;
        }
        output = "OK QUEST_TASKS " + std::to_string(count) + entries;
        return true;
    }

    inline bool Fresh(unsigned long long captured, unsigned long long now)
    { return captured != 0 && now >= captured && now - captured <= 3000; }

    // Supported-image repository list, audited against GetQuests: head at +8,
    // next at node+0, Quest2* at node+24. Read on the game thread only. This
    // owns only a local pointer list, never a game vector or allocator block.
    inline bool ReadTrackedRepository(void* repository, bool (*tracked)(void*), const Api& api, std::string& output)
    {
        output.clear();
        if (!repository || !tracked || !api.numTasks || !api.taskByIndex || !api.inProgress) return false;
        auto pointer = [](const void* object, size_t offset) {
            void* result = nullptr;
            memcpy(&result, static_cast<const unsigned char*>(object) + offset, sizeof(result));
            return result;
        };
        const auto head = pointer(repository, 8);
        if (!head) return false;
        std::vector<void*> quests;
        std::unordered_set<void*> visited;
        for (auto node = pointer(head, 0); node != head; node = pointer(node, 0))
        {
            if (!node || visited.size() >= 4096 || !visited.insert(node).second) return false;
            const auto quest = pointer(node, 24);
            if (!quest) return false;
            // IsTracked is a preference bit, also true on dormant/completed
            // definitions. Match GetQuests(Filter::Tracked): require a current
            // unsuppressed task BEFORE counting against the active-list limit.
            if (tracked(quest))
            {
                const auto taskCount = api.numTasks(quest);
                if (taskCount > 512) return false;
                bool active = false;
                for (unsigned i = 0; i < taskCount; ++i)
                {
                    const auto task = api.taskByIndex(quest, static_cast<int>(i));
                    if (!task) return false;
                    if (api.inProgress(task, false)) { active = true; break; }
                }
                if (active) quests.push_back(quest);
            }
            if (quests.size() > 128) return false;
        }
        if (quests.empty()) return Read(nullptr, nullptr, api, output);
        const auto first = reinterpret_cast<const unsigned char*>(quests.data());
        return Read(first, first + quests.size() * sizeof(void*), api, output);
    }
}
