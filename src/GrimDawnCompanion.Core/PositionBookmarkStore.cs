using System.Text.Json;

namespace GrimDawnCompanion.Core;

public sealed class PositionBookmarkStore
{
    public static string DefaultPath => Path.Combine(CompanionDataPaths.DirectoryPath, "position-bookmarks.json");
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record Document(int SchemaVersion, List<PositionBookmark> Bookmarks);
    public PositionBookmarkStore(string? path = null) => _path = Path.GetFullPath(path ?? DefaultPath);
    public async Task<IReadOnlyList<PositionBookmark>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return [];
        if (new FileInfo(_path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Bookmark file is too large. It has not been modified.");
        var doc = JsonSerializer.Deserialize<Document>(await File.ReadAllTextAsync(_path, ct), Options);
        if (doc is null || doc.SchemaVersion != 1 || doc.Bookmarks is null || doc.Bookmarks.Count > 1000)
            throw new InvalidDataException("Unsupported or damaged bookmark file. It has not been modified.");
        foreach (var bookmark in doc.Bookmarks) Validate(bookmark);
        if (doc.Bookmarks.Select(b => b.Id).Distinct().Count() != doc.Bookmarks.Count) throw new InvalidDataException("Duplicate bookmark IDs. File has not been modified.");
        ValidateHotkeys(doc.Bookmarks);
        return doc.Bookmarks;
    }
    public Task<IReadOnlyList<PositionBookmark>> AddAsync(PositionBookmark bookmark, CancellationToken ct = default)
    {
        Validate(bookmark);
        return ChangeAsync(list => { if (list.Any(b => b.Id == bookmark.Id)) throw new InvalidOperationException("Bookmark already exists."); list.Add(bookmark); }, ct);
    }
    public Task<IReadOnlyList<PositionBookmark>> DeleteAsync(Guid id, CancellationToken ct = default) =>
        ChangeAsync(list => list.RemoveAll(b => b.Id == id), ct);
    public Task<IReadOnlyList<PositionBookmark>> SetHotkeyAsync(Guid id, BookmarkHotkey? hotkey, CancellationToken ct = default)
    {
        hotkey?.Validate();
        return ChangeAsync(list =>
        {
            var index = list.FindIndex(b => b.Id == id);
            if (index < 0) throw new InvalidOperationException("This bookmark was deleted. Reload the bookmark list.");
            list[index] = list[index] with { Hotkey = hotkey };
        }, ct);
    }
    public Task<IReadOnlyList<PositionBookmark>> SetLocationNamesAsync(IReadOnlyDictionary<Guid, string> names, CancellationToken ct = default) =>
        ChangeAsync(list =>
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i].LocationName is null && names.TryGetValue(list[i].Id, out var name))
                    list[i] = list[i] with { LocationName = name };
        }, ct);
    private static void ValidateHotkeys(List<PositionBookmark> list)
    {
        var assigned = list.Where(b => b.Hotkey is not null).Select(b => b.Hotkey).ToList();
        if (assigned.Distinct().Count() != assigned.Count)
            throw new InvalidDataException("That hotkey is already assigned to another bookmark. Choose a different combination.");
    }
    private async Task<IReadOnlyList<PositionBookmark>> ChangeAsync(Action<List<PositionBookmark>> change, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // Serialize writers across app instances. Re-read before every edit so
        // another instance's bookmarks are preserved. Never overwrite corruption.
        using var writeLock = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var list = (await LoadAsync(ct)).ToList(); change(list);
        foreach (var bookmark in list) Validate(bookmark);
        ValidateHotkeys(list);
        if (list.Count > 1000) throw new InvalidOperationException("The bookmark limit is 1,000. Delete an unused bookmark first.");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new Document(1, list), Options, ct);
                await stream.FlushAsync(ct); stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak");
            else File.Move(temporary, _path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return list;
    }
    private static void Validate(PositionBookmark? bookmark)
    {
        if (bookmark is null || bookmark.Id == Guid.Empty || string.IsNullOrWhiteSpace(bookmark.Name) || bookmark.Name.Length > 60 ||
            bookmark.Name.Any(char.IsControl) || bookmark.Location is null || bookmark.CreatedAt == default ||
            bookmark.MapFingerprint is null || bookmark.MapFingerprint.Length != 64 || !bookmark.MapFingerprint.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid persistent bookmark. File has not been modified.");
        bookmark.Location.Validate();
        if (bookmark.LocationName is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl)))
            throw new InvalidDataException("Invalid bookmark location name. File has not been modified.");
        bookmark.Hotkey?.Validate();
    }
}
