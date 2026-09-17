using GrimDawnCompanion.Core;
using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GrimDawnCompanion.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly CatalogService _catalog = new();
    private readonly CompatibilityService _compatibility = new();
    private readonly QuestTokenCatalogService _tokenCatalog = new();
    private readonly WorldMarkerCatalogService _worldMarkerCatalog = new();
    private readonly GameBridgeClient _bridge = new();
    private readonly PositionBookmarkStore _bookmarkStore = new();
    private bool _bookmarkBusy;
    private string? _bookmarkProgressText;
    private readonly CancellationTokenSource _lifetime = new();
    private GameInstallation? _game;
    private CatalogDocument? _document;
    private WorldMarkerCatalog? _navigationCatalog;
    private NavigationOverlayWindow? _navigationOverlay;
    private DispatcherTimer? _navigationTimer;
    private IReadOnlyList<string> _activeQuestPaths = [];
    private QuestNavigationState _questNavigationState = new();
    private bool _questStageDataReady;
    private ulong _nativeRadarContext;
    private bool _nativeRadarSubmitted;
    private IReadOnlyList<LiveMapMarkerRecord> _liveMapMarkers = [];
    private IReadOnlyList<LiveMapMarkerRecord> _regionalQuestMarkers = [];
    private string? _currentLevelPath;
    private MinimapGeometry? _minimapGeometry;
    private readonly RegionalQuestMarkerCache _questMarkerCache = new();
    private AppSettings _settings = new();
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _settingsLoaded;
    private bool _navigationBusy;
    private bool _navigationPollBusy;
    private bool _refreshDataBusy;
    private int _navigationPollCount;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) =>
        {
            await InitializeAsync();
            var arguments = Environment.GetCommandLineArgs();
            if (arguments.Contains("--preview-dropdown", StringComparer.OrdinalIgnoreCase))
                SourceFilter.IsDropDownOpen = true;
            if (arguments.Contains("--smoke-test-window", StringComparer.OrdinalIgnoreCase))
            {
                UpdateLayout();
                Directory.CreateDirectory(CompanionDataPaths.DirectoryPath);
                var passed = PrimaryButtonLabelUsesAccentForeground() && _document is { Items.Count: > 0 }
                    && _navigationCatalog is not null && _bridge.ProcessId is null
                    && File.Exists(_catalog.CachePath);
                await File.WriteAllTextAsync(Path.Combine(CompanionDataPaths.DirectoryPath, "startup-result.json"),
                    JsonSerializer.Serialize(new { Passed = passed, Items = _document?.Items.Count,
                        NavigationMarkers = _navigationCatalog?.Markers.Count, Cache = _catalog.CachePath,
                        Connected = _bridge.ProcessId is not null, Status = _viewModel.StatusTitle, Detail = _viewModel.StatusDetail }));
                if (!passed)
                {
                    System.Windows.Application.Current.Shutdown(2);
                    return;
                }
                Close();
            }
        };
        SourceInitialized += (_, _) => ApplyNativeWindowAppearance();
        Loaded += (_, _) => StartBookmarkHotkeys();
        Closing += CloseCompanion;
    }

    private bool PrimaryButtonLabelUsesAccentForeground()
    {
        ConnectButton.ApplyTemplate();
        var label = FindVisualChild<TextBlock>(ConnectButton);
        return label?.Foreground is SolidColorBrush actual
            && FindResource("AccentForeground") is SolidColorBrush expected
            && actual.Color == expected.Color;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private void ApplyNativeWindowAppearance()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        var roundedCorners = 2;
        _ = DwmSetWindowAttribute(handle, 33, ref roundedCorners, sizeof(int));
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private void WindowStateChanged(object sender, EventArgs e)
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = Geometry.Parse(isMaximized ? "M 3,1 L 11,1 11,9 9,9 M 9,3 L 1,3 1,11 9,11 Z" : "M 1,1 L 11,1 11,11 1,11 Z");
        MaximizeButton.ToolTip = isMaximized ? "Restore Down" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, isMaximized ? "Restore Down" : "Maximize");
        AutomationProperties.SetHelpText(MaximizeButton, isMaximized ? "Restore Window" : "Maximize Window");
    }

    private async Task InitializeAsync()
    {
        _viewModel.IsBusy = true;
        try
        {
            _settingsLoaded = false;
            _settings = await AppSettings.LoadAsync();
            try { ShowStoredBookmarks(await _bookmarkStore.LoadAsync(_lifetime.Token)); }
            catch (Exception ex) { MessageBox.Show(this, "Bookmarks could not be loaded. The existing file has not been changed.\n" + ex.Message, "Bookmark storage", MessageBoxButton.OK, MessageBoxImage.Warning); }
            AutoUnbridgeBox.IsChecked = _settings.AutoUnbridgeOnExit;
            NavigationQuestBox.IsChecked = _settings.ShowActiveQuestMarkers;
            NavigationPoiBox.IsChecked = _settings.ShowPointOfInterestMarkers;
            foreach (var box in PoiTypesPanel.Children.OfType<CheckBox>())
                box.IsChecked = (_settings.EnabledPoiTypes & Enum.Parse<PoiFilter>((string)box.Tag)) != 0;
            NavigationAutomaticBox.IsChecked = _settings.AutomaticNavigationAlignment;
            NavigationManualPanel.IsEnabled = !_settings.AutomaticNavigationAlignment;
            NavigationRangeSlider.IsEnabled = !_settings.AutomaticNavigationAlignment;
            NavigationHorizontalOffsetSlider.Value = _settings.NavigationOverlayOffsetX;
            NavigationVerticalOffsetSlider.Value = _settings.NavigationOverlayOffsetY;
            NavigationScaleSlider.Value = _settings.NavigationOverlayScale;
            NavigationRangeSlider.Value = _settings.NavigationOverlayRange;
            UpdateNavigationAlignmentLabels();
            _settingsLoaded = true;
            _game = GameLocator.Locate(_settings.GameDirectory);
            if (_game is null)
            {
                SetStatus("Game not found", "Choose the Grim Dawn installation folder in Settings.", 0);
                ShowPage(SettingsPage, "Settings", "Choose the installed game directory and manage local data.", SettingsNav);
                return;
            }
            _viewModel.GameDirectory = _game.RootDirectory;
            var tokenTask = _tokenCatalog.BuildAsync(_game, _lifetime.Token);
            _document = await _catalog.LoadAsync(cancellationToken: _lifetime.Token);
            if (_document is not null)
            {
                _viewModel.SetCatalog(_document);
                SetStatus("Catalog loaded", $"{_document.Items.Count:N0} items available while updates are checked.", 0);
            }
            await VerifyCompatibilityAsync();
            var installedFingerprint = await CatalogService.ComputeSourceFingerprintAsync(_game, _lifetime.Token);
            if (_document is null || !string.Equals(_document.SourceFingerprint, installedFingerprint, StringComparison.OrdinalIgnoreCase))
                await RebuildCatalogAsync(automatic: true);
            else
                SetStatus("Ready", $"Catalog verified against the installed game • {_document.Items.Count:N0} items", 100);
            _viewModel.SetQuestTokens(await tokenTask);
            try
            {
                _navigationCatalog = await _worldMarkerCatalog.BuildAsync(
                    _game, _document?.QuestRecordAssociations, _lifetime.Token, _document?.QuestNavigationRecords);
            }
            catch (Exception ex)
            {
                _navigationCatalog = null;
                SetStatus("Navigation catalog unavailable", ex.Message, 0);
            }
            UpdateNavigationUi();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("Attention required", ex.Message, 0); MessageBox.Show(ex.Message, "Startup check", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _viewModel.IsBusy = false; }
    }

    private async Task VerifyCompatibilityAsync()
    {
        if (_game is null) return;
        SetStatus("Checking compatibility", "Resolving exported game symbols and refreshing fingerprints…", 12);
        var report = await _compatibility.VerifyAndUpdateAsync(_game, _lifetime.Token);
        _viewModel.SetCompatibility(report);
        if (report.Level == VerificationLevel.Failed)
            SetStatus("Bridge disabled", "A required game export is missing. Compatibility failed closed.", 0);
    }

    private async Task RebuildCatalogAsync(bool automatic)
    {
        if (_game is null) return;
        var wasBusy = _viewModel.IsBusy;
        _viewModel.IsBusy = true;
        try
        {
            var progress = new Progress<CatalogProgress>(value => SetStatus(value.Stage, value.Detail, value.Percent));
            _document = await _catalog.RebuildAsync(_game, progress: progress, cancellationToken: _lifetime.Token);
            _viewModel.SetCatalog(_document);
            SetStatus("Catalog ready", $"Verified {_document.Items.Count:N0} records across {_document.Sources.Count} installed content archives.", 100);
        }
        catch (Exception ex) when (automatic && _document is not null)
        {
            SetStatus("Using cached catalog", "Automatic update failed: " + ex.Message, 0);
        }
        finally { _viewModel.IsBusy = wasBusy; }
    }

    private async void RefreshData(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy || _refreshDataBusy) return;
        if (_game is null) { MessageBox.Show(this, "Choose a valid Grim Dawn installation first.", "Force Refresh Data", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (MessageBox.Show(this,
            "Rebuild the item catalogue and refresh compatibility, quest and navigation data from the installed game files?\n\nThis may take some time. No game archives or character saves are changed.\n\nContinue?",
            "Force Refresh Data", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _refreshDataBusy = true;
        _viewModel.IsBusy = true;
        ForceRefreshDataButton.IsEnabled = false;
        try
        {
            await VerifyCompatibilityAsync();
            await RebuildCatalogAsync(automatic: false);
            if (_game is not null)
            {
                SetStatus("Refreshing companion data", "Updating quest and navigation indexes…", 95);
                var tokens = _tokenCatalog.BuildAsync(_game, _lifetime.Token);
                var navigation = _worldMarkerCatalog.BuildAsync(_game, _document?.QuestRecordAssociations, _lifetime.Token, _document?.QuestNavigationRecords);
                _viewModel.SetQuestTokens(await tokens);
                _navigationCatalog = await navigation;
                UpdateNavigationUi();
            }
            SetStatus("Data refreshed", "Item catalog, compatibility, quest, and navigation data are up to date.", 100);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Refresh failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            _refreshDataBusy = false;
            _viewModel.IsBusy = false;
            ForceRefreshDataButton.IsEnabled = true;
        }
    }

    private async void ConnectGame(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsConnected)
        {
            await DisconnectGameAsync();
            return;
        }
        if (_game is null) { MessageBox.Show("Choose a valid Grim Dawn installation first."); return; }
        if (_viewModel.Compatibility?.CanConnect != true) { MessageBox.Show("Compatibility verification has not passed. Open Compatibility for details."); return; }
        try
        {
            ConnectButton.IsEnabled = false;
            SetStatus("Connecting", "Loading the transparent x64 bridge into the running local game…", 50);
            var bridgePath = Path.Combine(AppContext.BaseDirectory, "GrimDawnBridge.dll");
            ResourceCapBypassBox.IsChecked=false;
            var handshake = await _bridge.ConnectAsync(_game, bridgePath, _lifetime.Token);
            _viewModel.IsConnected = true;
            StartGameplayAssistUi();
            _viewModel.NotifyConnectionChanged();
            _viewModel.NavigationAvailable = _bridge.SupportsNavigationOverlay && _navigationCatalog is not null;
            _viewModel.NavigationActive = false;
            ConnectButton.Content = "Disconnect";
            ConnectButton.Background = (Brush)FindResource("Danger");
            ConnectButton.Foreground = Brushes.White;
            UpdateNavigationUi();
            SetStatus("Bridge active", $"Game process {_bridge.ProcessId} • {handshake}", 100);
            if (_bridge.SupportsUtilities)
            {
                try { _viewModel.Resources = await _bridge.GetResourcesAsync(_lifetime.Token); await RefreshResourceLimitsAsync(); }
                catch { }
            }
        }
        catch (Exception ex) { SetStatus("Connection failed", ex.Message, 0); MessageBox.Show(ex.Message, "Could not connect", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { ConnectButton.IsEnabled = true; }
    }

    private async Task DisconnectGameAsync()
    {
        SuspendGameplayAssistUi();
        try
        {
            ConnectButton.IsEnabled = false;
            var gameWasRunning = _bridge.IsGameProcessRunning;
            SetStatus("Disconnecting", gameWasRunning ? "Removing the game-thread hook and unloading the bridge…" : "Clearing the closed game connection…", 60);
            await DisableNavigationOverlayAsync(clearBridgeState: gameWasRunning);
            if (!await _bridge.UnloadAsync(_lifetime.Token))
                throw new InvalidOperationException("The running game did not confirm that the bridge unloaded. Close Grim Dawn before reconnecting.");
            SetAssistIndicator(false);
            _viewModel.IsConnected = false;
            ResourceCapBypassBox.IsChecked=false;
            _viewModel.Resources = null;
            CharacterLevelCapText.Text = "Connect and refresh to read the active level cap.";
            MoneyLimitText.Text = SkillLimitText.Text = AttributeLimitText.Text = DevotionLimitText.Text = "Refresh to read the current limit.";
            _viewModel.NavigationAvailable = false;
            _viewModel.NavigationActive = false;
            _viewModel.NotifyConnectionChanged();
            ConnectButton.Content = "Connect to Game";
            ConnectButton.Background = (Brush)FindResource("Accent");
            ConnectButton.Foreground = (Brush)FindResource("AccentForeground");
            UpdateNavigationUi();
            SetStatus(gameWasRunning ? "Bridge disabled" : "Connection cleared",
                gameWasRunning ? "The hook was removed and the bridge unloaded. Grim Dawn can remain open." : "Grim Dawn had already closed; the stale local connection was cleared.", 100);
        }
        catch (Exception ex)
        {
            SetStatus("Disconnect failed", ex.Message, 0);
            MessageBox.Show(ex.Message, "Could not disconnect", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { ConnectButton.IsEnabled = true; }
    }

    private async void SpawnItem(object sender, RoutedEventArgs e)
    {
        if (_itemSpawnBusy) return;
        if (_viewModel.SelectedItem is null) { MessageBox.Show("Select an item first."); return; }
        if (!int.TryParse(QuantityBox.Text, out var quantity) || quantity is < 1 or > 1000)
        {
            MessageBox.Show("Quantity must be a whole number from 1 to 1000."); return;
        }
        _itemSpawnBusy=true;
        if(sender is Button spawnButton) spawnButton.IsEnabled=false;
        try
        {
            if(_viewModel.SelectedPrefix.RecordPath.Length>0 || _viewModel.SelectedSuffix.RecordPath.Length>0)
            {
                await _bridge.SpawnAffixedItemAsync(_viewModel.SelectedItem,_viewModel.SelectedPrefix,_viewModel.SelectedSuffix,quantity,_viewModel.AffixIndex,_lifetime.Token);
                SetStatus("Affixed items created",$"{quantity} × {_viewModel.SelectedItem.Name}. Check inventory or nearby ground if full.",100);
                return;
            }
            var result = await _bridge.SpawnItemAsync(_viewModel.SelectedItem, quantity, CompleteComponentBox.IsChecked == true, _lifetime.Token);
            if (!result.StartsWith("QUEUED", StringComparison.Ordinal)) throw new InvalidOperationException(result);
            SetStatus("Item queued", $"{quantity} × {_viewModel.SelectedItem.Name}", 100);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Item spawning not completed", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _itemSpawnBusy=false;if(sender is Button button) button.IsEnabled=true; }
    }
    private bool _itemSpawnBusy;

    private async void RefreshCharacterResources(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.Resources = await _bridge.GetResourcesAsync(_lifetime.Token);
            await RefreshResourceLimitsAsync();
            SetStatus("Resources refreshed", "Current values read from the loaded local character.", 100);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Resource refresh failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private bool _characterResourceBusy;
    private async void ResourceCapBypassChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || !_bridge.IsConnected) return;
        try { await RefreshResourceLimitsAsync(); }
        catch (Exception ex) { SetStatus("Resource limits unavailable", ex.Message, 0); }
    }
    private async Task<CharacterResourceLimits> RefreshResourceLimitsAsync()
    {
        var limits=await _bridge.GetResourceLimitsAsync(_lifetime.Token);
        var bypass=ResourceCapBypassBox.IsChecked==true;
          CharacterLevelCapText.Text=$"Current level: {limits.Level} • Maximum: {limits.LevelCap} • {(limits.UsesModRules ? "Active mod progression" : "Standard game/DLC progression")}";
        MoneyLimitText.Text=$"Maximum: {limits.Caps[0]:N0}";
        string pointLimit(int kind) => $"Maximum unspent: {limits.Available(kind,bypass):N0} • Allocated: {limits.Spent[kind]:N0} • Natural cap: {limits.Caps[kind]:N0}"+(bypass ? $" • Bypass total: {limits.EffectiveCap(kind,true):N0}" : "");
        SkillLimitText.Text=pointLimit(1);
        AttributeLimitText.Text=pointLimit(2);
        DevotionLimitText.Text=pointLimit(3);
          if(!limits.DevotionAccountingValid) DevotionLimitText.Text += $"\nChanges blocked: allocated + unspent = {(ulong)limits.Spent[3]+limits.Unspent[3]}, earned = {limits.DevotionEarned}. Open Skills/Devotion and refresh.";
        return limits;
    }
    private async void SetCharacterResource(object sender, RoutedEventArgs e)
    {
        if (_characterResourceBusy || _characterLevelBusy || _viewModel.IsBusy) return;
        if (sender is not Button button || button.Tag is not string resource) return;
        var box = resource switch { "money" => MoneyAmountBox, "skill" => SkillAmountBox, "attribute" => AttributeAmountBox, "devotion" => DevotionAmountBox, _ => null };
        if (box is null || !uint.TryParse(box.Text, out var amount))
        {
            MessageBox.Show("Enter a non-negative whole number for the desired balance.", "Invalid amount", MessageBoxButton.OK, MessageBoxImage.Information); return;
        }
        _characterResourceBusy=true;button.IsEnabled=false;ResourceCapBypassBox.IsEnabled=false;
        try
        {
            await RefreshResourceLimitsAsync();
            var bypass=ResourceCapBypassBox.IsChecked==true && resource!="money";
            var prepared=await _bridge.PrepareResourceAsync(resource,amount,_lifetime.Token,bypass);
            var label=resource=="money" ? "money" : $"unspent {resource} points";
            if(MessageBox.Show($"Set {prepared.Name}'s {label} from {prepared.Current:N0} to {prepared.Target:N0}?\n\n"+
                $"Maximum available: {prepared.Maximum:N0}\n"+
                (resource=="money" ? "\n" : $"Already allocated: {prepared.Spent:N0}\nPermitted total: {prepared.Cap:N0}\n\n"+
                    "Allocated skills, attributes and devotions will not be removed.\n\n"+
                    "Quests and future rewards are unchanged. Further rewards may take a character whose points are already filled beyond its normal total.\n\n")+
                (bypass ? "Natural Cap Bypass is enabled. Attempted safety caps are in place, but save and mod compatibility above normal limits is not guaranteed.\n\n" : "")+
                "Back up the character first; Cloud saving is not a backup.",
                "Confirm Resource Balance",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes) return;
            _viewModel.Resources=await _bridge.CommitResourceAsync(prepared,_lifetime.Token);
            await RefreshResourceLimitsAsync();
            SetStatus("Character updated",$"Set {label} to {amount:N0}.",100);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Resource update not completed", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _characterResourceBusy=false;button.IsEnabled=true;ResourceCapBypassBox.IsEnabled=true; }
    }

    private async void SpawnBlueprint(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedBlueprint is null) { MessageBox.Show("Select a blueprint first."); return; }
        await SpawnUtilityItemAsync(_viewModel.SelectedBlueprint, "Blueprint spawned", "Use the blueprint from inventory to learn its formula.");
    }

    private async Task SpawnUtilityItemAsync(ItemRecord item, string title, string detail)
    {
        try
        {
            var response = await _bridge.SpawnItemAsync(item, 1, false, _lifetime.Token);
            if (!response.StartsWith("QUEUED", StringComparison.Ordinal)) throw new InvalidOperationException(response);
            SetStatus(title, $"{item.Name} • {detail}", 100);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Item delivery not completed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void CheckQuestToken(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedToken is null) { MessageBox.Show("Select a token first."); return; }
        try
        {
            var present = await _bridge.HasTokenAsync(_viewModel.SelectedToken.Name, _lifetime.Token);
            _viewModel.TokenState = present ? "Present" : "Absent";
            SetStatus("Token inspected", $"{_viewModel.SelectedToken.Name} is {_viewModel.TokenState.ToLowerInvariant()}.", 100);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Token inspection failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void SetQuestToken(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedToken is null || sender is not Button button || button.Tag is not string action) return;
        var present = action == "grant";
        var verb = present ? "grant" : "remove";
        if (MessageBox.Show($"{char.ToUpperInvariant(verb[0]) + verb[1..]} quest token “{_viewModel.SelectedToken.Name}”?\n\nThis changes quest progression and can affect related quests. Back up the character first, and continue only if you understand this token's effect.",
            "Confirm Quest Token Change", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var state = await _bridge.SetTokenAsync(_viewModel.SelectedToken.Name, present, _lifetime.Token);
            _viewModel.TokenState = state ? "Present" : "Absent";
            SetStatus("Token repaired", $"{_viewModel.SelectedToken.Name} is now {_viewModel.TokenState.ToLowerInvariant()}.", 100);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Quest token change not completed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void SavePositionBookmark(object sender, RoutedEventArgs e)
    {
        if (_bookmarkBusy) return;
        var name = PositionNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = $"Bookmark {_viewModel.PositionBookmarks.Count + 1}";
        if (name.Length > 60) { MessageBox.Show("Bookmark names may be up to 60 characters."); return; }
        _bookmarkBusy = true;
        UpdateBookmarkUi();
        try
        {
            if (_navigationCatalog is null) throw new InvalidOperationException("Wait for the installed map data to finish loading.");
            var location = (await _bridge.ReadBookmarkLocationAsync(_lifetime.Token)).Location;
            var areaName = await _bridge.ReadBookmarkNameAsync(location, _lifetime.Token);
            var bookmark = new PositionBookmark(Guid.NewGuid(), name, DateTimeOffset.Now, location, _navigationCatalog.SourceFingerprint) { LocationName = areaName };
            ShowStoredBookmarks(await _bookmarkStore.AddAsync(bookmark, _lifetime.Token));
            _viewModel.SelectedBookmark = bookmark;
            PositionNameBox.Clear();
            SetStatus("Position saved", $"{name} is saved locally and will remain available after restarting Companion or Grim Dawn.", 100);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not save bookmark", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _bookmarkBusy = false; UpdateBookmarkUi(); }
    }

    private async void RestorePositionBookmark(object sender, RoutedEventArgs e)
    {
        if (_bookmarkBusy) return;
        if (_viewModel.SelectedBookmark is not { } bookmark) { MessageBox.Show("Select a bookmark first."); return; }
        await ReturnToPositionBookmarkAsync(bookmark, fromHotkey: false);
    }

    private async Task ReturnToPositionBookmarkAsync(PositionBookmark bookmark, bool fromHotkey)
    {
        if (_bookmarkBusy || _shutdownStarted || (fromHotkey && !BookmarkGameHasFocus())) return;
        if(!_bookmarkReturnGuard.TryBegin(Environment.TickCount64,fromHotkey)) return;
        _bookmarkBusy = true;
        UpdateBookmarkUi();
        try
        {
            if (_navigationCatalog is null || bookmark.MapFingerprint != _navigationCatalog.SourceFingerprint)
                throw new InvalidOperationException("The installed map data changed or is unavailable. This bookmark has been kept, but returning is blocked. Revisit the location and save a new bookmark.");
            var process = _bridge.ProcessId;
            var inspectionStarted=Environment.TickCount64;
            var current = await _bridge.ReadBookmarkLocationAsync(_lifetime.Token);
            if(fromHotkey && Environment.TickCount64-inspectionStarted>BookmarkActivationGuard.MaxReadMs) return;
            if (!bookmark.Location.MatchesContext(current.Location))
                throw new InvalidOperationException("This bookmark belongs to different campaign map data. Any character, difficulty or hardcore mode can use it when the map data is compatible.");
            if (!_bridge.IsConnected || _bridge.ProcessId != process) throw new InvalidOperationException("The connection changed. No return was submitted.");
            if (fromHotkey && !BookmarkGameHasFocus()) return;
            await _bridge.ReturnToBookmarkAsync(bookmark.Location, current.PlayerId, _lifetime.Token, message =>
            {
                _bookmarkProgressText = BookmarkAvailabilityText.Text = message;
                SetStatus("Loading destination", message, 0);
            });
            SetStatus("Position restored", $"Verified return to {bookmark.Name}.", 100);
        }
        catch (Exception ex)
        {
            if (fromHotkey) SetStatus("Bookmark return stopped", ex.Message, 0);
            else MessageBox.Show(ex.Message, "Could not return to bookmark", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _bookmarkReturnGuard.Complete(Environment.TickCount64); _bookmarkProgressText = null; _bookmarkBusy = false; UpdateBookmarkUi(); }
    }

    private async void DeletePositionBookmark(object sender, RoutedEventArgs e)
    {
        if (_bookmarkBusy || _viewModel.SelectedBookmark is not { } bookmark) return;
        _bookmarkBusy = true; UpdateBookmarkUi();
        try { ShowStoredBookmarks(await _bookmarkStore.DeleteAsync(bookmark.Id, _lifetime.Token)); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not delete bookmark", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _bookmarkBusy = false; UpdateBookmarkUi(); }
    }

    private void ShowStoredBookmarks(IReadOnlyList<PositionBookmark> bookmarks)
    {
        var selected = _viewModel.SelectedBookmark?.Id;
        _viewModel.PositionBookmarks.Clear();
        foreach (var bookmark in bookmarks) _viewModel.PositionBookmarks.Add(bookmark);
        _viewModel.SelectedBookmark = bookmarks.FirstOrDefault(b => b.Id == selected) ?? bookmarks.FirstOrDefault();
    }

    private void UpdateBookmarkUi()
    {
        var available = !_bookmarkBusy && _bridge.IsConnected && _bridge.SupportsPersistentBookmarks && _navigationCatalog is not null;
        SaveBookmarkButton.IsEnabled = available;
        ReturnBookmarkButton.IsEnabled = available;
        DeleteBookmarkButton.IsEnabled = !_bookmarkBusy;
        AssignBookmarkHotkeyButton.IsEnabled = !_bookmarkBusy;
        ClearBookmarkHotkeyButton.IsEnabled = !_bookmarkBusy;
        BookmarkHotkeyBox.IsEnabled = !_bookmarkBusy;
        RefreshBookmarkNamesButton.IsEnabled = available && _bridge.SupportsBookmarkLabels;
        BookmarkAvailabilityText.Text = _bookmarkBusy ? _bookmarkProgressText ?? "Checking bookmark…" :
            !_bridge.IsConnected ? "Connect to Grim Dawn to save or return. Saved bookmarks can be viewed and deleted offline." :
            !_bridge.SupportsPersistentBookmarks ? "Reconnect with this build. Persistent bookmarks require a supported game patch." :
            _navigationCatalog is null ? "Waiting for verified installed map data." :
            !_bridge.SupportsBookmarkLoading ? "Ready for loaded areas. Reconnect with this build to enable destination loading." :
            "Ready. Unloaded surface destinations use game-managed travel. Keep a character backup.";
    }

    private async void ToggleNavigationMarkers(object sender, RoutedEventArgs e)
    {
        if (_navigationBusy) return;
        if (!_bridge.IsConnected || !_bridge.SupportsNavigationOverlay || _navigationCatalog is null)
        {
            MessageBox.Show("Connect with the current bridge and wait for the navigation catalog to finish loading. If Grim Dawn still has an older bridge loaded, restart the game once.",
                "Navigation unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var quests = NavigationQuestBox.IsChecked == true;
        var points = NavigationPoiBox.IsChecked == true;
        if (!_viewModel.NavigationActive && !quests && !points)
        {
            MessageBox.Show("Select Active Quest Objectives, Points Of Interest, or both.", "Choose marker types", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _navigationBusy = true;
            UpdateNavigationUi();
            if (_viewModel.NavigationActive)
            {
                await DisableNavigationOverlayAsync(clearBridgeState: true);
            }
            else
            {
                await EnableNavigationBridgeAsync(quests, points);
                _viewModel.NavigationActive = true;
                StartNavigationOverlay();
            }
        }
        catch (Exception ex)
        {
            await DisableNavigationOverlayAsync(clearBridgeState: false);
            MessageBox.Show(ex.Message, "Could not update navigation markers", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _navigationBusy = false;
            UpdateNavigationUi();
        }
    }

    private async void NavigationFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded) return;
        _settings.ShowActiveQuestMarkers = NavigationQuestBox.IsChecked == true;
        _settings.ShowPointOfInterestMarkers = NavigationPoiBox.IsChecked == true;
        _settings.EnabledPoiTypes = PoiTypesPanel.Children.OfType<CheckBox>()
            .Where(box => box.IsChecked == true).Aggregate(PoiFilter.None, (mask, box) => mask | Enum.Parse<PoiFilter>((string)box.Tag));
        _nativeRadarSubmitted = false;
        try { await _settings.SaveAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not save navigation filters", MessageBoxButton.OK, MessageBoxImage.Warning); }

        if (!_viewModel.NavigationActive || _navigationBusy || !_bridge.SupportsNavigationOverlay) return;
        try
        {
            _navigationBusy = true;
            UpdateNavigationUi();
            var quests = NavigationQuestBox.IsChecked == true;
            var points = NavigationPoiBox.IsChecked == true;
            if (!quests && !points)
            {
                await DisableNavigationOverlayAsync(clearBridgeState: true);
            }
            else
            {
                await EnableNavigationBridgeAsync(quests, points);
            }
        }
        catch (Exception ex)
        {
            await DisableNavigationOverlayAsync(clearBridgeState: false);
            MessageBox.Show(ex.Message, "Could not update navigation filters", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _navigationBusy = false;
            UpdateNavigationUi();
        }
    }

    private void UpdateNavigationUi()
    {
        UpdateMasteryAuditUi();
        UpdateBookmarkUi();
        var available = _bridge.IsConnected && _bridge.SupportsNavigationOverlay && _navigationCatalog is not null;
        _viewModel.NavigationAvailable = available;
        NavigationToggleButton.IsEnabled = available && !_navigationBusy;
        NavigationQuestBox.IsEnabled = available && !_navigationBusy;
        NavigationPoiBox.IsEnabled = available && !_navigationBusy;
        NavigationToggleButton.Content = _viewModel.NavigationActive ? "Disable Radar Overlay" : "Enable Radar Overlay";
        NavigationToggleButton.Background = (Brush)FindResource(_viewModel.NavigationActive ? "Danger" : "Accent");
        NavigationToggleButton.Foreground = _viewModel.NavigationActive ? Brushes.White : (Brush)FindResource("AccentForeground");
        NavigationStatusDot.Fill = (Brush)FindResource(_viewModel.NavigationActive ? "Accent" : "SecondaryText");
        _viewModel.NavigationStatusText = _navigationBusy ? "Updating the radar filters…" :
            _viewModel.NavigationActive ? "Active — the click-through radar appears while Grim Dawn is in the foreground." :
            available ? $"Ready — {_navigationCatalog!.Markers.Count:N0} installed locations indexed." :
            _bridge.IsConnected ? "The overlay reader or installed navigation catalog is unavailable for this game build." :
            "Connect to Grim Dawn to enable the external radar overlay.";
    }

    private void StartNavigationOverlay()
    {
        _navigationOverlay ??= new NavigationOverlayWindow();
        if (!_navigationOverlay.IsVisible) _navigationOverlay.Show();
        _navigationPollCount = 0;
        _nativeRadarContext = 0;
        _nativeRadarSubmitted = false;
        _questNavigationState = new();
        _questStageDataReady = false;
        _activeQuestPaths = [];
        _liveMapMarkers = [];
        _regionalQuestMarkers = [];
        _currentLevelPath = null;
        _questMarkerCache.Clear();
        _navigationTimer ??= new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _navigationTimer.Tick -= PollNavigationOverlay;
        _navigationTimer.Tick += PollNavigationOverlay;
        _navigationTimer.Start();
        PollNavigationOverlay(this, EventArgs.Empty);
    }

    private async void PollNavigationOverlay(object? sender, EventArgs e)
    {
        if (_navigationPollBusy || !_viewModel.NavigationActive || _navigationCatalog is null) return;
        _navigationPollBusy = true;
        try
        {
            var player = await _bridge.GetPlayerPositionAsync(_lifetime.Token);
            if (_navigationPollCount++ % 15 == 0)
            {
                _currentLevelPath = _bridge.SupportsRegion
                    ? await _bridge.GetPlayerRegionPathAsync(_lifetime.Token)
                    : null;
                var nextQuestState = NavigationQuestBox.IsChecked == true
                    ? await _bridge.GetQuestNavigationStateAsync(_lifetime.Token) : null;
                _questStageDataReady = nextQuestState is not null;
                nextQuestState ??= new();
                var taskSignature = nextQuestState.Signature;
                var neededTokens = nextQuestState.NeededTokens(_navigationCatalog.Markers).ToArray();
                foreach (var token in neededTokens)
                    nextQuestState.Tokens[token] = await _bridge.HasTokenAsync(token, _lifetime.Token);
                if (neededTokens.Length > 0)
                {
                    var afterTokens = await _bridge.GetQuestNavigationStateAsync(_lifetime.Token);
                    if (afterTokens?.Signature != taskSignature)
                    {
                        nextQuestState = new();
                        _questStageDataReady = false;
                    }
                }
                if (_questNavigationState.Signature != nextQuestState.Signature) _questMarkerCache.Clear();
                _questNavigationState = nextQuestState;
                _activeQuestPaths = nextQuestState.Tasks.Keys.ToArray();
                var catalogObjectives = NavigationOverlayProjection.Build(player, _navigationCatalog.Markers,
                    _activeQuestPaths, NavigationQuestBox.IsChecked == true, false,
                    _settings.NavigationOverlayRange, _currentLevelPath, questState: nextQuestState);
                NavigationQuestDiagnosticsText.Text = NavigationQuestBox.IsChecked != true
                    ? "Quest objectives are off."
                    : !_bridge.SupportsLiveQuestTasks
                        ? "Reconnect with the updated bridge for reliable long-range quest data."
                        : !_questStageDataReady
                            ? "Quest data unavailable — long-range objectives cannot be resolved yet."
                            : $"Quest data: {_activeQuestPaths.Count} active tracked quests • {catalogObjectives.Count} catalog targets in this map region.";
                _liveMapMarkers = await _bridge.GetLiveMapMarkersAsync(_lifetime.Token);
                var region = NavigationOverlayProjection.ResolveRegionFamily(_currentLevelPath) ??
                             NavigationOverlayProjection.ResolveCurrentRegionFamily(
                                 player, _navigationCatalog.Markers, _settings.NavigationOverlayRange);
                _regionalQuestMarkers = _questMarkerCache.Update(region, _activeQuestPaths, _liveMapMarkers, player,
                    Math.Max(120, _settings.NavigationOverlayRange * 1.75));
            }
            // Fetch the coherent render frame last, after slower quest/catalog queries.
            _minimapGeometry = _settings.AutomaticNavigationAlignment
                ? await _bridge.GetMinimapFrameAsync(_lifetime.Token) : null;
            var liveMarkers = _liveMapMarkers.Where(marker => marker.Type != 21)
                .Concat(_regionalQuestMarkers)
                .Select(ToOverlayMarker);
            if (_settings.AutomaticNavigationAlignment && _bridge.SupportsNativeRadar)
            {
                _navigationOverlay?.Hide(); // One renderer only, in every display mode.
                if (_minimapGeometry?.Projection is null || _minimapGeometry.RenderContext == 0)
                {
                    if (_nativeRadarSubmitted) await _bridge.ClearNativeRadarAsync(_lifetime.Token);
                    _nativeRadarSubmitted = false;
                    NavigationAlignmentStatusText.Text = "Waiting for a supported visible minimap; in-game radar hidden.";
                    _viewModel.NavigationStatusText = "Radar enabled — waiting for the game's minimap.";
                    return;
                }
                if (_nativeRadarContext != 0 && _nativeRadarContext != _minimapGeometry.RenderContext)
                {
                    await _bridge.ClearNativeRadarAsync(_lifetime.Token);
                    _nativeRadarContext = _minimapGeometry.RenderContext;
                    _nativeRadarSubmitted = false;
                    _navigationPollCount = 0; // Refresh map/quest selection before using the new render context.
                    return;
                }
                _nativeRadarContext = _minimapGeometry.RenderContext;
                if (!_nativeRadarSubmitted || _navigationPollCount % 3 == 1)
                {
                    var selected = NavigationOverlayProjection.Build(player, _navigationCatalog.Markers.Concat(liveMarkers), _activeQuestPaths,
                        NavigationQuestBox.IsChecked == true, NavigationPoiBox.IsChecked == true, _settings.NavigationOverlayRange,
                        _currentLevelPath, _minimapGeometry.Projection, _questNavigationState, _settings.EnabledPoiTypes);
                    await _bridge.SetNativeRadarFrameAsync(_nativeRadarContext, selected, _lifetime.Token);
                    _nativeRadarSubmitted = true;
                }
                NavigationAlignmentStatusText.Text = $"In-game rendering • minimap {_minimapGeometry.Width:0} px • fullscreen and windowed modes";
                if (_navigationPollCount % 15 == 1)
                {
                    var native = await _bridge.GetNativeRadarStatusAsync(_lifetime.Token);
                    _viewModel.NavigationStatusText = native.Rendering
                        ? $"Active — {native.Drawn} markers rendered inside Grim Dawn."
                        : "Radar enabled — waiting for a foreground game render.";
                    if (NavigationQuestBox.IsChecked == true && !_bridge.SupportsLiveQuestTasks)
                        _viewModel.NavigationStatusText += " Restart Grim Dawn and reconnect for reliable long-range quest stages.";
                    else if (NavigationQuestBox.IsChecked == true && !_questStageDataReady)
                        _viewModel.NavigationStatusText += " Waiting for tracked quest stages; nearby stars only.";
                }
                return;
            }
            if (_nativeRadarSubmitted)
            {
                await _bridge.ClearNativeRadarAsync(_lifetime.Token);
                _nativeRadarSubmitted = false;
            }
            var markerCount = _navigationOverlay?.UpdateOverlay(player, _navigationCatalog.Markers.Concat(liveMarkers), _activeQuestPaths,
                NavigationQuestBox.IsChecked == true, NavigationPoiBox.IsChecked == true, _bridge.ProcessId,
                _settings.NavigationOverlayOffsetX, _settings.NavigationOverlayOffsetY, _settings.NavigationOverlayScale,
                _settings.NavigationOverlayRange, _currentLevelPath, _settings.AutomaticNavigationAlignment, _minimapGeometry, _questNavigationState, _settings.EnabledPoiTypes);
            NavigationAlignmentStatusText.Text = _settings.AutomaticNavigationAlignment && !_bridge.SupportsMinimapProjection
                ? "Automatic zoom matching requires the new bridge and a supported game build. Restart the game and reconnect, or use Advanced manual calibration."
                : _navigationOverlay?.AlignmentStatus ?? "Waiting for the game's minimap.";
            if (!_bridge.SupportsNativeRadar)
                NavigationAlignmentStatusText.Text += " External-window fallback: Fullscreen requires the latest supported bridge; restart Grim Dawn and reconnect.";
            if (_navigationPollCount % 15 == 1 && markerCount is int drawn)
            {
                var liveQuests = _liveMapMarkers.Count(marker => marker.Type == 21);
                var cachedQuests = Math.Max(0, _regionalQuestMarkers.Count - liveQuests);
                var livePoints = _liveMapMarkers.Count - liveQuests;
                _viewModel.NavigationStatusText = $"Active — {drawn} drawn • {liveQuests} live quest • {cachedQuests} regional quest • {livePoints} live POI • player X {player.X:0.0}, Z {player.Z:0.0}.";
                if (NavigationQuestBox.IsChecked == true && !_questStageDataReady)
                    _viewModel.NavigationStatusText += _bridge.SupportsQuestTasks
                        ? " Waiting for tracked quest stages; nearby stars only."
                        : " Restart Grim Dawn and reconnect to enable long-range quest stages.";
            }
            else if (_navigationPollCount % 15 == 1 && markerCount is null)
                _viewModel.NavigationStatusText = "Radar enabled — overlay hidden while the game or a supported minimap is not visible. Use Borderless Windowed for the external overlay.";
        }
        catch (OperationCanceledException) { }
        catch
        {
            await DisableNavigationOverlayAsync(clearBridgeState: false);
            SetStatus("Radar stopped", "The game connection ended or position data became unavailable.", 0);
            UpdateNavigationUi();
        }
        finally { _navigationPollBusy = false; }
    }

    private async Task DisableNavigationOverlayAsync(bool clearBridgeState)
    {
        _navigationTimer?.Stop();
        if (_bridge.IsConnected && _bridge.SupportsNativeRadar)
        {
            try { await _bridge.ClearNativeRadarAsync(_lifetime.Token); } catch { }
        }
        _nativeRadarSubmitted = false;
        _nativeRadarContext = 0;
        _navigationOverlay?.Hide();
        NavigationQuestDiagnosticsText.Text = "";
        _activeQuestPaths = [];
        _liveMapMarkers = [];
        _regionalQuestMarkers = [];
        _currentLevelPath = null;
        _minimapGeometry = null;
        _questMarkerCache.Clear();
        _viewModel.NavigationActive = false;
        if (clearBridgeState && _bridge.IsConnected && _bridge.SupportsNavigationOverlay)
        {
            try { await _bridge.ClearNavigationAsync(_lifetime.Token); }
            catch { }
        }
    }

    private async Task EnableNavigationBridgeAsync(bool quests, bool points)
    {
        // Capture the game-owned minimap records while temporarily asking the
        // map builder to include long-range quest targets and undiscovered
        // travel utilities. The bridge restores every game object field after
        // each map pass and never writes marker discovery or quest state.
        await _bridge.SetRadarCaptureAsync(quests, points, _lifetime.Token);
        try
        {
            await _bridge.SetNavigationAsync(quests, points, _lifetime.Token);
        }
        catch
        {
            try { await _bridge.ClearNavigationAsync(_lifetime.Token); }
            catch { }
            throw;
        }
    }

    private static WorldMarkerRecord ToOverlayMarker(LiveMapMarkerRecord marker)
    {
        var category = marker.Type switch
        {
            3 => "Travel Utility",
            21 => "Quest Objective",
            2 => "NPC or Site",
            7 => "Vendor",
            13 => "Smuggler",
            16 => "Devotion Shrine",
            18 => "Illusionist",
            19 => "Monster Shrine",
            20 => "Travel Utility",
            _ => "Point of Interest"
        };
        var position = marker.Position;
        var id = $"live:{marker.Type}:{BitConverter.SingleToUInt32Bits(position.X):x8}:{BitConverter.SingleToUInt32Bits(position.Z):x8}";
        return new WorldMarkerRecord(id, category, $"live://minimap/{marker.Type}", "live://current-map",
            position.X, position.Y, position.Z, ["$live"],
            CleanMinimapLabel(marker.Label) ?? (marker.Type == 21 ? "Active quest objective" : category));
    }

    private static string? CleanMinimapLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = System.Text.RegularExpressions.Regex.Replace(value, @"\{\^[^}]*\}", "").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private async void AutoUnbridgeChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded) return;
        _settings.AutoUnbridgeOnExit = AutoUnbridgeBox.IsChecked == true;
        try { await _settings.SaveAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not save setting", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void NavigationAlignmentModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded) return;
        _settings.AutomaticNavigationAlignment = NavigationAutomaticBox.IsChecked == true;
        NavigationManualPanel.IsEnabled = !_settings.AutomaticNavigationAlignment;
        NavigationRangeSlider.IsEnabled = !_settings.AutomaticNavigationAlignment;
        _minimapGeometry = null;
        _navigationPollCount = 0;
        NavigationAlignmentStatusText.Text = _settings.AutomaticNavigationAlignment
            ? "Waiting for the game's live minimap bounds." : "Manual calibration enabled.";
        try { await _settings.SaveAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not save alignment mode", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void NavigationOverlaySettingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsLoaded) return;
        _settings.NavigationOverlayOffsetX = NavigationHorizontalOffsetSlider.Value;
        _settings.NavigationOverlayOffsetY = NavigationVerticalOffsetSlider.Value;
        _settings.NavigationOverlayScale = NavigationScaleSlider.Value;
        _settings.NavigationOverlayRange = NavigationRangeSlider.Value;
        UpdateNavigationAlignmentLabels();
        try { await _settings.SaveAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not save overlay alignment", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void ResetNavigationOverlayAlignment(object sender, RoutedEventArgs e)
    {
        _settingsLoaded = false;
        NavigationHorizontalOffsetSlider.Value = 0;
        NavigationVerticalOffsetSlider.Value = 0;
        NavigationScaleSlider.Value = 1;
        _settings.NavigationOverlayOffsetX = 0;
        _settings.NavigationOverlayOffsetY = 0;
        _settings.NavigationOverlayScale = 1;
        UpdateNavigationAlignmentLabels();
        _settingsLoaded = true;
        try { await _settings.SaveAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not save overlay alignment", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void UpdateNavigationAlignmentLabels()
    {
        NavigationHorizontalOffsetText.Text = $"{NavigationHorizontalOffsetSlider.Value:+0;-0;0} px";
        NavigationVerticalOffsetText.Text = $"{NavigationVerticalOffsetSlider.Value:+0;-0;0} px";
        NavigationScaleText.Text = $"{NavigationScaleSlider.Value:0.00}×";
        NavigationRangeText.Text = $"{NavigationRangeSlider.Value:0} u";
    }

    private async void CloseCompanion(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        StopBookmarkHotkeys();
        SuspendGameplayAssistUi();
        try
        {
            _navigationTimer?.Stop();
            _navigationOverlay?.Close();
            _navigationOverlay = null;
            _viewModel.NavigationActive = false;
            // Cancel any in-flight radar read before attempting the shutdown
            // handshake. Otherwise a dead game can leave that read holding the
            // pipe send lock while window closure waits behind it.
            _lifetime.Cancel();
            if (_settings.AutoUnbridgeOnExit && _bridge.ProcessId is not null)
            {
                if (_bridge.IsGameProcessRunning)
                    SetStatus("Disconnecting", "Removing the game-thread hook and unloading the companion bridge…", 75);
                _ = await _bridge.UnloadAsync(CancellationToken.None);
            }
        }
        catch { /* The game may already have exited; local shutdown should still complete. */ }
        finally
        {
            await _bridge.DisposeAsync();
            _lifetime.Dispose();
            _shutdownComplete = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    private async void ChooseGameFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the Grim Dawn installation folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        if (!GameLocator.IsValid(dialog.FolderName)) { MessageBox.Show("That folder does not contain the x64 Grim Dawn installation and database."); return; }
        _settings.GameDirectory = dialog.FolderName;
        await _settings.SaveAsync();
        _game = GameLocator.Locate(dialog.FolderName);
        _viewModel.GameDirectory = _game!.RootDirectory;
        await InitializeAsync();
    }

    private void SetStatus(string title, string detail, double progress)
    {
        _viewModel.StatusTitle = title;
        _viewModel.StatusDetail = detail;
        _viewModel.Progress = progress;
    }

    private void ShowCatalog(object sender, RoutedEventArgs e) => ShowPage(CatalogPage, "Item Catalog", "Search installed items and deliver your selection to the loaded character.", CatalogNav);
    private void ShowNavigation(object sender, RoutedEventArgs e) => ShowPage(NavigationPage, "Navigation", "Track quest objectives and points of interest, or return to saved locations.", NavigationNav);
    private void ShowUtilities(object sender, RoutedEventArgs e) => ShowPage(UtilitiesPage, "Character Utilities", "Character and progression tools.", UtilitiesNav);
    private void ShowCompatibility(object sender, RoutedEventArgs e) => ShowPage(CompatibilityPage, "Compatibility", "Patch-aware symbol verification and local module fingerprints.", CompatibilityNav);
    private void ShowSettings(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, "Settings", "Choose the installed game directory and review the safety model.", SettingsNav);

    private void ShowPage(FrameworkElement page, string title, string subtitle, Button active)
    {
        foreach (var element in new FrameworkElement[] { CatalogPage, NavigationPage, UtilitiesPage, CompatibilityPage, SettingsPage }) element.Visibility = element == page ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { CatalogNav, NavigationNav, UtilitiesNav, CompatibilityNav, SettingsNav }) button.Background = button == active ? (Brush)FindResource("AccentDark") : Brushes.Transparent;
        PageTitle.Text = title; PageSubtitle.Text = subtitle;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, uint valueSize);
}

internal sealed class AppSettings
{
    private static readonly string PathName = Path.Combine(CompanionDataPaths.DirectoryPath, "settings.json");
    public string? GameDirectory { get; set; }
    public bool AutoUnbridgeOnExit { get; set; } = true;
    public bool ShowActiveQuestMarkers { get; set; } = true;
    public bool ShowPointOfInterestMarkers { get; set; } = true;
    public PoiFilter EnabledPoiTypes { get; set; } = PoiFilter.All;
    public double NavigationOverlayOffsetX { get; set; }
    public bool AutomaticNavigationAlignment { get; set; } = true;
    public double NavigationOverlayOffsetY { get; set; }
    public double NavigationOverlayScale { get; set; } = 1;
    public double NavigationOverlayRange { get; set; } = 48;
    public int? NavigationProjectionVersion { get; set; }

    public static async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (File.Exists(PathName))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(PathName)) ?? new();
                if (settings.NavigationProjectionVersion is null or < 3)
                {
                    // Projection v3 uses Grim Dawn's measured diagonal X/Z
                    // basis. Older range values described a different axis
                    // model and cannot be carried over meaningfully.
                    settings.NavigationOverlayRange = 48;
                    settings.NavigationProjectionVersion = 3;
                }
                return settings;
            }
        }
        catch { }
        return new AppSettings { NavigationProjectionVersion = 3 };
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        await File.WriteAllTextAsync(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
