using GrimDawnCompanion.Core;
using System.Globalization;

internal static class PositionBookmarkRegression
{
    public static async Task Run(string root, Action<bool, string> require)
    {
        var hotkey = new BookmarkHotkey(0x70, 3);
        hotkey.Validate();
        require(hotkey.ToString() == "Ctrl+Alt+F1" && new BookmarkHotkey(0x41, 7).ToString() == "Ctrl+Alt+Shift+A", "hotkeys have readable stable labels");
        foreach (var invalid in new[] { new BookmarkHotkey(0x70, 0), new BookmarkHotkey(0x73, 1), new BookmarkHotkey(0x1B, 3), new BookmarkHotkey(0x70, 11) })
        {
            bool rejected = false;
            try { invalid.Validate(); } catch (InvalidDataException) { rejected = true; }
            require(rejected, "plain gameplay and system shortcut keys cannot be assigned");
        }
        var edge = new BookmarkHotkeyEdge();
        require(edge.Update(true, true) && !edge.Update(true, true), "hotkey activates once per press, never while held");
        edge.Update(false, true);
        require(!edge.Update(true, false) && !edge.Update(true, true), "presses outside game or while busy cannot activate later while held");
        edge.Update(false, false);
        require(edge.Update(true, true), "releasing and pressing again permits a later intentional return");
        var guard=new BookmarkActivationGuard();
        require(!guard.TryBegin(0,true),"hotkeys start disarmed until the loaded player is verified stable");
        foreach(var time in new long[]{0,400,800,1200}) guard.ObserveReady(time,time,"player-a:region-a");
        require(guard.IsReady(1200) && guard.TryBegin(1200,true) && !guard.TryBegin(1201,false),"one shared gate rejects a second bookmark or button during travel");
        guard.Complete(2000);
        require(!guard.TryBegin(3999,false) && guard.TryBegin(4000,false),"all bookmarks enforce a two-second cooldown after completion");
        guard.Complete(4001);
        require(!guard.TryBegin(7000,true),"cooldown completion alone never arms a hotkey without fresh readiness");
        foreach(var time in new long[]{7000,7400,7800,8200}) guard.ObserveReady(time,time,"player-a:region-a");
        require(guard.IsReady(8200) && !guard.IsReady(9000),"stale readiness expires during loading or a UI stall");
        guard.ObserveReady(9000,9300,"player-a:region-a");
        require(!guard.IsReady(9300),"a delayed game-thread readiness reply cannot arm a hotkey");
        foreach(var time in new long[]{10000,10400,10800,11200}) guard.ObserveReady(time,time,"player-a:region-a");
        guard.ObserveReady(11300,11300,"player-b:region-b");
        require(!guard.IsReady(11300),"character and region transitions restart the readiness window");
        guard.InvalidateReadiness();
        require(!guard.IsReady(11400),"loading errors and focus loss immediately disarm hotkeys");
        var blockedEdge=new BookmarkHotkeyEdge();
        require(!blockedEdge.Update(true,false) && !blockedEdge.Update(true,true),"cooldown and loading presses are discarded, never queued for later");
        blockedEdge.Update(false,true);
        require(blockedEdge.Update(true,true),"a fresh press is required after a blocked hold");
        require(GameBridgeClient.ParseBookmarkName("OK BOOKMARK LABEL " + Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes("Devil's Crossing"))) == "Devil's Crossing" &&
            GameBridgeClient.ParseBookmarkName("OK BOOKMARK LABEL -") is null, "localized area names and unknown labels parse without inventing names");
        var point = new BookmarkLocation("0102030405060708090A0B0C0D0E0F10", "Ana 😀", 0, false,
            "levels/world001.map", "levels/test.lvl", 1.234567f, -2.25f, 45.6f);
        require(BookmarkLocation.ParseWire(point.ToWire()) == point, "persistent bookmark wire preserves Unicode metadata and exact coordinates");
        var shared = point with { CharacterId = new string('0', 32) };
        require(BookmarkCapture.ParseResponse("OK BOOKMARK 2 42 " + shared.ToWire()) == new BookmarkCapture(42, shared),
            "all-zero save ID is accepted with a separate live confirmation identity");
        require(point.MatchesContext(shared with { CharacterName = "Another character" }),
            "shared bookmarks permit a different character including an unset save ID");
        foreach (var invalidResponse in new[] { "OK BOOKMARK 1 " + point.ToWire(), "OK BOOKMARK 2 0 " + point.ToWire(), "OK BOOKMARK 2 bad " + point.ToWire() })
        {
            bool rejected = false;
            try { BookmarkCapture.ParseResponse(invalidResponse); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { rejected = true; }
            require(rejected, "legacy or invalid live confirmation identity cannot authorize shared travel");
        }
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            require(BookmarkLocation.ParseWire(point.ToWire()) == point, "bookmark coordinates are independent of decimal-comma locale");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        for (uint savedDifficulty = 0; savedDifficulty < 3; savedDifficulty++)
        foreach (var savedHardcore in new[] { false, true })
        for (uint currentDifficulty = 0; currentDifficulty < 3; currentDifficulty++)
        foreach (var currentHardcore in new[] { false, true })
            require((point with { Difficulty = savedDifficulty, Hardcore = savedHardcore }).MatchesContext(
                point with { Difficulty = currentDifficulty, Hardcore = currentHardcore }),
                "shared bookmark permits every difficulty/hardcore pairing on compatible world data");
        require(!point.MatchesContext(point with { World = "other/world001.map", Difficulty = 2, Hardcore = true }),
            "cross-mode bookmarks still reject different world data");
        require(point.MatchesContext(point with { X = 200, Region = "levels/neighbor.lvl", CharacterName = "Renamed" }),
            "saved-by metadata and current location do not restrict shared bookmarks");
        foreach (var invalid in new[] { point with { X = float.NaN }, point with { Y = float.PositiveInfinity },
            point with { Z = 1000000 }, point with { Difficulty = 3 }, point with { CharacterId = "invalid" },
            point with { CharacterName = "Bad\nName" }, point with { Region = "../escape" }, point with { World = "bad\tpath" } })
        {
            bool rejected = false;
            try { invalid.ToWire(); } catch (InvalidDataException) { rejected = true; }
            require(rejected, "invalid persistent location cannot enter the game protocol");
        }
        var bridge = new GameBridgeClient();
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(bridge, 26);
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(bridge, new HashSet<string> { "PERSISTENT_BOOKMARKS", "SHARED_BOOKMARKS" });
        require(!bridge.SupportsPersistentBookmarks, "old bridges cannot enable persistent returns");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(bridge, 28);
        require(!bridge.SupportsPersistentBookmarks, "old mode-restricted bridge cannot enable revised bookmark workflow");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(bridge, 29);
        require(!bridge.SupportsPersistentBookmarks, "new bridge still needs explicit cross-mode capability");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(bridge, new HashSet<string> { "PERSISTENT_BOOKMARKS", "SHARED_BOOKMARKS", "CROSS_MODE_BOOKMARKS" });
        require(bridge.SupportsPersistentBookmarks, "protocol 29 plus cross-mode capability enables compatible shared returns");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(bridge, 37);
        require(!bridge.SupportsBookmarkLoading, "loading requires an explicit supported native capability");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(bridge, new HashSet<string> { "PERSISTENT_BOOKMARKS", "SHARED_BOOKMARKS", "CROSS_MODE_BOOKMARKS", "BOOKMARK_LOADING" });
        require(bridge.SupportsBookmarkLoading, "protocol 37 and audited loading capability enable asynchronous travel");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(bridge, 36);
        require(!bridge.SupportsBookmarkLoading, "older bridge never receives the loading command");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(bridge, new HashSet<string> { "PERSISTENT_BOOKMARKS" });
        require(!bridge.SupportsPersistentBookmarks, "unsupported native patch keeps persistent returns unavailable");

        const string travel = "BOOKMARK\tTRAVEL 42 fixture";
        var commands = new List<string>();
        var replies = new Queue<string>(new[] { "OK BOOKMARK LOADING 17", "ERROR GAME_THREAD_TIMEOUT", "OK BOOKMARK LOADING 17", "OK BOOKMARK RETURNED" });
        var progress = new List<string>();
        await BookmarkTravelWorkflow.RunAsync(travel, (command, _) => { commands.Add(command); return Task.FromResult(replies.Dequeue()); },
            progress.Add, pollInterval: TimeSpan.Zero);
        require(commands.SequenceEqual(new[] { travel, "BOOKMARK\tSTATUS 17", "BOOKMARK\tSTATUS 17", "BOOKMARK\tSTATUS 17" }) && progress.Count == 1,
            "loading progress and delayed game-thread replies only repeat status, never the travel request");
        commands.Clear();
        await BookmarkTravelWorkflow.RunAsync(travel, (command, _) => { commands.Add(command); return Task.FromResult("OK BOOKMARK RETURNED"); },
            _ => throw new Exception("unexpected loading"));
        require(commands.Count == 1, "nearby return completes without polling or a loading message");
        foreach (var invalid in new[] { "OK BOOKMARK LOADING 0", "OK BOOKMARK LOADING -1", "OK BOOKMARK LOADING 17 extra", "OK BOOKMARK LOADING  17" })
            require(!BookmarkTravelWorkflow.TryLoadingToken(invalid, out _), "invalid loading tokens fail closed");
        foreach (var failure in new[] { "disconnect", "timeout", "cancel", "mismatched token", "wrong arrival" })
        {
            commands.Clear();
            using var cancellation = new CancellationTokenSource();
            bool unknown = false;
            try
            {
                await BookmarkTravelWorkflow.RunAsync(travel, (command, _) =>
                {
                    commands.Add(command);
                    if (commands.Count == 1) return Task.FromResult("OK BOOKMARK LOADING 17");
                    if (failure == "disconnect") throw new IOException("disconnected");
                    return Task.FromResult(failure == "mismatched token" ? "OK BOOKMARK LOADING 18" : "ERROR BOOKMARK_TRAVEL_UNKNOWN_CHECK_GAME_DO_NOT_RETRY");
                }, _ => { if (failure == "cancel") cancellation.Cancel(); }, cancellation.Token,
                    pollInterval: failure == "timeout" ? TimeSpan.FromSeconds(1) : TimeSpan.Zero,
                    verificationTimeout: failure == "timeout" ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromSeconds(1));
            }
            catch (InvalidOperationException ex) { unknown = ex.Message == BookmarkTravelWorkflow.UnknownOutcome; }
            require(unknown && commands.Count(c => c == travel) == 1 && commands.Skip(1).All(c => c == "BOOKMARK\tSTATUS 17"),
                $"{failure} after travel warns the game may still finish and never resubmits movement");
        }
        commands.Clear();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await BookmarkTravelWorkflow.RunAsync(travel, (command, _) => { commands.Add(command); return Task.FromResult(""); }, ct: cancelled.Token); }
            catch (OperationCanceledException) { }
            require(commands.Count == 0, "cancellation before submission starts no travel");
        }
        bool rejectedBeforeTravel = false;
        commands.Clear();
        try
        {
            await BookmarkTravelWorkflow.RunAsync(travel, (command, _) => { commands.Add(command); return Task.FromResult("ERROR BOOKMARK_STREAMING_SURFACE_ONLY"); });
        }
        catch (InvalidOperationException ex) { rejectedBeforeTravel = ex.Message.Contains("Only numbered outdoor campaign regions"); }
        require(rejectedBeforeTravel && commands.Count == 1, "preflight refusal is reported without polling or retrying");

        // All file tests run in an isolated temporary directory, not the user's store.
        var directory = Directory.CreateTempSubdirectory("gdc-bookmarks-").FullName;
        try
        {
            var path = Path.Combine(directory, "bookmarks.json");
            var store = new PositionBookmarkStore(path);
            require((await store.LoadAsync()).Count == 0, "new bookmark store starts empty");
            var first = new PositionBookmark(Guid.NewGuid(), "Home", DateTimeOffset.UtcNow, point, new string('A', 64));
            await store.AddAsync(first);
            require((await new PositionBookmarkStore(path).LoadAsync()).Single() == first, "bookmark survives closing and reopening its store");
            var second = first with { Id = Guid.NewGuid(), Name = "Second", Location = shared };
            await new PositionBookmarkStore(path).AddAsync(second);
            await store.SetHotkeyAsync(first.Id, hotkey);
            require((await new PositionBookmarkStore(path).LoadAsync()).First(b => b.Id == first.Id).Hotkey == hotkey, "hotkeys persist across reopening");
            var beforeDuplicate = await File.ReadAllTextAsync(path);
            bool duplicate = false;
            try { await new PositionBookmarkStore(path).SetHotkeyAsync(second.Id, hotkey); } catch (InvalidDataException) { duplicate = true; }
            require(duplicate && await File.ReadAllTextAsync(path) == beforeDuplicate, "duplicate shortcut is rejected under the store lock without changing disk");
            await store.SetHotkeyAsync(first.Id, null);
            require((await new PositionBookmarkStore(path).LoadAsync()).First(b => b.Id == first.Id).Hotkey is null, "removed assignment stays removed after restart");
            await store.SetHotkeyAsync(second.Id, hotkey);
            await store.SetLocationNamesAsync(new Dictionary<Guid, string> { [second.Id] = "Devil's Crossing" });
            second = second with { Hotkey = hotkey, LocationName = "Devil's Crossing" };
            var remaining = await store.DeleteAsync(first.Id);
            require(remaining.Count == 1 && remaining[0] == second && (await new PositionBookmarkStore(path).LoadAsync()).Single() == second,
                "delete persists and stale store instances preserve another instance's bookmarks");
            require(File.Exists(path + ".bak"), "atomic bookmark updates retain the preceding file as backup");
            using (var fileLock = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                bool locked = false;
                try { await store.AddAsync(first); } catch (IOException) { locked = true; }
                require(locked, "concurrent writers fail safely without overwriting the store");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel(); bool cancelled = false;
                try { await store.AddAsync(first, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
                require(cancelled && (await store.LoadAsync()).Count == 1, "cancelled writes leave the previous bookmark file intact");
            }
            var validJson = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, "{broken");
            bool refused = false;
            try { await store.AddAsync(first); } catch (System.Text.Json.JsonException) { refused = true; }
            require(refused && await File.ReadAllTextAsync(path) == "{broken", "corrupt storage is reported and never overwritten by an empty list");
            await File.WriteAllTextAsync(path, validJson.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99"));
            bool unsupported = false;
            try { await store.LoadAsync(); } catch (InvalidDataException) { unsupported = true; }
            require(unsupported, "unknown bookmark schema is rejected rather than silently migrated");
        }
        finally { Directory.Delete(directory, true); }
        var app = File.ReadAllText(Path.Combine(root, "src", "GrimDawnCompanion.App", "MainWindow.xaml.cs"));
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src", "GrimDawnCompanion.App", "MainWindow.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var sharedCard = xaml.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "SharedBookmarksCard");
        var hotkeyCard = xaml.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "BookmarkHotkeysCard");
        require(sharedCard.Parent == hotkeyCard.Parent && (string?)hotkeyCard.Attribute("Grid.Column") == "2" &&
            (string?)hotkeyCard.Attribute("Grid.Row") == "1" && sharedCard.Attribute("Grid.Row") is null,
            "hotkey section is a separate card below Shared Bookmarks in the right column");
        require(new[] { "BookmarkHotkeyBox", "AssignBookmarkHotkeyButton", "ClearBookmarkHotkeyButton" }.All(name =>
            hotkeyCard.Descendants().Any(e => (string?)e.Attribute(x + "Name") == name)) &&
            sharedCard.Descendants().Any(e => (string?)e.Attribute(x + "Name") == "RefreshBookmarkNamesButton"),
            "hotkey controls move together while location refresh stays with the bookmark list");
        var client = File.ReadAllText(Path.Combine(root, "src", "GrimDawnCompanion.Core", "GameBridgeClient.cs"));
        require(!client.Contains("GDC_PositionBookmarks") && app.Contains("bookmark.MapFingerprint != _navigationCatalog.SourceFingerprint") &&
            app.Contains("ReturnToBookmarkAsync(bookmark.Location, current.PlayerId") &&
            !app.Contains("\"Return to bookmarked position\", MessageBoxButton.YesNo") && app.Contains("fromHotkey && !BookmarkGameHasFocus()"),
            "button and hotkeys share guarded return without a confirmation and hotkeys recheck focus after inspection");
        var hotkeyApp = File.ReadAllText(Path.Combine(root, "src", "GrimDawnCompanion.App", "MainWindow.Bookmarks.cs"));
        require(hotkeyApp.Contains("PollBookmarkReadinessAsync") && hotkeyApp.Contains("_bookmarkReturnGuard.IsReady") &&
            app.Contains("_bookmarkReturnGuard.TryBegin") && app.Contains("_bookmarkReturnGuard.Complete") && app.Contains("BookmarkActivationGuard.MaxReadMs"),
            "UI wiring uses the tested readiness, shared cooldown and delayed-inspection guards");
        require(hotkeyApp.Contains("ReturnToPositionBookmarkAsync(activate, fromHotkey: true)") &&
            hotkeyApp.Contains("!_bookmarkBusy") && hotkeyApp.Contains("BookmarkGetWindowThreadProcessId") && app.Contains("StopBookmarkHotkeys();"),
            "hotkeys use foreground process ownership, busy guard, shared return path and shutdown cleanup");
    }
}
