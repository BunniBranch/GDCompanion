using System.Text.Json.Serialization;

namespace GrimDawnCompanion.Core;

public sealed record GameInstallation(string RootDirectory)
{
    public string ExecutablePath => Path.Combine(RootDirectory, "x64", "Grim Dawn.exe");
    public string EnginePath => Path.Combine(RootDirectory, "x64", "Engine.dll");
    public string GameDllPath => Path.Combine(RootDirectory, "x64", "Game.dll");
    public string ArchiveToolPath => Path.Combine(RootDirectory, "ArchiveTool.exe");
}

public sealed record GameArchive(string Id, string DisplayName, string DatabasePath, string? TextPath);

public sealed record ItemRecord(
    string Name,
    string RecordPath,
    string Source,
    string Category,
    string Classification,
    int ItemLevel,
    string NameTag,
    string InternalClass)
{
    public bool NameUnavailable { get; init; }
}

public sealed record QuestTokenRecord(string Name, string Source, int References);

public sealed record CharacterResources(uint Money, uint SkillPoints, uint AttributePoints, uint DevotionPoints);

public sealed record WorldPosition(float X, float Y, float Z);

public sealed record LiveMapMarkerRecord(uint Type, WorldPosition Position, string? Label = null);

public sealed record PositionBookmark(Guid Id, string Name, DateTimeOffset CreatedAt, BookmarkLocation Location, string MapFingerprint)
{
    public string? LocationName { get; init; }
    public BookmarkHotkey? Hotkey { get; init; }
    [JsonIgnore] public string LocationDisplay => LocationName ?? "Location not yet identified";
    [JsonIgnore] public string HotkeyDisplay => Hotkey?.ToString() ?? "—";
    [JsonIgnore] public string Character => Location.CharacterName;
    [JsonIgnore] public string Area => Path.GetFileNameWithoutExtension(Location.Region.Replace('\\', '/'));
    [JsonIgnore] public string Difficulty => Location.Difficulty switch { 0 => "Normal", 1 => "Elite", 2 => "Ultimate", _ => "Unknown" };
}

public sealed record WorldMarkerRecord(
    string Id,
    string Category,
    string RecordPath,
    string LevelPath,
    float WorldX,
    float WorldY,
    float WorldZ,
    string[]? QuestPaths = null,
    string? DisplayName = null,
    QuestTargetBinding[]? QuestTargets = null,
    string[]? RequiredTokens = null,
    string[]? ExcludedTokens = null,
    bool IsSpawnArea = false);

public sealed record QuestTargetBinding(string QuestPath, uint TaskUid);
public sealed record QuestSpawnLink(string RecordPath, string[] RequiredTokens, string[] ExcludedTokens);
public sealed record QuestNavigationRecord(string RecordPath, string? DisplayName,
    QuestTargetBinding[] Targets, string[] Children, string? OnAddToWorld);

public sealed class WorldMarkerCatalog
{
    public int SchemaVersion { get; init; } = 1;
    public string SourceFingerprint { get; init; } = "";
    public DateTimeOffset GeneratedAt { get; init; }
    public List<WorldMarkerRecord> Markers { get; init; } = [];
}

public sealed class CatalogDocument
{
    public int SchemaVersion { get; init; } = CatalogService.CurrentSchema;
    public string SourceFingerprint { get; init; } = "";
    public DateTimeOffset GeneratedAt { get; init; }
    public List<CatalogSource> Sources { get; init; } = [];
    public List<ItemRecord> Items { get; init; } = [];
    public List<AffixCatalogRecord> AffixRecords { get; init; } = [];
    public Dictionary<string, string[]> QuestRecordAssociations { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, QuestNavigationRecord> QuestNavigationRecords { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record CatalogSource(string Id, string DisplayName, string DatabaseSha256, string? TextSha256);

public enum VerificationLevel { Passed, Warning, Failed }

public sealed record SymbolVerification(
    string Module,
    string DecoratedName,
    string FriendlyName,
    bool Required,
    bool Found,
    uint Rva,
    string Section,
    string Fingerprint,
    string Message);

public sealed class CompatibilityReport
{
    public DateTimeOffset VerifiedAt { get; init; }
    public string GameDirectory { get; init; } = "";
    public string EngineSha256 { get; init; } = "";
    public string GameDllSha256 { get; init; } = "";
    public VerificationLevel Level { get; init; }
    public bool ProfileUpdated { get; init; }
    public List<SymbolVerification> Symbols { get; init; } = [];
    [JsonIgnore] public bool CanConnect => Level != VerificationLevel.Failed && Symbols.Where(x => x.Required).All(x => x.Found);
}

public sealed record CatalogProgress(string Stage, int Current, int Total, string Detail)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Current * 100d / Total, 0, 100);
}
