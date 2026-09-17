#include "RadarCanvasState.h"
#include <cstdio>
#include <cstdlib>

static void Check(bool value, const char* name)
{
    printf("%s %s\n", value ? "PASS" : "FAIL", name);
    if (!value) std::exit(1);
}
int main()
{
    using namespace RadarCanvas;
    State state;
    Check(state.Command("BEGIN\t7\t2", 100) && state.Command("ADD\t104,200,0,5175657374", 101) &&
        state.active.markers.empty(), "radar staging never exposes a partial marker set");
    Check(state.Command("ADD\t105,201,3,426F6174", 102) && state.Command("COMMIT", 103) &&
        state.active.markers.size() == 2 && state.active.markers[1].label == "Boat", "radar frame commits atomically with labels");
    Check(Fresh(state.active, 7, 104) && !Fresh(state.active, 8, 104) && !Fresh(state.active, 7, 1603) &&
        !Fresh(state.active, 7, 102), "radar rejects expired, other-map and time-reversed frames");
    Check(state.Command("BEGIN\t8\t1", 200) && !state.Command("COMMIT", 201) &&
        state.active.context == 7, "incomplete updates cannot replace a committed frame");
    Check(state.Command("BEGIN\t8\t0", 200) && !state.Command("COMMIT", 1201) &&
        state.active.context == 7, "expired transactions cannot revive stale markers");
    Check(!state.Command("BEGIN\t0\t1", 300) && !state.Command("BEGIN\t8\t113", 300), "radar rejects missing context and oversized frames");
    Check(state.Command("BEGIN\t8\t1", 300) &&
        !state.Command("ADD\t1,2,0,41\t3,4,0,42", 301) && !state.Command("COMMIT", 302),
        "too many entries invalidate the whole pending transaction");
    Marker marker;
    for (const auto invalid : {"nan,2,0,41", "1,inf,0,41", "1000001,2,0,41", "1,2,7,41",
        "1,2,0,00", "1,2,0,09", "1,2,0,7F", "1,2,0,GG", "1,2,0,4", "1,2,0,"})
        Check(!ParseMarker(invalid, marker), "radar rejects malformed coordinates, kinds and labels");
    Check(ParseMarker("-1.25,2.5,6,E28886", marker) && marker.label.size() == 3,
        "radar wire supports invariant decimals and UTF-8 labels");
    std::array<float, 8> transform{100,200,0,0,.05f,0,0,.05f};
    marker = {104,200,0,"Quest"};
    auto p = Project(marker, transform);
    Check(fabsf(p[0] - .2f) < .00001f && p[1] == 0, "native projection follows the measured minimap transform");
    transform[0] = 102; transform[1] = 201;
    p = Project(marker, transform);
    Check(fabsf(p[0] - .1f) < .00001f && fabsf(p[1] + .05f) < .00001f,
        "stationary world marker moves correctly with the same-frame camera origin");
    transform = {100,200,0,0,0,.1f,-.1f,0};
    p = Project(marker, transform);
    Check(p[0] == 0 && fabsf(p[1] - .4f) < .00001f, "native projection follows camera rotation and zoom");
    marker.x = 10000;
    p = Project(marker, transform);
    Check(fabsf(hypotf(p[0], p[1]) - 1.05f) < .00001f, "distant quest directions use the outer minimap rim");
    for (unsigned i = 0; i < 8; ++i)
    {
        const float angle = i * .785398163f;
        transform = {0,0,0,0,.05f,0,0,.05f};
        marker = {200 * cosf(angle),200 * sinf(angle),0,"Quest"};
        const auto questPoint = Project(marker, transform);
        marker.kind = 3;
        const auto poiPoint = Project(marker, transform);
        Check(fabsf(hypotf(questPoint[0], questPoint[1]) - 1.05f) < .00001f &&
            fabsf(hypotf(poiPoint[0], poiPoint[1]) - .91f) < .00001f &&
            fabsf(questPoint[0] * poiPoint[1] - questPoint[1] * poiPoint[0]) < .00001f,
            "quest and POI rim lanes remain separate without changing compass bearing");
    }
    marker = {19,0,0,"Visible quest"};
    p = Project(marker, transform);
    Check(fabsf(p[0] - .95f) < .00001f && p[1] == 0,
        "near-edge visible quest is not displaced into the distant lane");
    Check(state.Command("BEGIN\t8\t0", 400) && state.Command("COMMIT", 401) && state.active.markers.empty(),
        "empty target selection removes previous markers");
    Check(state.Command("BEGIN\t8\t1", 500) && state.Command("CLEAR", 501) && !state.Command("COMMIT", 502) &&
        !Fresh(state.active, 8, 502), "disable clears both active and queued frames");
}
