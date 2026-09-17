using GrimDawnCompanion.Core;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace GrimDawnCompanion.App;

public partial class MainWindow
{
    private BookmarkHotkey? _pendingBookmarkHotkey;
    private DispatcherTimer? _bookmarkHotkeyTimer;
    private readonly Dictionary<BookmarkHotkey, BookmarkHotkeyEdge> _bookmarkKeyEdges = new();
    private readonly Mutex _bookmarkHotkeyOwner = new(false, @"Local\GrimDawnCompanion.BookmarkHotkeys");
    private bool _ownsBookmarkHotkeys;
    private bool _bookmarkPollPrimed;
    private readonly BookmarkActivationGuard _bookmarkReturnGuard = new();
    private bool _bookmarkReadinessBusy;
    private long _bookmarkNextReadinessCheck;

    private async Task PollBookmarkReadinessAsync()
    {
        _bookmarkReadinessBusy=true;
        var started=Environment.TickCount64;
        var process=_bridge.ProcessId;
        try
        {
            var capture=await _bridge.ReadBookmarkLocationAsync(_lifetime.Token);
            if(!BookmarkGameHasFocus() || _bookmarkBusy || _viewModel.IsBusy || process!=_bridge.ProcessId)
                _bookmarkReturnGuard.InvalidateReadiness();
            else _bookmarkReturnGuard.ObserveReady(started,Environment.TickCount64,
                $"{process}:{capture.PlayerId}:{capture.Location.CharacterName}:{capture.Location.World}:{capture.Location.Region}");
        }
        catch { _bookmarkReturnGuard.InvalidateReadiness(); }
        finally { _bookmarkReadinessBusy=false; _bookmarkNextReadinessCheck=Environment.TickCount64+300; }
    }

    private void CaptureBookmarkHotkey(object sender, KeyEventArgs e)
    {
        // Preserve normal keyboard navigation out of the capture control.
        if (e.Key == Key.Tab) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;
        var candidate = new BookmarkHotkey(KeyInterop.VirtualKeyFromKey(key), (int)Keyboard.Modifiers);
        try { candidate.Validate(); _pendingBookmarkHotkey = candidate; BookmarkHotkeyBox.Text = candidate.ToString(); }
        catch (Exception ex) { _pendingBookmarkHotkey = null; BookmarkHotkeyBox.Text = ex.Message; }
    }

    private async void AssignBookmarkHotkey(object sender, RoutedEventArgs e)
    {
        if (_pendingBookmarkHotkey is null) { MessageBox.Show(this, "Enter a key combination first.", "Bookmark hotkey"); return; }
        await SetBookmarkHotkeyAsync(_pendingBookmarkHotkey);
    }
    private async void ClearBookmarkHotkey(object sender, RoutedEventArgs e) => await SetBookmarkHotkeyAsync(null);

    private async Task SetBookmarkHotkeyAsync(BookmarkHotkey? hotkey)
    {
        if (_bookmarkBusy) return;
        if (_viewModel.SelectedBookmark is not { } bookmark) { MessageBox.Show(this, "Select a bookmark first.", "Bookmark hotkey"); return; }
        _bookmarkBusy = true; UpdateBookmarkUi();
        try
        {
            ShowStoredBookmarks(await _bookmarkStore.SetHotkeyAsync(bookmark.Id, hotkey, _lifetime.Token));
            _pendingBookmarkHotkey = null;
            BookmarkHotkeyBox.Text = "Press a key combination here";
            SetStatus(hotkey is null ? "Hotkey removed" : "Hotkey saved", bookmark.Name + (hotkey is null ? " has no assigned hotkey." : $": {hotkey}. Active only while Grim Dawn has focus."), 100);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not update hotkey", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _bookmarkBusy = false; UpdateBookmarkUi(); }
    }

    private async void RefreshBookmarkNames(object sender, RoutedEventArgs e)
    {
        if (_bookmarkBusy) return;
        _bookmarkBusy = true; UpdateBookmarkUi();
        try
        {
            var names = new Dictionary<Guid, string>();
            foreach (var bookmark in _viewModel.PositionBookmarks.ToArray())
            {
                if (bookmark.LocationName is not null || bookmark.MapFingerprint != _navigationCatalog?.SourceFingerprint) continue;
                var name = await _bridge.ReadBookmarkNameAsync(bookmark.Location, _lifetime.Token);
                if (name is not null) names[bookmark.Id] = name;
            }
            if (names.Count != 0) ShowStoredBookmarks(await _bookmarkStore.SetLocationNamesAsync(names, _lifetime.Token));
            SetStatus("Location names refreshed", $"Identified {names.Count} locations. For remaining older bookmarks, travel near the saved area in the matching campaign world and refresh again. No character was moved.", 100);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not identify locations", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _bookmarkBusy = false; UpdateBookmarkUi(); }
    }

    private void StartBookmarkHotkeys()
    {
        if (_bookmarkHotkeyTimer is not null || _shutdownStarted) return;
        _bookmarkHotkeyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _bookmarkHotkeyTimer.Tick += PollBookmarkHotkeys;
        _bookmarkHotkeyTimer.Start();
    }

    private bool BookmarkGameHasFocus()
    {
        if (!_bridge.IsConnected || _bridge.ProcessId is not int process || process <= 0 || _shutdownStarted) return false;
        var window = BookmarkGetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        _ = BookmarkGetWindowThreadProcessId(window, out var owner);
        return owner == (uint)process;
    }
    private static bool BookmarkKeyDown(int key) => (BookmarkGetAsyncKeyState(key) & 0x8000) != 0;

    private void PollBookmarkHotkeys(object? sender, EventArgs e)
    {
        var focused = BookmarkGameHasFocus();
        if (!focused && _ownsBookmarkHotkeys) { _bookmarkHotkeyOwner.ReleaseMutex(); _ownsBookmarkHotkeys = false; }
        if (focused && !_ownsBookmarkHotkeys)
        {
            try { _ownsBookmarkHotkeys = _bookmarkHotkeyOwner.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsBookmarkHotkeys = true; }
        }
        var modifiers = (BookmarkKeyDown(0x12) ? 1 : 0) | (BookmarkKeyDown(0x11) ? 2 : 0) | (BookmarkKeyDown(0x10) ? 4 : 0) |
            (BookmarkKeyDown(0x5B) || BookmarkKeyDown(0x5C) ? 8 : 0);
        var eligible = _bookmarkPollPrimed && focused && _ownsBookmarkHotkeys && !_bookmarkBusy && !_viewModel.IsBusy &&
            _bridge.SupportsPersistentBookmarks && _navigationCatalog is not null;
        if(!eligible) _bookmarkReturnGuard.InvalidateReadiness();
        else if(!_bookmarkReadinessBusy && Environment.TickCount64>=_bookmarkNextReadinessCheck && _viewModel.PositionBookmarks.Any(b=>b.Hotkey is not null))
            _=PollBookmarkReadinessAsync();
        eligible=eligible && _bookmarkReturnGuard.IsReady(Environment.TickCount64);
        PositionBookmark? activate = null;
        foreach (var bookmark in _viewModel.PositionBookmarks)
        {
            if (bookmark.Hotkey is not { } hotkey) continue;
            if (!_bookmarkKeyEdges.TryGetValue(hotkey, out var edge))
            {
                edge = new BookmarkHotkeyEdge(); _bookmarkKeyEdges.Add(hotkey, edge);
                // New/changed bindings never fire while already held.
                edge.Update(BookmarkKeyDown(hotkey.Key), false);
            }
            // Track the primary key even outside the right modifier/focus state.
            // A held key cannot fire after returning focus or changing modifiers.
            if (edge.Update(BookmarkKeyDown(hotkey.Key), eligible && modifiers == hotkey.Modifiers)) activate ??= bookmark;
        }
        _bookmarkPollPrimed = true;
        if (activate is not null) _ = ReturnToPositionBookmarkAsync(activate, fromHotkey: true);
    }

    private void StopBookmarkHotkeys()
    {
        _bookmarkHotkeyTimer?.Stop();
        if (_ownsBookmarkHotkeys) { _bookmarkHotkeyOwner.ReleaseMutex(); _ownsBookmarkHotkeys = false; }
        _bookmarkHotkeyOwner.Dispose();
    }
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr BookmarkGetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint BookmarkGetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")] private static extern short BookmarkGetAsyncKeyState(int key);
}
