using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace GrimDawnCompanion.Core;

public static partial class GameLocator
{
    private const string RelativeGamePath = "steamapps\\common\\Grim Dawn";

    public static GameInstallation? Locate(string? preferredPath = null)
    {
        foreach (var candidate in EnumerateCandidates(preferredPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsValid(candidate)) return new GameInstallation(Path.GetFullPath(candidate));
        }
        return null;
    }

    public static bool IsValid(string? root) =>
        !string.IsNullOrWhiteSpace(root) &&
        File.Exists(Path.Combine(root, "x64", "Grim Dawn.exe")) &&
        File.Exists(Path.Combine(root, "x64", "Engine.dll")) &&
        File.Exists(Path.Combine(root, "database", "database.arz"));

    public static IReadOnlyList<GameArchive> GetInstalledArchives(GameInstallation game)
    {
        var definitions = new[]
        {
            ("base", "Base Game", "database\\database.arz", "resources\\Text_EN.arc"),
            ("gdx1", "Ashes of Malmouth", "gdx1\\database\\GDX1.arz", "gdx1\\resources\\Text_EN.arc"),
            ("gdx2", "Forgotten Gods", "gdx2\\database\\GDX2.arz", "gdx2\\resources\\Text_EN.arc"),
            ("gdx3", "Fangs of Asterkarn", "gdx3\\database\\GDX3.arz", "gdx3\\resources\\Text_EN.arc"),
            ("crucible1", "The Crucible", "survivalmode1\\database\\SurvivalMode1.arz", "survivalmode1\\resources\\Text_EN.arc"),
            ("crucible2", "The Crucible Update II", "survivalmode2\\database\\SurvivalMode2.arz", "survivalmode2\\resources\\text_en.arc"),
            ("crucible3", "The Crucible Update III", "survivalmode3\\database\\SurvivalMode3.arz", "survivalmode3\\resources\\Text_EN.arc"),
        };

        return definitions
            .Select(x => new GameArchive(x.Item1, x.Item2, Path.Combine(game.RootDirectory, x.Item3), Path.Combine(game.RootDirectory, x.Item4)))
            .Where(x => File.Exists(x.DatabasePath))
            .ToArray();
    }

    private static IEnumerable<string> EnumerateCandidates(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) yield return preferred;
        yield return @"C:\Program Files (x86)\Steam\steamapps\common\Grim Dawn";
        yield return @"C:\Program Files\Steam\steamapps\common\Grim Dawn";

        foreach (var registryPath in new[]
        {
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
        })
        {
            var steam = Registry.GetValue(registryPath, "SteamPath", null)?.ToString()
                        ?? Registry.GetValue(registryPath, "InstallPath", null)?.ToString();
            if (string.IsNullOrWhiteSpace(steam)) continue;
            yield return Path.Combine(steam, RelativeGamePath);

            var libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraries)) continue;
            foreach (Match match in LibraryPathRegex().Matches(File.ReadAllText(libraries)))
            {
                var root = match.Groups[1].Value.Replace("\\\\", "\\");
                yield return Path.Combine(root, RelativeGamePath);
            }
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
}
