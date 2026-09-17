using GrimDawnCompanion.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace GrimDawnCompanion.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _searchText = "";
    private string _selectedCategory = "All Categories";
    private string _selectedSource = "All Content";
    private ItemRecord? _selectedItem;
    private string _statusTitle = "Starting";
    private string _statusDetail = "Locating Grim Dawn…";
    private double _progress;
    private bool _isBusy;
    private bool _isConnected;
    private string _gameDirectory = "Not detected";
    private CompatibilityReport? _compatibility;
    private string _blueprintSearch = "";
    private string _tokenSearch = "";
    private ItemRecord? _selectedBlueprint;
    private QuestTokenRecord? _selectedToken;
    private string _tokenState = "Not Checked";
    private CharacterResources? _resources;
    private PositionBookmark? _selectedBookmark;
    private bool _navigationAvailable;
    private bool _navigationActive;
    private string _navigationStatusText = "Connect to Grim Dawn to enable temporary map markers.";

    public ObservableCollection<ItemRecord> Items { get; } = [];
    public ObservableCollection<string> Categories { get; } = ["All Categories"];
    public ObservableCollection<string> Sources { get; } = ["All Content"];
    public ICollectionView ItemsView { get; }
    public ObservableCollection<SymbolVerification> Symbols { get; } = [];
    public ObservableCollection<ItemRecord> Blueprints { get; } = [];
    public ObservableCollection<QuestTokenRecord> QuestTokens { get; } = [];
    public ObservableCollection<PositionBookmark> PositionBookmarks { get; } = [];
    public ICollectionView BlueprintsView { get; }
    public ICollectionView QuestTokensView { get; }

    public MainViewModel()
    {
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
        ItemsView.SortDescriptions.Add(new(nameof(ItemRecord.Name), ListSortDirection.Ascending));
        BlueprintsView = CollectionViewSource.GetDefaultView(Blueprints);
        BlueprintsView.Filter = value => value is ItemRecord item && Matches(item, BlueprintSearch);
        BlueprintsView.SortDescriptions.Add(new(nameof(ItemRecord.Name), ListSortDirection.Ascending));
        QuestTokensView = CollectionViewSource.GetDefaultView(QuestTokens);
        QuestTokensView.Filter = value => value is QuestTokenRecord token && (string.IsNullOrWhiteSpace(TokenSearch) || token.Name.Contains(TokenSearch, StringComparison.OrdinalIgnoreCase) || token.Source.Contains(TokenSearch, StringComparison.OrdinalIgnoreCase));
        QuestTokensView.SortDescriptions.Add(new(nameof(QuestTokenRecord.Name), ListSortDirection.Ascending));
    }

    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) RefreshFilter(); } }
    public string SelectedCategory { get => _selectedCategory; set { if (Set(ref _selectedCategory, string.IsNullOrWhiteSpace(value) ? "All Categories" : value)) RefreshFilter(); } }
    public string SelectedSource { get => _selectedSource; set { if (Set(ref _selectedSource, string.IsNullOrWhiteSpace(value) ? "All Content" : value)) RefreshFilter(); } }
    public ItemAffixIndex AffixIndex { get; private set; } = new([]);
    public ObservableCollection<AffixChoice> PrefixChoices { get; } = [AffixChoice.None];
    public ObservableCollection<AffixChoice> SuffixChoices { get; } = [AffixChoice.None];
    private AffixChoice _selectedPrefix=AffixChoice.None,_selectedSuffix=AffixChoice.None;
    public AffixChoice SelectedPrefix { get=>_selectedPrefix; set=>Set(ref _selectedPrefix,value ?? AffixChoice.None); }
    public AffixChoice SelectedSuffix { get=>_selectedSuffix; set=>Set(ref _selectedSuffix,value ?? AffixChoice.None); }
    public bool HasPrefixChoices => PrefixChoices.Count>1;
    public bool HasSuffixChoices => SuffixChoices.Count>1;
    public string AffixHint => HasPrefixChoices || HasSuffixChoices ? "Choose compatible prefixes and suffixes from the installed game data. Higher-tier affixes may increase item requirements. Select None to leave that affix unchanged from the base item." : "No compatible prefix or suffix options were found for this item.";
    public ItemRecord? SelectedItem { get => _selectedItem; set { if(Set(ref _selectedItem, value)) RefreshAffixes(); } }
    private void RefreshAffixes()
    {
        SelectedPrefix=SelectedSuffix=AffixChoice.None;
        PrefixChoices.Clear();SuffixChoices.Clear();
        foreach(var choice in AffixIndex.Choices(SelectedItem,true)) PrefixChoices.Add(choice);
        foreach(var choice in AffixIndex.Choices(SelectedItem,false)) SuffixChoices.Add(choice);
        OnPropertyChanged(nameof(SelectedPrefix));OnPropertyChanged(nameof(SelectedSuffix));
        OnPropertyChanged(nameof(HasPrefixChoices));OnPropertyChanged(nameof(HasSuffixChoices));OnPropertyChanged(nameof(AffixHint));
    }
    public string StatusTitle { get => _statusTitle; set => Set(ref _statusTitle, value); }
    public string StatusDetail { get => _statusDetail; set => Set(ref _statusDetail, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }
    public bool IsConnected { get => _isConnected; set { if (Set(ref _isConnected, value)) OnPropertyChanged(nameof(ConnectionText)); } }
    public string ConnectionText => IsConnected ? "Active" : "Disabled";
    public string GameDirectory { get => _gameDirectory; set => Set(ref _gameDirectory, value); }
    public CompatibilityReport? Compatibility { get => _compatibility; set => Set(ref _compatibility, value); }
    public string ItemCountText => $"{ItemsView.Cast<object>().Count():N0} of {Items.Count:N0} items";
    public string BlueprintSearch { get => _blueprintSearch; set { if (Set(ref _blueprintSearch, value)) { BlueprintsView.Refresh(); OnPropertyChanged(nameof(BlueprintCountText)); } } }
    public string TokenSearch { get => _tokenSearch; set { if (Set(ref _tokenSearch, value)) { QuestTokensView.Refresh(); OnPropertyChanged(nameof(TokenCountText)); } } }
    public ItemRecord? SelectedBlueprint { get => _selectedBlueprint; set => Set(ref _selectedBlueprint, value); }
    public QuestTokenRecord? SelectedToken { get => _selectedToken; set { if (Set(ref _selectedToken, value)) TokenState = "Not Checked"; } }
    public string TokenState { get => _tokenState; set => Set(ref _tokenState, value); }
    public CharacterResources? Resources { get => _resources; set { if (Set(ref _resources, value)) { OnPropertyChanged(nameof(MoneyText)); OnPropertyChanged(nameof(SkillPointsText)); OnPropertyChanged(nameof(AttributePointsText)); OnPropertyChanged(nameof(DevotionPointsText)); } } }
    public string MoneyText => Resources?.Money.ToString("N0") ?? "—";
    public string SkillPointsText => Resources?.SkillPoints.ToString("N0") ?? "—";
    public string AttributePointsText => Resources?.AttributePoints.ToString("N0") ?? "—";
    public string DevotionPointsText => Resources?.DevotionPoints.ToString("N0") ?? "—";
    public PositionBookmark? SelectedBookmark { get => _selectedBookmark; set => Set(ref _selectedBookmark, value); }
    public bool NavigationAvailable { get => _navigationAvailable; set => Set(ref _navigationAvailable, value); }
    public bool NavigationActive { get => _navigationActive; set => Set(ref _navigationActive, value); }
    public string NavigationStatusText { get => _navigationStatusText; set => Set(ref _navigationStatusText, value); }
    public string BlueprintCountText => $"{BlueprintsView.Cast<object>().Count():N0} formulas";
    public string TokenCountText => $"{QuestTokensView.Cast<object>().Count():N0} tokens";

    public void SetCatalog(CatalogDocument document)
    {
        AffixIndex=new(document.AffixRecords);
        Items.Clear();
        foreach (var item in document.Items) Items.Add(item);
        Blueprints.Clear();
        foreach (var item in document.Items)
        {
            if (item.Category == "Blueprints") Blueprints.Add(item);
        }
        Categories.Clear(); Categories.Add("All Categories");
        foreach (var value in document.Items.Select(x => x.Category).Distinct().Order()) Categories.Add(value);
        Sources.Clear(); Sources.Add("All Content");
        foreach (var value in document.Items.Select(x => x.Source).Distinct().Order()) Sources.Add(value);
        _selectedCategory = "All Categories";
        _selectedSource = "All Content";
        OnPropertyChanged(nameof(SelectedCategory));
        OnPropertyChanged(nameof(SelectedSource));
        ItemsView.Refresh();
        OnPropertyChanged(nameof(ItemCountText));
        SelectedItem ??= Items.FirstOrDefault();
        RefreshAffixes();
        SelectedBlueprint ??= Blueprints.FirstOrDefault();
        OnPropertyChanged(nameof(BlueprintCountText));
    }

    public void SetQuestTokens(IEnumerable<QuestTokenRecord> tokens)
    {
        QuestTokens.Clear();
        foreach (var token in tokens) QuestTokens.Add(token);
        QuestTokensView.Refresh();
        SelectedToken = QuestTokens.FirstOrDefault();
        OnPropertyChanged(nameof(TokenCountText));
    }

    public void SetCompatibility(CompatibilityReport report)
    {
        Compatibility = report;
        Symbols.Clear();
        foreach (var symbol in report.Symbols) Symbols.Add(symbol);
    }

    public void NotifyConnectionChanged() { OnPropertyChanged(nameof(IsConnected)); OnPropertyChanged(nameof(ConnectionText)); }

    private void RefreshFilter() { ItemsView.Refresh(); OnPropertyChanged(nameof(ItemCountText)); }

    private bool FilterItem(object value)
    {
        if (value is not ItemRecord item) return false;
        if (SelectedCategory != "All Categories" && item.Category != SelectedCategory) return false;
        if (SelectedSource != "All Content" && item.Source != SelectedSource) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        return item.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
               item.RecordPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               item.Classification.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(ItemRecord item, string search) => string.IsNullOrWhiteSpace(search) ||
        item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || item.RecordPath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        item.Classification.Contains(search, StringComparison.OrdinalIgnoreCase) || item.Source.Contains(search, StringComparison.OrdinalIgnoreCase);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(property); return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    public event PropertyChangedEventHandler? PropertyChanged;
}
