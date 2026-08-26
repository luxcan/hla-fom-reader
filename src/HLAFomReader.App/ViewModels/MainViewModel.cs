using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.Core.Registry;

namespace HLAFomReader.App.ViewModels;

/// <summary>Shell view model: owns navigation, the window chrome commands and the status bar.</summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IFomRepository _repository;
    private readonly IDialogService _dialogs;

    private NavigationItem _selectedNavigation = null!;
    private object _currentView = null!;
    private string _statusText = "Ready";
    private string _statusDetail = "";
    private bool _isDatabaseEncrypted;
    private bool _isSidebarCollapsed;

    /// <summary>Sidebar width with the labels showing.</summary>
    private const double SidebarExpandedWidth = 196;

    /// <summary>Sidebar width collapsed to its icon rail, per the design reference.</summary>
    private const double SidebarCollapsedWidth = 52;

    public MainViewModel(IFomRepository repository, IDialogService dialogs)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        Registry = new RegistryViewModel(repository, dialogs);
        Compare = new CompareViewModel(repository, dialogs);
        Settings = new SettingsViewModel();

        NavigationItems = new ObservableCollection<NavigationItem>
        {
            new(AppScreen.Registry, "Registry", "Register FOM and FED files and inspect what was parsed"),
            new(AppScreen.Compare, "Compare", "Diff two registered FOMs property by property"),
            new(AppScreen.Settings, "Settings", "Appearance, the registry database, and which build this is"),
        };

        NavigateCommand = new RelayCommand<NavigationItem>(item => { if (item is not null) Navigate(item.Screen); });
        ShowSettingsCommand = new RelayCommand(() => Navigate(AppScreen.Settings));

        // Read rather than defaulted: on a laptop the sidebar is usually collapsed for good, and
        // re-collapsing it every launch is the kind of small tax that makes a tool tiring.
        _isSidebarCollapsed = AppConfig.GetSidebarCollapsed();
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarCollapsed = !IsSidebarCollapsed);

        MinimizeCommand = new RelayCommand(() => SetWindowState(WindowState.Minimized));
        MaximizeCommand = new RelayCommand(ToggleMaximize);
        CloseCommand = new RelayCommand(() => Application.Current.MainWindow?.Close());

        // The registry is the source of truth for the compare screen's two pickers.
        Registry.RegistryChanged += OnRegistryChanged;
        Registry.DetailRequested += OnDetailRequested;

        _selectedNavigation = NavigationItems[0];
        _selectedNavigation.IsSelected = true;
        _currentView = Registry;
    }

    public RegistryViewModel Registry { get; }
    public CompareViewModel Compare { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<NavigationItem> NavigationItems { get; }

    public RelayCommand<NavigationItem> NavigateCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ToggleSidebarCommand { get; }
    public RelayCommand MinimizeCommand { get; }
    public RelayCommand MaximizeCommand { get; }
    public RelayCommand CloseCommand { get; }

    public string Title => "HLA FOM Reader";

    public string DatabasePath => _repository.DatabasePath;

    public string DatabaseName => Path.GetFileName(_repository.DatabasePath);

    /// <summary>
    /// Whether the open database is SQLCipher-encrypted. Cached rather than probed per binding
    /// evaluation, because answering it means opening the file and reading from it.
    /// </summary>
    public bool IsDatabaseEncrypted
    {
        get => _isDatabaseEncrypted;
        private set
        {
            if (SetProperty(ref _isDatabaseEncrypted, value))
                OnPropertyChanged(nameof(IsDatabasePlaintext), nameof(EncryptionSummary));
        }
    }

    public bool IsDatabasePlaintext => !IsDatabaseEncrypted;

    public string EncryptionSummary => IsDatabaseEncrypted ? "Password protected" : "Not encrypted";

    /// <summary>Re-reads the database's encryption state, e.g. after a password was set or removed.</summary>
    public void RefreshDatabaseState()
    {
        IsDatabaseEncrypted = FomDatabase.IsEncrypted(_repository.DatabasePath);
        OnPropertyChanged(nameof(DatabasePath), nameof(DatabaseName));

        // Worked out once here and handed on, because answering it means opening the file.
        Settings.UpdateDatabase(_repository.DatabasePath, IsDatabaseEncrypted);
    }

    /// <summary>
    /// Whether the sidebar is reduced to its icon rail. On a laptop screen the labels cost 148 DIP
    /// that the record grids need more than the two destinations do.
    /// </summary>
    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        set
        {
            if (!SetProperty(ref _isSidebarCollapsed, value)) return;

            OnPropertyChanged(nameof(IsSidebarExpanded), nameof(SidebarWidth), nameof(SidebarToggleTooltip));
            AppConfig.SetSidebarCollapsed(value);
        }
    }

    /// <summary>Inverse of <see cref="IsSidebarCollapsed"/>, for the parts that only show expanded.</summary>
    public bool IsSidebarExpanded => !IsSidebarCollapsed;

    /// <summary>
    /// The width the sidebar should be. A plain double rather than a <see cref="GridLength"/>
    /// because the panel animates between the two, and WPF ships no GridLength animation — the
    /// column is sized to its content and the panel itself carries the width.
    /// </summary>
    public double SidebarWidth => IsSidebarCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth;

    /// <summary>Names the action the toggle performs, not the state it is in.</summary>
    public string SidebarToggleTooltip =>
        IsSidebarCollapsed ? "Expand sidebar (Ctrl+B)" : "Collapse sidebar (Ctrl+B)";

    /// <summary>The build, short enough for the status bar corner to carry it permanently.</summary>
    public string VersionLabel => BuildInfo.VersionSummary;

    /// <summary>
    /// What the status bar's version control says on hover. The question behind it is usually just
    /// "which build is this?", which the label already answers without a click.
    /// </summary>
    public string VersionTooltip =>
        $"{BuildInfo.ProductName} {BuildInfo.VersionSummary} — open Settings";

    public NavigationItem SelectedNavigation
    {
        get => _selectedNavigation;
        private set => SetProperty(ref _selectedNavigation, value);
    }

    public object CurrentView
    {
        get => _currentView;
        private set => SetProperty(ref _currentView, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>Right-hand status segment: counts, or the active comparison summary.</summary>
    public string StatusDetail
    {
        get => _statusDetail;
        set => SetProperty(ref _statusDetail, value);
    }

    public void Initialize()
    {
        RefreshDatabaseState();
        Registry.Load();
        OnRegistryChanged(this, EventArgs.Empty);
    }

    public void Navigate(AppScreen screen)
    {
        var target = NavigationItems.FirstOrDefault(n => n.Screen == screen);
        if (target is null) return;

        // Already on this screen — unless a drill-down is covering it, in which case clicking the
        // nav item is the natural way back out.
        if (ReferenceEquals(target, SelectedNavigation) && !IsShowingDetail) return;

        foreach (var item in NavigationItems)
            item.IsSelected = ReferenceEquals(item, target);

        SelectedNavigation = target;
        CurrentView = screen switch
        {
            AppScreen.Registry => Registry,
            AppScreen.Compare => Compare,
            _ => Settings,
        };
        StatusText = target.Title;
    }

    /// <summary>True while a drill-down screen is covering the selected section.</summary>
    private bool IsShowingDetail => CurrentView is FomDetailViewModel;

    /// <summary>
    /// Swaps in the full-width explorer for one FOM. The sidebar keeps Registry highlighted, because
    /// this is a drill-down within that section rather than a fourth destination.
    /// </summary>
    private void OnDetailRequested(object? sender, FomRegistryEntry entry)
    {
        var detail = new FomDetailViewModel(_repository, _dialogs, entry);
        detail.CloseRequested += OnDetailClosed;

        CurrentView = detail;
        StatusText = entry.DisplayName;
    }

    private void OnDetailClosed(object? sender, EventArgs e)
    {
        if (sender is FomDetailViewModel detail)
            detail.CloseRequested -= OnDetailClosed;

        CurrentView = Registry;
        StatusText = "Registry";
    }

    private void OnRegistryChanged(object? sender, EventArgs e)
    {
        var entries = Registry.Entries.ToList();

        NavigationItems[0].Count = entries.Count.ToString(CultureInfo.InvariantCulture);

        // Compare and Settings deliberately carry no badge. Registry's is a count — a number that
        // says how much is in there and changes as you work. "ready" was neither: it restated the
        // enabled state of a row you can already see is enabled, in a slot the eye reads as a count.
        // A badge that never counts anything is chrome pretending to be information.

        Compare.RefreshSources(entries);

        var standards = entries.Select(en => en.StandardBadge).Distinct().Count();
        StatusDetail = entries.Count == 0
            ? "No FOMs registered"
            : $"{entries.Count} FOM{(entries.Count == 1 ? "" : "s")} · {standards} standard{(standards == 1 ? "" : "s")}";
    }

    private static void SetWindowState(WindowState state)
    {
        if (Application.Current.MainWindow is { } window)
            window.WindowState = state;
    }

    /// <summary>
    /// Releases the screens that hold onto anything outliving the shell. The shell is rebuilt from
    /// scratch every time the registry database changes, so "outliving the shell" is a real
    /// lifetime and not a theoretical one.
    /// </summary>
    public void Dispose() => Settings.Dispose();

    private static void ToggleMaximize()
    {
        if (Application.Current.MainWindow is not { } window) return;

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}
