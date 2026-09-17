#include "GameplayAssistState.h"
#include <cstdio>
#include <cstdlib>
using namespace GameplayAssists;
void Check(bool condition, const char* label)
{
    if (!condition) { std::fprintf(stderr, "FAIL %s\n", label); std::exit(1); }
    std::printf("PASS %s\n", label);
}
int main()
{
    Lease state;
    Check(!state.Set(1, 1, 100, 0), "no player cannot enable assists");
    state.Context(100, 42, 0);
    auto first = state.Read(0);
    Check(first.mask == 0 && first.speed == 100, "new character starts off");
    Check(!state.Set(first.revision, 16, 100, 0) && !state.Set(first.revision, 8, 201, 0) &&
        !state.Set(first.revision, 8, 99, 0) && !state.Set(first.revision, 1, 125, 0), "invalid masks and speed combinations rejected");
    Check(state.Set(first.revision, 15, 150, 0), "all four toggles accepted for inspected character");
    auto active = state.Read(1);
    Check(!state.Set(first.revision, 1, 100, 1), "duplicate or queued stale apply rejected");
    Check(state.Pulse(active.revision, 2000), "matching heartbeat extends active lease");
    Check(state.Read(5999).mask == 15 && state.Read(6000).mask == 0, "lease expires without companion heartbeat");
    Check(!state.Pulse(active.revision, 6001), "late heartbeat cannot resurrect expired assists");
    Check(!state.Set(active.revision, 15, 150, 6001), "late apply cannot resurrect expired assists");
    auto off = state.Read(6001);
    Check(state.Set(off.revision, 8, 200, 6001), "bounded speed can be enabled again explicitly");
    auto beforeStop = state.Read(6001);
    state.Stop();
    Check(state.Read(6002).mask == 0 && state.Read(6002).speed == 100 &&
        !state.Pulse(beforeStop.revision, 6002) && !state.Set(beforeStop.revision, 8, 200, 6002), "all-off invalidates pending heartbeat and apply");
    auto next = state.Read(6002);
    Check(state.Set(next.revision, 7, 100, 6002), "non-speed assists use normal speed");
    state.Context(100, 43, 6003);
    Check(state.Read(6003).mask == 0, "object-id change at same address stops assists");
    state.Set(state.Read(6003).revision, 1, 100, 6003);
    state.Context(200, 43, 6004);
    Check(state.Read(6004).mask == 0, "pointer change at same object-id stops assists");
    state.Set(state.Read(6004).revision, 1, 100, 6004);
    state.Context(0, 0, 6005);
    Check(state.Read(6005).mask == 0 && !state.Set(state.Read(6005).revision, 1, 100, 6005), "loading missing-player and multiplayer clear context");
    std::puts("ALL GAMEPLAY ASSIST STATE TESTS PASSED");
}
