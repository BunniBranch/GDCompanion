using GrimDawnCompanion.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GrimDawnCompanion.App;

public partial class NavigationOverlayWindow : Window
{
    private const double ReferenceHeight = 1080;
    private const double ReferenceMinimapDiameter = 230;
    public string AlignmentStatus { get; private set; } = "Waiting for the game's minimap.";

    public NavigationOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    public int? UpdateOverlay(WorldPosition player, IEnumerable<WorldMarkerRecord> catalog,
        IReadOnlyCollection<string> activeQuestPaths, bool showQuests, bool showPointsOfInterest,
        int? gameProcessId, double horizontalOffset, double verticalOffset, double overlayScale, double displayRange,
        string? currentLevelPath = null, bool automaticAlignment = true, MinimapGeometry? minimap = null,
        QuestNavigationState? questState = null, PoiFilter poiFilter = PoiFilter.All)
    {
        displayRange = Math.Clamp(displayRange, 25, 100);
        overlayScale = Math.Clamp(overlayScale, 0.6, 1.5);
        if (!TryGetGameSurface(gameProcessId, out var surface))
        {
            Hide();
            return null;
        }

        if (automaticAlignment && minimap?.Projection is null)
        {
            AlignmentStatus = "Waiting for a visible minimap with a supported camera/zoom; overlay hidden.";
            Hide();
            return null;
        }
        // Position the HWND in physical screen pixels. Dividing absolute
        // monitor origins by the game's DPI misplaces mixed-DPI/negative-origin
        // monitors. WPF then performs the local pixel-to-DIP conversion.
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (!IsVisible) Show();
        if (!GetWindowRect(handle, out var windowRect) || windowRect.Left != surface.Left || windowRect.Top != surface.Top ||
            windowRect.Right - windowRect.Left != surface.Width || windowRect.Bottom - windowRect.Top != surface.Height)
        {
            if (!SetWindowPos(handle, new nint(-1), (int)surface.Left, (int)surface.Top,
                (int)surface.Width, (int)surface.Height, 0x0010))
            {
                AlignmentStatus = "Could not align the overlay with the game window.";
                Hide();
                return null;
            }
        }
        var dpi = GetDpiForWindow(handle);
        var scale = dpi > 0 ? dpi / 96d : 1d;
        surface = surface with { Width = surface.Width / scale, Height = surface.Height / scale };
        KeepAboveGame();

        var hudScale = surface.Height / ReferenceHeight;
        var diameter = Math.Clamp(ReferenceMinimapDiameter * hudScale * overlayScale, 130, surface.Height * 0.45);
        var rightInset = 14 * hudScale;
        var topInset = 14 * hudScale;
        var radarLeft = surface.Width - diameter - rightInset + horizontalOffset;
        var radarTop = topInset + verticalOffset;
        if (automaticAlignment)
        {
            var rectangle = minimap!.ToClient(surface.Width, surface.Height);
            if (Math.Abs(rectangle.Width - rectangle.Height) > Math.Max(rectangle.Width, rectangle.Height) * .02)
            {
                AlignmentStatus = "Waiting for game rendering to match the resized window.";
                Hide();
                return null;
            }
            radarLeft = rectangle.Left;
            radarTop = rectangle.Top;
            diameter = Math.Min(rectangle.Width, rectangle.Height);
            AlignmentStatus = $"Automatic • minimap {minimap.Width:0} px • range {minimap.Projection!.DisplayRange:0.0} u • zoom and rotation matched";
        }
        else AlignmentStatus = "Manual calibration • automatic alignment is off.";
        GuideRing.Visibility = automaticAlignment ? Visibility.Hidden : Visibility.Visible;
        PlayerMarker.Visibility = automaticAlignment ? Visibility.Hidden : Visibility.Visible;
        PositionRadarElements(radarLeft, radarTop, diameter);
        MarkerCanvas.Children.Clear();

        var candidates = NavigationOverlayProjection.Build(player, catalog, activeQuestPaths,
            showQuests, showPointsOfInterest, displayRange, currentLevelPath, automaticAlignment ? minimap!.Projection : null, questState, poiFilter);
        var hoverTargets = new List<HoverTarget>();

        foreach (var point in candidates)
        {
            var quest = point.Marker.Category == "Quest Objective";
            var center = diameter / 2;
            var x = center + point.HorizontalRatio * center;
            var y = center + point.VerticalRatio * center;
            var glyphScale = Math.Clamp(diameter / ReferenceMinimapDiameter, 0.75, 2.25);
            var markerSize = (quest ? 13 : 8) * glyphScale;
            Shape marker = quest ? CreateQuestStar(markerSize) : new Ellipse
            {
                Width = markerSize,
                Height = markerSize,
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(PoiColor(point.Marker.Category))),
                Stroke = new SolidColorBrush(Color.FromArgb(220, 8, 17, 29)),
                StrokeThickness = Math.Max(1, 1.25 * glyphScale),
                Opacity = point.IsDistant ? 0.88 : 0.98
            };
            marker.Opacity = point.IsDistant ? 0.88 : 0.98;
            Canvas.SetLeft(marker, x - marker.Width / 2);
            Canvas.SetTop(marker, y - marker.Height / 2);
            MarkerCanvas.Children.Add(marker);
            hoverTargets.Add(new HoverTarget(point, radarLeft + x, radarTop + y,
                Math.Max(10, markerSize * 0.75)));
        }
        UpdateHoverTooltip(surface, hoverTargets);
        return candidates.Count;
    }

    private static Polygon CreateQuestStar(double size)
    {
        var points = new PointCollection(10);
        var center = size / 2;
        for (var index = 0; index < 10; ++index)
        {
            var angle = -Math.PI / 2 + index * Math.PI / 5;
            var radius = index % 2 == 0 ? center : center * 0.43;
            points.Add(new Point(center + Math.Cos(angle) * radius, center + Math.Sin(angle) * radius));
        }
        return new Polygon
        {
            Width = size,
            Height = size,
            Points = points,
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFC44D")),
            Stroke = new SolidColorBrush(Color.FromArgb(235, 8, 17, 29)),
            StrokeThickness = Math.Max(1.2, size / 9),
            Stretch = Stretch.None
        };
    }

    private void UpdateHoverTooltip(GameSurface surface, IReadOnlyList<HoverTarget> targets)
    {
        if (!GetCursorPos(out var cursor))
        {
            HoverTooltip.Visibility = Visibility.Collapsed;
            return;
        }
        // Let WPF convert the physical screen cursor into this overlay's DIP
        // coordinate space. Manually dividing by the game window's DPI scale
        // double-scaled the pointer on high-DPI displays, so visible markers
        // could never satisfy the hover hit test.
        var localCursor = PointFromScreen(new Point(cursor.X, cursor.Y));
        var cursorX = localCursor.X;
        var cursorY = localCursor.Y;
        var nearest = targets
            .Select(target => new { Target = target, Distance = Math.Sqrt(
                Math.Pow(target.CenterX - cursorX, 2) + Math.Pow(target.CenterY - cursorY, 2)) })
            .OrderBy(item => item.Distance)
            .FirstOrDefault();
        var radarLeft = Canvas.GetLeft(GuideRing);
        var radarTop = Canvas.GetTop(GuideRing);
        var radarRadius = GuideRing.Width / 2;
        var radarCenterX = radarLeft + radarRadius;
        var radarCenterY = radarTop + radarRadius;
        var cursorInRadar = Math.Pow(cursorX - radarCenterX, 2) + Math.Pow(cursorY - radarCenterY, 2) <=
                            Math.Pow(radarRadius + 4, 2);
        // Grim Dawn uses a large themed cursor whose visible hand does not
        // reliably match its hotspot. Inside the minimap, identify the nearest
        // marker; immediately outside it, retain the marker-sized hit target.
        var hovered = nearest is not null && (cursorInRadar || nearest.Distance <= nearest.Target.HitRadius)
            ? nearest.Target
            : null;
        if (hovered is null)
        {
            HoverTooltip.Visibility = Visibility.Collapsed;
            return;
        }

        var point = hovered.Point;
        HoverTitle.Text = point.Marker.DisplayName ?? point.Marker.Category;
        HoverDetail.Text = point.IsDistant
            ? $"{point.Marker.Category} • {point.Distance:0} units away • direction only"
            : $"{point.Marker.Category} • {point.Distance:0} units away";
        if (point.Marker.IsSpawnArea) HoverDetail.Text += " • possible spawn area";
        HoverTooltip.Visibility = Visibility.Visible;
        HoverTooltip.Measure(new Size(270, double.PositiveInfinity));
        var width = Math.Max(120, HoverTooltip.DesiredSize.Width);
        var height = Math.Max(40, HoverTooltip.DesiredSize.Height);
        Canvas.SetLeft(HoverTooltip, Math.Clamp(cursorX + 14, 8, Math.Max(8, surface.Width - width - 8)));
        Canvas.SetTop(HoverTooltip, Math.Clamp(cursorY + 16, 8, Math.Max(8, surface.Height - height - 8)));
    }

    private void PositionRadarElements(double left, double top, double diameter)
    {
        GuideRing.Width = diameter;
        GuideRing.Height = diameter;
        Canvas.SetLeft(GuideRing, left);
        Canvas.SetTop(GuideRing, top);

        MarkerCanvas.Width = diameter;
        MarkerCanvas.Height = diameter;
        Canvas.SetLeft(MarkerCanvas, left);
        Canvas.SetTop(MarkerCanvas, top);

        var playerWidth = Math.Clamp(diameter / ReferenceMinimapDiameter * 13, 10, 26);
        var playerHeight = playerWidth * 1.18;
        PlayerMarker.Width = playerWidth;
        PlayerMarker.Height = playerHeight;
        Canvas.SetLeft(PlayerMarker, left + diameter / 2 - playerWidth / 2);
        Canvas.SetTop(PlayerMarker, top + diameter / 2 - playerHeight / 2);
    }

    private static string PoiColor(string category) => category switch
    {
        "Devotion Shrine" => "#FFFF914D",
        "Riftgate" or "Travel Utility" => "#FF5FA8FF",
        "One-Shot Chest" => "#FFFFDF70",
        _ => "#FF42E8C6"
    };

    private static bool TryGetGameSurface(int? gameProcessId, out GameSurface surface)
    {
        surface = default;
        if (gameProcessId is not int processId) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            var gameWindow = FindGameWindow(process);
            var foreground = GetForegroundWindow();
            if (foreground != 0 && !BelongsToProcess(foreground, processId)) return false;

            if (gameWindow != 0)
            {
                if (IsIconic(gameWindow) || !GetClientRect(gameWindow, out var client)) return false;
                var origin = new PointI();
                if (!ClientToScreen(gameWindow, ref origin)) return false;
                var width = client.Right - client.Left;
                var height = client.Bottom - client.Top;
                if (width < 640 || height < 480) return false;
                surface = new GameSurface(origin.X, origin.Y, width, height);
                return true;
            }

            // Do not guess a primary-monitor surface when no game HWND can be
            // identified. A hidden overlay is safer than an incorrectly placed one.
        }
        catch { }
        return false;
    }

    private static nint FindGameWindow(Process process)
    {
        if (process.MainWindowHandle != 0) return process.MainWindowHandle;
        nint best = 0;
        long bestArea = 0;
        _ = EnumWindows((window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var owner);
            if (owner != process.Id || !IsWindowVisible(window) || !GetClientRect(window, out var rect)) return true;
            var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea) { bestArea = area; best = window; }
            return true;
        }, 0);
        return best;
    }

    private static bool BelongsToProcess(nint window, int processId)
    {
        _ = GetWindowThreadProcessId(window, out var owner);
        return owner == processId;
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        style |= 0x00000020L | 0x08000000L | 0x00000080L; // TRANSPARENT | NOACTIVATE | TOOLWINDOW
        _ = SetWindowLongPtr(handle, -20, new nint(style));
    }

    private void KeepAboveGame()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
            _ = SetWindowPos(handle, new nint(-1), 0, 0, 0, 0, 0x0053); // TOPMOST; no move, size, or activation
    }

    private sealed record HoverTarget(NavigationOverlayPoint Point, double CenterX, double CenterY, double HitRadius);
    private readonly record struct GameSurface(double Left, double Top, double Width, double Height);
    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointI { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint window, ref PointI point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointI point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
