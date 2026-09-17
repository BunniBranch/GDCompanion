using System.Globalization;

namespace GrimDawnCompanion.Core;

/// <summary>The visible circular minimap bounds in the game's render pixels.</summary>
public sealed record MinimapGeometry(double Left, double Top, double Width, double Height,
    double RenderWidth, double RenderHeight)
{
    public MinimapProjection? Projection { get; init; }
    public ulong RenderContext { get; init; }
    public static MinimapGeometry? Parse(string response)
    {
        if (response == "OK MINIMAP HIDDEN") return null;
        var parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 8 || parts[0] != "OK" || parts[1] != "MINIMAP")
            throw new InvalidDataException("Invalid minimap bounds response.");
        var values = new double[6];
        for (var index = 0; index < values.Length; index++)
            if (!double.TryParse(parts[index + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]) ||
                !double.IsFinite(values[index])) throw new InvalidDataException("Invalid minimap coordinate.");
        var result = new MinimapGeometry(values[0], values[1], values[2], values[3], values[4], values[5]);
        if (result.Left < 0 || result.Top < 0 || result.Width < 32 || result.Height < 32 ||
            result.RenderWidth is < 640 or > 32768 || result.RenderHeight is < 480 or > 32768 ||
            result.Left + result.Width > result.RenderWidth + 2 || result.Top + result.Height > result.RenderHeight + 2 ||
            Math.Abs(result.Width - result.Height) > 2)
            throw new InvalidDataException("Minimap bounds are outside the game surface.");
        return result;
    }

    // Client dimensions are already WPF DIPs. Native rendering and desktop DPI
    // scaling are converted exactly once; monitor screen origins are separate.
    public RadarRectangle ToClient(double clientWidth, double clientHeight)
    {
        if (!double.IsFinite(clientWidth) || !double.IsFinite(clientHeight) || clientWidth <= 0 || clientHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(clientWidth));
        return new RadarRectangle(Left * clientWidth / RenderWidth, Top * clientHeight / RenderHeight,
            Width * clientWidth / RenderWidth, Height * clientHeight / RenderHeight);
    }
}

public readonly record struct RadarRectangle(double Left, double Top, double Width, double Height);
