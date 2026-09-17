namespace GrimDawnCompanion.Core;

public sealed class RegionalQuestMarkerCache
{
    private readonly Dictionary<string, Dictionary<string, LiveMapMarkerRecord>> _regions =
        new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _activeQuestPaths = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LiveMapMarkerRecord> Update(
        string? region,
        IReadOnlyCollection<string> activeQuestPaths,
        IReadOnlyCollection<LiveMapMarkerRecord> liveMarkers,
        WorldPosition player,
        double authoritativeRange)
    {
        var nextQuestPaths = activeQuestPaths
            .Select(NormalizeQuestPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!_activeQuestPaths.SetEquals(nextQuestPaths))
        {
            _regions.Clear();
            _activeQuestPaths = nextQuestPaths;
        }

        var liveQuests = liveMarkers.Where(IsQuestMarker).ToArray();
        if (region is null) return liveQuests;

        if (!_regions.TryGetValue(region, out var cached))
        {
            cached = new Dictionary<string, LiveMapMarkerRecord>(StringComparer.Ordinal);
            _regions[region] = cached;
        }

        var observed = liveQuests.ToDictionary(MarkerKey, StringComparer.Ordinal);
        foreach (var marker in liveQuests) cached[MarkerKey(marker)] = marker;

        // If the game is currently authoritative for an old position but no
        // longer reports the star there, its objective was completed or moved.
        // Farther-away stars remain cached because their objects are unloaded.
        foreach (var (key, marker) in cached.ToArray())
        {
            if (!observed.ContainsKey(key) && Distance(player, marker.Position) <= authoritativeRange)
                cached.Remove(key);
        }

        return cached.Values.ToArray();
    }

    public void Clear()
    {
        _regions.Clear();
        _activeQuestPaths.Clear();
    }

    private static bool IsQuestMarker(LiveMapMarkerRecord marker) => marker.Type == 21;

    private static string MarkerKey(LiveMapMarkerRecord marker) =>
        $"{BitConverter.SingleToUInt32Bits(marker.Position.X):x8}:{BitConverter.SingleToUInt32Bits(marker.Position.Z):x8}";

    private static string NormalizeQuestPath(string value) => value.Replace('\\', '/').Trim().TrimStart('/');

    private static double Distance(WorldPosition left, WorldPosition right)
    {
        var x = left.X - right.X;
        var z = left.Z - right.Z;
        return Math.Sqrt(x * x + z * z);
    }
}
