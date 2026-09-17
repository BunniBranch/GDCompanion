namespace GrimDawnCompanion.Core;

public sealed record NavigationOverlayPoint(
    WorldMarkerRecord Marker,
    double Distance,
    double HorizontalRatio,
    double VerticalRatio,
    bool IsDistant);

public static class NavigationOverlayProjection
{
    private const int MaximumQuestTargets = 64;
    private const int MaximumPoiTargets = 48;
    private const int MinimumPoiTargets = 8;
    private const int MaximumNearbyTravelTargets = 8;
    private const double PoiEdgeRatio = 0.91;
    private const double QuestEdgeRatio = 1.05;
    private const double InverseSqrtTwo = 0.7071067811865476;

    public static IReadOnlyList<NavigationOverlayPoint> Build(
        WorldPosition player,
        IEnumerable<WorldMarkerRecord> catalog,
        IReadOnlyCollection<string> activeQuestPaths,
        bool showQuests,
        bool showPointsOfInterest,
        double displayRange,
        string? currentLevelPath = null, MinimapProjection? projection = null, QuestNavigationState? questState = null,
        PoiFilter poiFilter = PoiFilter.All)
    {
        displayRange = projection?.DisplayRange ?? Math.Clamp(displayRange, 25, 100);
        var active = activeQuestPaths.Select(NormalizeQuestPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var points = catalog.Select(marker => new Candidate(marker, Distance(player, marker))).ToArray();

        Candidate[] quests = [];
        if (showQuests)
        {
            var liveQuests = points.Where(point => point.Marker.Category == "Quest Objective" && IsLive(point.Marker))
                .OrderBy(point => point.Distance)
                .ToArray();
            var catalogQuests = points.Where(point => point.Marker.Category == "Quest Objective" &&
                                                       !IsLive(point.Marker) &&
                                                       (questState is not null ? questState.Allows(point.Marker) :
                                                        point.Marker.QuestTargets is null &&
                                                        point.Marker.QuestPaths?.Any(path => QuestMatches(active, path)) == true))
                .ToArray();

            // A live minimap pass only contains quest objects loaded around the
            // player. Supplement it with installed placements from the same
            // connected map family (for example, every Region0A tile), so all
            // active objectives in the current overworld or dungeon remain as
            // rim directions even when their objects have not streamed in.
            var currentRegion = LevelRegionFamily(currentLevelPath ?? "") ??
                                FindCurrentRegionFamily(player, points, liveQuests, catalogQuests, displayRange);
            var regionalQuests = currentRegion is null
                ? []
                : catalogQuests.Where(point => string.Equals(LevelRegionFamily(point.Marker.LevelPath), currentRegion,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(point => point.Distance)
                    .ToArray();
            quests = MergeQuestTargets(liveQuests, regionalQuests);
        }

        Candidate[] pois = [];
        if (showPointsOfInterest)
        {
            // Filter before quotas/fallback selection, but retain all original
            // points for quest-region anchoring. POI preferences cannot hide quests.
            var poiPoints = points.Where(point => PoiFilters.Allows(poiFilter, point.Marker.Category)).ToArray();
            // Game-owned live markers are the only reliable nearby POIs: the
            // installed catalog spans separate world maps whose coordinates
            // can overlap. Catalog entries remain useful as distant bearings.
            var live = poiPoints.Where(point => point.Marker.Category != "Quest Objective" && IsLive(point.Marker) &&
                                             point.Distance <= displayRange)
                .OrderBy(point => point.Distance)
                .Take(MaximumPoiTargets)
                .ToArray();
            // Static boats and ferries are absent from some game minimap
            // passes. Their catalog placements are safe and useful nearby,
            // while other catalog POIs stay direction-only to avoid markers
            // from overlapping dungeon coordinate spaces.
            var nearbyTravel = poiPoints.Where(point => point.Marker.Category == "Travel Utility" &&
                                                      !IsLive(point.Marker) && point.Distance <= displayRange)
                .OrderBy(point => point.Distance)
                .Take(MaximumNearbyTravelTargets)
                .ToArray();
            var nearby = live.Concat(nearbyTravel).DistinctBy(point => point.Marker.Id).ToArray();
            var fallbackCount = Math.Max(0, MinimumPoiTargets - nearby.Length);
            var distant = poiPoints.Where(point => point.Marker.Category != "Quest Objective" && !IsLive(point.Marker) &&
                                                point.Distance > displayRange)
                .OrderBy(point => point.Distance)
                .Take(fallbackCount);
            pois = nearby.Concat(distant).Take(MaximumPoiTargets).ToArray();
        }

        return quests.Concat(pois).Select(point => Project(player, point, displayRange, projection)).ToArray();
    }

    private static NavigationOverlayPoint Project(WorldPosition player, Candidate point, double displayRange, MinimapProjection? projection)
    {
        if (projection is not null)
        {
            var (x, y) = projection.Project(point.Marker.WorldX, point.Marker.WorldZ);
            var radius = Math.Sqrt(x * x + y * y);
            var scale = RimScale(radius, point.Marker.Category == "Quest Objective");
            return new NavigationOverlayPoint(point.Marker, point.Distance, x * scale, y * scale, radius > 1);
        }
        // Grim Dawn renders its two ground-plane axes diagonally. Live-frame
        // measurements confirm that +X maps down-right and +Z maps down-left.
        // The sqrt(2) normalization preserves world distance, so displayRange
        // remains the radius of the visible minimap in world units.
        var worldX = point.Marker.WorldX - player.X;
        var worldZ = point.Marker.WorldZ - player.Z;
        var dx = (worldX - worldZ) * InverseSqrtTwo;
        var dy = (worldX + worldZ) * InverseSqrtTwo;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var directionScale = RimScale(length / displayRange, point.Marker.Category == "Quest Objective");
        return new NavigationOverlayPoint(
            point.Marker,
            point.Distance,
            dx * directionScale / displayRange,
            dy * directionScale / displayRange,
            point.Distance > displayRange);
    }

    // Match RadarCanvas::Project. Visible quests stay at their world location;
    // distant stars use a separate lane outside the unchanged POI rim.
    private static double RimScale(double radius, bool quest) => quest
        ? (radius > 1 ? QuestEdgeRatio / radius : 1)
        : (radius > PoiEdgeRatio ? PoiEdgeRatio / radius : 1);

    private static double Distance(WorldPosition player, WorldMarkerRecord marker)
    {
        var x = marker.WorldX - player.X;
        var z = marker.WorldZ - player.Z;
        return Math.Sqrt(x * x + z * z);
    }

    private static string NormalizeQuestPath(string value) => value.Replace('\\', '/').Trim().TrimStart('/');

    private static Candidate[] MergeQuestTargets(IEnumerable<Candidate> live, IEnumerable<Candidate> regional)
    {
        var liveTargets = live.Take(MaximumQuestTargets).ToArray();
        var regionalTargets = regional.ToArray();
        var regionalNames = regionalTargets.Select(candidate => NormalizeObjectiveName(candidate.Marker.DisplayName)).ToArray();
        var uniquelyMatchedRegional = new HashSet<int>();
        foreach (var existing in liveTargets)
        {
            var liveName = NormalizeObjectiveName(existing.Marker.DisplayName);
            var matches = Enumerable.Range(0, regionalTargets.Length)
                .Where(index => ObjectiveLabelMatches(liveName, regionalNames[index])).Take(2).ToArray();
            if (matches.Length == 1) uniquelyMatchedRegional.Add(matches[0]);
        }
        // These are verified outputs from Grim Dawn's own tracked-quest map
        // pass. Render them in the stable world-space overlay: the game may
        // clip even nearby records for some objective states, while the
        // tracked-only bridge filter prevents dormant quest stars from being
        // promoted into this set.
        var merged = new List<Candidate>(liveTargets);
        // Match objectives individually. A partial streamed map pass is not
        // evidence that arbitrary catalog targets are represented; counting
        // stars made a 2 -> 1 live transition replace unrelated objectives.
        var unmatchedRegional = regionalTargets.Where((candidate, index) => !uniquelyMatchedRegional.Contains(index) &&
            !liveTargets.Any(existing => QuestTargetsMatch(existing.Marker, candidate.Marker)))
            .ToArray();

        foreach (var candidate in unmatchedRegional)
        {
            if (merged.Count >= MaximumQuestTargets) break;
            // Corrected catalog positions share the game's world coordinates.
            if (merged.Any(existing => QuestTargetsMatch(existing.Marker, candidate.Marker))) continue;
            merged.Add(candidate);
        }
        return merged.ToArray();
    }

    private static bool QuestTargetsMatch(WorldMarkerRecord left, WorldMarkerRecord right)
    {
        var x = left.WorldX - right.WorldX;
        var z = left.WorldZ - right.WorldZ;
        return x * x + z * z <= 36;
    }

    private static bool ObjectiveLabelMatches(string? liveName, string? catalogName)
    {
        return liveName is not null && catalogName is not null &&
               (" " + liveName + " ").Contains(" " + catalogName + " ", StringComparison.Ordinal);
    }

    private static string? NormalizeObjectiveName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("Active quest objective", StringComparison.OrdinalIgnoreCase)) return null;
        value = System.Text.RegularExpressions.Regex.Replace(value, @"\{\^[^}]*\}", "");
        var normalized = System.Text.RegularExpressions.Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
        return normalized.Length < 3 ? null : normalized;
    }

    private static string? FindCurrentRegionFamily(WorldPosition player, IReadOnlyCollection<Candidate> points,
        IReadOnlyCollection<Candidate> liveQuests, IReadOnlyCollection<Candidate> catalogQuests, double displayRange)
    {
        // An exact live quest star can identify its compiled level even when
        // the region contains no nearby catalog POI.
        var liveMatch = liveQuests
            .SelectMany(live => catalogQuests.Select(catalog => new
            {
                Catalog = catalog,
                Distance = MarkerDistance(live.Marker, catalog.Marker)
            }))
            .Where(match => match.Distance <= Math.Max(96, displayRange * 2))
            .OrderBy(match => match.Distance)
            .FirstOrDefault();
        var matchedFamily = liveMatch is null ? null : LevelRegionFamily(liveMatch.Catalog.Marker.LevelPath);
        if (matchedFamily is not null) return matchedFamily;

        // Otherwise anchor the player to the nearest compiled marker. Include
        // vertical separation in the score to keep overlapping dungeon and
        // overworld coordinate spaces from selecting each other.
        return ResolveCurrentRegionFamily(player, points.Select(point => point.Marker), displayRange);
    }

    public static string? ResolveCurrentRegionFamily(WorldPosition player, IEnumerable<WorldMarkerRecord> catalog,
        double displayRange)
    {
        var maximumAnchorDistance = Math.Max(160, displayRange * 2.5);
        var nearest = catalog
            .Where(marker => !IsLive(marker) && LevelRegionFamily(marker.LevelPath) is not null &&
                             Distance(player, marker) <= maximumAnchorDistance && Math.Abs(marker.WorldY - player.Y) <= 80)
            // Prefer fixed navigation landmarks over generic records when
            // different maps reuse nearby X/Z coordinates.
            .OrderBy(marker => RegionAnchorPriority(marker.Category))
            .ThenBy(marker => Distance(player, marker) + Math.Abs(marker.WorldY - player.Y) * 2)
            .FirstOrDefault();
        return nearest is null ? null : LevelRegionFamily(nearest.LevelPath);
    }

    public static string? ResolveRegionFamily(string? levelPath) => LevelRegionFamily(levelPath ?? "");

    private static int RegionAnchorPriority(string category) => category switch
    {
        "Travel Utility" or "Riftgate" or "Devotion Shrine" => 0,
        "One-Shot Chest" or "Lore Note" => 1,
        "Quest Objective" => 3,
        _ => 2
    };

    private static double MarkerDistance(WorldMarkerRecord left, WorldMarkerRecord right)
    {
        var x = left.WorldX - right.WorldX;
        var z = left.WorldZ - right.WorldZ;
        return Math.Sqrt(x * x + z * z);
    }

    private static string? LevelRegionFamily(string levelPath)
    {
        if (string.IsNullOrWhiteSpace(levelPath) || levelPath.StartsWith("live://", StringComparison.OrdinalIgnoreCase))
            return null;
        var normalized = levelPath.Replace('\\', '/').Trim();
        var slash = normalized.LastIndexOf('/');
        var directory = slash >= 0 ? normalized[..slash] : "";
        var name = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        if (name.EndsWith(".lvl", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        // Surface regions are numbered tiles in a connected lettered world
        // (Region0A001, Region0A010, ...). Dungeons and challenge areas use a
        // descriptive stem followed by A01/B02-style tile suffixes.
        if (name.Length >= 8 && name.StartsWith("Region0", StringComparison.OrdinalIgnoreCase) &&
            char.IsLetter(name[7]))
            name = name[..8];
        else
        {
            var end = name.EndsWith("half", StringComparison.OrdinalIgnoreCase) ? name.Length - 4 : name.Length;
            var digits = end;
            while (digits > 0 && char.IsDigit(name[digits - 1])) --digits;
            if (digits < end)
            {
                if (digits > 0 && char.IsLetter(name[digits - 1])) --digits;
                if (digits > 0 && name[digits - 1] == '_') --digits;
                name = name[..digits];
            }
        }
        return (directory.Length == 0 ? name : directory + '/' + name).ToLowerInvariant();
    }

    private static bool IsLive(WorldMarkerRecord marker) =>
        marker.QuestPaths?.Contains("$live", StringComparer.Ordinal) == true;

    private static bool QuestMatches(IReadOnlySet<string> active, string candidate)
    {
        candidate = NormalizeQuestPath(candidate);
        return active.Any(path => path.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                                  path.EndsWith('/' + candidate, StringComparison.OrdinalIgnoreCase) ||
                                  candidate.EndsWith('/' + path, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record Candidate(WorldMarkerRecord Marker, double Distance);
}
