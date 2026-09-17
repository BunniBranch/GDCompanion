using System.Globalization;

namespace GrimDawnCompanion.Core;

/// <summary>A same-frame world-to-minimap transform measured through the game's camera.</summary>
public sealed record MinimapProjection(double OriginX, double OriginZ, double OffsetX, double OffsetY,
    double XAxisX, double XAxisY, double ZAxisX, double ZAxisY)
{
    public double DisplayRange => 1 / Math.Sqrt(XAxisX * XAxisX + XAxisY * XAxisY);

    public (double X, double Y) Project(double worldX, double worldZ)
    {
        var x = worldX - OriginX;
        var z = worldZ - OriginZ;
        return (OffsetX + x * XAxisX + z * ZAxisX, OffsetY + x * XAxisY + z * ZAxisY);
    }

    public static MinimapGeometry? ParseFrame(string response)
    {
        if (response == "OK MINIMAP_FRAME HIDDEN") return null;
        var parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (16 or 17) || parts[0] != "OK" || parts[1] != "MINIMAP_FRAME")
            throw new InvalidDataException("Invalid minimap projection frame.");
        var geometry = MinimapGeometry.Parse("OK MINIMAP " + string.Join(" ", parts.Skip(2).Take(6)))!;
        var v = new double[8];
        for (var i = 0; i < v.Length; i++)
            if (!double.TryParse(parts[i + 8], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !double.IsFinite(v[i]))
                throw new InvalidDataException("Invalid minimap projection value.");
        var xs = Math.Sqrt(v[4] * v[4] + v[5] * v[5]);
        var zs = Math.Sqrt(v[6] * v[6] + v[7] * v[7]);
        if (xs is < .001 or > 1 || zs is < .001 or > 1 || Math.Abs(xs - zs) > xs * .02 ||
            Math.Abs(v[4] * v[6] + v[5] * v[7]) > xs * zs * .02 || Math.Abs(v[2]) >= 2 || Math.Abs(v[3]) >= 2)
            throw new InvalidDataException("Unsupported minimap projection scale or orientation.");
        ulong context = 0;
        if (parts.Length == 17 && (!ulong.TryParse(parts[16], NumberStyles.None, CultureInfo.InvariantCulture, out context) || context == 0))
            throw new InvalidDataException("Invalid minimap render context.");
        return geometry with { Projection = new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]), RenderContext = context };
    }
}
