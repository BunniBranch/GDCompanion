#pragma once
#include <cstdint>
#include <mutex>

namespace GameplayAssists
{
    constexpr unsigned Invincible = 1, Energy = 2, Cooldown = 4, Speed = 8;
    struct Snapshot
    {
        uintptr_t player = 0;
        unsigned id = 0, mask = 0, speed = 100;
        uint64_t revision = 1, expires = 0;
    };
    // No saved character values: this state only authorizes temporary hooks.
    class Lease
    {
        std::mutex mutex;
        Snapshot value;
        void Clear() { value.mask = 0; value.speed = 100; value.expires = 0; ++value.revision; }
        void Expire(uint64_t now) { if (value.mask && now >= value.expires) Clear(); }
    public:
        Snapshot Read(uint64_t now) { std::lock_guard lock(mutex); Expire(now); return value; }
        void Stop() { std::lock_guard lock(mutex); Clear(); }
        void Context(uintptr_t player, unsigned id, uint64_t now)
        {
            std::lock_guard lock(mutex); Expire(now);
            if (player != value.player || id != value.id)
            { Clear(); value.player = player; value.id = id; }
        }
        bool Set(uint64_t revision, unsigned mask, unsigned speed, uint64_t now)
        {
            std::lock_guard lock(mutex); Expire(now);
            if (!value.player || !value.id || revision != value.revision || mask > 15 || speed < 100 || speed > 200 ||
                (!(mask & Speed) && speed != 100)) return false;
            value.mask = mask; value.speed = speed; value.expires = now + 4000;
            ++value.revision; return true;
        }
        bool Pulse(uint64_t revision, uint64_t now)
        {
            std::lock_guard lock(mutex); Expire(now);
            if (!value.mask || revision != value.revision) return false;
            value.expires = now + 4000; return true;
        }
    };
}
