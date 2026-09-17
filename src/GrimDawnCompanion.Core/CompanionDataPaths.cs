namespace GrimDawnCompanion.Core;

/// <summary>Normal user storage is unchanged; window smoke tests get a fresh, isolated profile.</summary>
public static class CompanionDataPaths
{
    public static string DirectoryPath { get; } = Environment.GetCommandLineArgs()
        .Contains("--smoke-test-window", StringComparer.OrdinalIgnoreCase)
        ? Path.Combine(Path.GetTempPath(), "GrimDawnCompanionSmoke", Guid.NewGuid().ToString("N"))
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrimDawnCompanion");
}
