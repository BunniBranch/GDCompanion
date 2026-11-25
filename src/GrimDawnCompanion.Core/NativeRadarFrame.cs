using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GrimDawnCompanion.Core;

public static class NativeRadarFrame
{
    private const int MaxTooltipChars = 48;
    private const int MaxLabelUtf8Bytes = 192;

    public static IReadOnlyList<string> Commands(ulong context, IEnumerable<NavigationOverlayPoint> points,
        bool supportsTravelKind = true, string lane = "RADAR")
    {
        if (context == 0) throw new ArgumentOutOfRangeException(nameof(context));
        var markers = points.ToArray();
        if (markers.Length > 112) throw new ArgumentOutOfRangeException(nameof(points));
        var commands = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{lane}\tCANVAS\tBEGIN\t{context}\t{markers.Length}") };
        var prefix = lane + "\tCANVAS\tADD";
        var batch = prefix;
        foreach (var point in markers)
        {
            var marker = point.Marker;
            if (!float.IsFinite(marker.WorldX) || !float.IsFinite(marker.WorldZ) ||
                Math.Abs(marker.WorldX) > 1_000_000 || Math.Abs(marker.WorldZ) > 1_000_000)
                throw new InvalidDataException("Invalid native radar position.");
            var encoded = Convert.ToHexString(Encoding.UTF8.GetBytes(FormatLabel(point)));
            var kind = marker.Category switch
            {
                "Quest Objective" => 0,
                "Devotion Shrine" => 2,
                "Riftgate" => 3,
                "One-Shot Chest" => 4,
                "Vendor" or "Blacksmith" => 5,
                "Lore Note" => 6,
                "Travel Utility" when !string.Equals(marker.RecordPath, "live://minimap/3", StringComparison.OrdinalIgnoreCase) =>
                    supportsTravelKind ? 7 : 3,
                "Travel Utility" => 3,
                _ => 1
            };
            var entry = string.Create(CultureInfo.InvariantCulture, $"\t{marker.WorldX:R},{marker.WorldZ:R},{kind},{encoded}");
            if (batch.Length + entry.Length > 3500) { commands.Add(batch); batch = prefix; }
            batch += entry;
        }
        if (batch.Length > prefix.Length) commands.Add(batch);
        commands.Add(lane + "\tCANVAS\tCOMMIT");
        return commands;
    }

    private static string FormatLabel(NavigationOverlayPoint point)
    {
        var marker = point.Marker;
        var label = Regex.Replace(marker.DisplayName ?? marker.Category, @"\{[^}]*\}|[\p{Cc}\p{Cf}]", "");
        label = label.Replace('^', ' ').Trim();
        if (label.Length == 0) label = marker.Category;
        var distanceText = double.IsFinite(point.Distance) && point.Distance >= 0 && point.Distance < 1_000_000
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(point.Distance, MidpointRounding.AwayFromZero)}m")
            : null;
        var suffix = (marker.IsSpawnArea, distanceText) switch
        {
            (true, { } d) => $" (possible spawn area, {d})",
            (true, null) => " (possible spawn area)",
            (false, { } d) => $" ({d})",
            _ => ""
        };
        var suffixBytes = Encoding.UTF8.GetByteCount(suffix);
        if (label.Length + suffix.Length <= MaxTooltipChars &&
            Encoding.UTF8.GetByteCount(label) + suffixBytes <= MaxLabelUtf8Bytes)
            return label + suffix;

        const string ellipsis = "\u2026";
        var maxPrefixChars = Math.Max(0, MaxTooltipChars - ellipsis.Length - suffix.Length);
        var maxPrefixBytes = Math.Max(0, MaxLabelUtf8Bytes - Encoding.UTF8.GetByteCount(ellipsis) - suffixBytes);
        var bounded = new StringBuilder();
        var chars = 0;
        var bytes = 0;
        foreach (var rune in label.EnumerateRunes())
        {
            if (chars + rune.Utf16SequenceLength > maxPrefixChars ||
                bytes + rune.Utf8SequenceLength > maxPrefixBytes)
                break;
            bounded.Append(rune.ToString());
            chars += rune.Utf16SequenceLength;
            bytes += rune.Utf8SequenceLength;
        }
        return bounded.Append(ellipsis).Append(suffix).ToString();
    }
}

/// <summary>A live full-screen map render context; null when the map is hidden.</summary>
public sealed record NativeMapFrame(ulong RenderContext)
{
    public static NativeMapFrame? Parse(string response)
    {
        if (response == "OK MAP_FRAME HIDDEN") return null;
        var parts = response.Split(' ');
        if (parts.Length != 4 || parts[0] != "OK" || parts[1] != "MAP_FRAME" || parts[2] != "OPEN" ||
            !ulong.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var context) || context == 0)
            throw new InvalidDataException("Invalid full-screen map frame.");
        return new(context);
    }
}

public sealed record NativeRadarStatus(int Drawn, ulong AgeMilliseconds)
{
    public bool Rendering => AgeMilliseconds < 500;
    public static NativeRadarStatus Parse(string response, string lane = "RADAR_CANVAS")
    {
        var fields = response.Split(' ');
        if (fields.Length != 4 || fields[0] != "OK" || fields[1] != lane ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 0 or > 112 ||
            !ulong.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var age))
            throw new InvalidDataException("Invalid native radar status.");
        return new(count, age);
    }
}
