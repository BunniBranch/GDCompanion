using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GrimDawnCompanion.Core;

public static class NativeRadarFrame
{
    public static IReadOnlyList<string> Commands(ulong context, IEnumerable<NavigationOverlayPoint> points)
    {
        if (context == 0) throw new ArgumentOutOfRangeException(nameof(context));
        var markers = points.ToArray();
        if (markers.Length > 112) throw new ArgumentOutOfRangeException(nameof(points));
        var commands = new List<string> { $"RADAR\tCANVAS\tBEGIN\t{context}\t{markers.Length}" };
        const string prefix = "RADAR\tCANVAS\tADD";
        var batch = prefix;
        foreach (var point in markers)
        {
            var marker = point.Marker;
            if (!float.IsFinite(marker.WorldX) || !float.IsFinite(marker.WorldZ) ||
                Math.Abs(marker.WorldX) > 1_000_000 || Math.Abs(marker.WorldZ) > 1_000_000)
                throw new InvalidDataException("Invalid native radar position.");
            var label = Regex.Replace(marker.DisplayName ?? marker.Category, @"\{[^}]*\}|[\p{Cc}\p{Cf}]", "");
            label = label.Replace('^', ' ').Trim();
            if (label.Length == 0) label = marker.Category;
            if (marker.IsSpawnArea) label += " (possible spawn area)";
            // Truncate at Unicode scalar boundaries, never halfway through UTF-8.
            var bounded = new StringBuilder();
            var bytes = 0;
            foreach (var rune in label.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 192) break;
                bounded.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
            }
            var encoded = Convert.ToHexString(Encoding.UTF8.GetBytes(bounded.ToString()));
            var kind = marker.Category switch
            {
                "Quest Objective" => 0, "Devotion Shrine" => 2, "Riftgate" or "Travel Utility" => 3,
                "One-Shot Chest" => 4, "Vendor" or "Blacksmith" => 5, "Lore Note" => 6, _ => 1
            };
            var entry = string.Create(CultureInfo.InvariantCulture, $"\t{marker.WorldX:R},{marker.WorldZ:R},{kind},{encoded}");
            if (batch.Length + entry.Length > 3500) { commands.Add(batch); batch = prefix; }
            batch += entry;
        }
        if (batch.Length > prefix.Length) commands.Add(batch);
        commands.Add("RADAR\tCANVAS\tCOMMIT");
        return commands;
    }
}

public sealed record NativeRadarStatus(int Drawn, ulong AgeMilliseconds)
{
    public bool Rendering => AgeMilliseconds < 500;
    public static NativeRadarStatus Parse(string response)
    {
        var fields = response.Split(' ');
        if (fields.Length != 4 || fields[0] != "OK" || fields[1] != "RADAR_CANVAS" ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 0 or > 112 ||
            !ulong.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var age))
            throw new InvalidDataException("Invalid native radar status.");
        return new(count, age);
    }
}
