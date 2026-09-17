namespace GrimDawnCompanion.Core;

public sealed partial class GameBridgeClient
{
    public bool SupportsPersistentBookmarks => ProtocolVersion >= 29 && Capabilities.Contains("PERSISTENT_BOOKMARKS") && Capabilities.Contains("SHARED_BOOKMARKS") && Capabilities.Contains("CROSS_MODE_BOOKMARKS");
    public bool SupportsBookmarkLabels => ProtocolVersion >= 28 && Capabilities.Contains("BOOKMARK_LABELS");
    public bool SupportsBookmarkLoading => SupportsPersistentBookmarks && ProtocolVersion >= 37 && Capabilities.Contains("BOOKMARK_LOADING");
    private readonly SemaphoreSlim _bookmarkTravelLock = new(1, 1);
    public async Task<string?> ReadBookmarkNameAsync(BookmarkLocation location, CancellationToken ct = default)
    {
        EnsurePersistentBookmarks();
        if (!SupportsBookmarkLabels) return null;
        return ParseBookmarkName(await SendAsync("BOOKMARK\tLABEL " + location.ToWire(), ct));
    }
    public static string? ParseBookmarkName(string response)
    {
        const string prefix = "OK BOOKMARK LABEL ";
        if (!response.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException(BookmarkError(response));
        var value = response[prefix.Length..];
        if (value == "-") return null;
        var name = new System.Text.UnicodeEncoding(false, false, true).GetString(Convert.FromHexString(value));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl))
            throw new InvalidDataException("Invalid bookmark location name.");
        return name;
    }
    public async Task<BookmarkCapture> ReadBookmarkLocationAsync(CancellationToken ct = default)
    {
        EnsurePersistentBookmarks();
        return BookmarkCapture.ParseResponse(await SendAsync("BOOKMARK\tREAD", ct));
    }
    public async Task ReturnToBookmarkAsync(BookmarkLocation location, uint expectedPlayerId, CancellationToken ct = default, Action<string>? progress = null)
    {
        EnsurePersistentBookmarks();
        if (expectedPlayerId == 0) throw new InvalidOperationException("Inspect the loaded character before confirming travel.");
        var wire = location.ToWire();
        if (!await _bookmarkTravelLock.WaitAsync(0, ct)) throw new InvalidOperationException("Bookmark travel is already in progress.");
        var pipe = _pipe;
        async Task<string> SendTravelCommand(string command, CancellationToken token)
        {
            if (!ReferenceEquals(pipe, _pipe)) throw new IOException("The game connection changed.");
            try { return await SendAsync(command, token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // A timed-out response may still arrive. Never leave that line
                // available to be mistaken for a subsequent command's reply.
                if (ReferenceEquals(pipe, _pipe)) AbortPipe();
                throw;
            }
        }
        try
        {
            await BookmarkTravelWorkflow.RunAsync(FormattableString.Invariant($"BOOKMARK\t{(SupportsBookmarkLoading ? "TRAVEL" : "RETURN")} {expectedPlayerId} {wire}"),
                SendTravelCommand,
                progress, ct);
        }
        finally { _bookmarkTravelLock.Release(); }
    }
    private void EnsurePersistentBookmarks()
    {
        if (!IsConnected || !SupportsPersistentBookmarks)
            throw new InvalidOperationException("Reconnect with this Companion build and a supported game patch to use persistent bookmarks.");
    }
    public static string BookmarkError(string response) => response switch
    {
        "ERROR BOOKMARK_DESTINATION_UNAVAILABLE_TRAVEL_NEARBY_FIRST" => "The saved destination could not be safely resolved. Reconnect with the latest Companion; if it remains unavailable, revisit the location using the game and save a new bookmark.",
        "ERROR BOOKMARK_LOADING_UNAVAILABLE" => "Reconnect with this Companion build to enable unloaded-area travel on a supported game patch.",
        "ERROR BOOKMARK_STREAMING_SURFACE_ONLY" => "Loading this destination is not supported. Only numbered outdoor campaign regions can be loaded; visit other areas using the game first.",
        "ERROR BOOKMARK_TRAVEL_IN_PROGRESS" => "Travel is already in progress. Wait for the game to finish before using another bookmark.",
        "ERROR BOOKMARK_TRAVEL_UNKNOWN_CHECK_GAME_DO_NOT_RETRY" => BookmarkTravelWorkflow.UnknownOutcome,
        "ERROR BOOKMARK_WORLD_MISMATCH" => "This bookmark belongs to different campaign map data. No return was made.",
        "ERROR BOOKMARK_DIFFICULTY_OR_WORLD_MISMATCH" => "The game has an older bridge loaded. Reconnect with this Companion build to use bookmarks across difficulties and hardcore modes.",
        "ERROR BOOKMARK_CHARACTER_CHANGED_NO_MOVE" => "The loaded character changed after inspection. No move was made. Inspect and confirm again.",
        "ERROR BOOKMARK_LOADED_OUTDOOR_AREA_REQUIRED" or "ERROR BOOKMARK_CAMPAIGN_ONLY" => "Start bookmark travel from a ready outdoor campaign area, not a dungeon, custom game or challenge mode.",
        "ERROR BOOKMARK_PLAYER_NOT_READY_OR_IN_COMBAT" or "ERROR BOOKMARK_NO_READY_PLAYER" => "Load a living single-player character, finish traveling, and leave combat before using bookmarks.",
        "ERROR BOOKMARK_MOVE_UNVERIFIED_CHECK_GAME_DO_NOT_RETRY" or "ERROR BOOKMARK_MOVE_UNKNOWN_CHECK_GAME_DO_NOT_RETRY" or "ERROR GAME_THREAD_TIMEOUT" => BookmarkTravelWorkflow.UnknownOutcome,
        _ => "Bookmark operation stopped: " + response
    };
}
