using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using HLAFomReader.App.Infrastructure;

namespace HLAFomReader.App.ViewModels;

/// <summary>
/// The Settings screen: how the app looks, which registry database it is pointed at, and which
/// build it is.
/// </summary>
/// <remarks>
/// The database facts are pushed in by the shell rather than read here. Answering "is this file
/// encrypted?" means opening it, so it is worth doing once when it changes and handing the answer
/// to whoever needs it, not once per view that asks.
/// </remarks>
public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private const string CopyLink = "Copy link";

    /// <summary>How long the copy button confirms for before going back to naming its action.</summary>
    private static readonly TimeSpan CopyConfirmation = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _copyReset;

    private string _databasePath = "";
    private bool _isDatabaseEncrypted;
    private AppTheme _theme = ThemeManager.Current;
    private bool _followsSystem = ThemeManager.FollowsSystem;
    private string _copyLinkLabel = CopyLink;

    public SettingsViewModel()
    {
        OpenReleasesCommand = new RelayCommand(OpenReleases);
        CopyReleasesLinkCommand = new RelayCommand(CopyReleasesLink);

        _copyReset = new DispatcherTimer { Interval = CopyConfirmation };
        _copyReset.Tick += (_, _) =>
        {
            _copyReset.Stop();
            CopyLinkLabel = CopyLink;
        };

        // The manager is the authority on which theme is merged, not this property. Following its
        // event rather than assuming keeps the two from drifting if anything else ever applies one.
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    // ---- appearance ---------------------------------------------------------------------------

    /// <summary>
    /// The active theme. Writing it switches the application and records the choice.
    /// </summary>
    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (_theme == value) return;

            // The new value is not stored here — ThemeManager raises ThemeChanged and the handler
            // reads it back, so what this property reports and what is actually merged cannot drift.
            ThemeManager.Apply(value);
        }
    }

    /// <summary>
    /// Where the current theme came from. Worth saying: a first launch inherits Windows, and
    /// somebody who has never touched this control should be able to find out why the app is the
    /// colour it is.
    /// </summary>
    public string ThemeSourceNote => _followsSystem
        ? "Matching the Windows app theme. Choosing a mode here overrides it from now on."
        : "Your choice, remembered for next launch.";

    // ---- registry database --------------------------------------------------------------------

    /// <summary>Absolute path of the open registry database.</summary>
    public string DatabasePath
    {
        get => _databasePath;
        private set
        {
            if (SetProperty(ref _databasePath, value))
                OnPropertyChanged(nameof(DatabaseName), nameof(DatabaseFolder));
        }
    }

    public string DatabaseName => Path.GetFileName(DatabasePath);

    /// <summary>The folder the database sits in, shown under the file name rather than beside it.</summary>
    public string DatabaseFolder => Path.GetDirectoryName(DatabasePath) ?? "";

    public bool IsDatabaseEncrypted
    {
        get => _isDatabaseEncrypted;
        private set
        {
            if (SetProperty(ref _isDatabaseEncrypted, value))
                OnPropertyChanged(nameof(IsDatabasePlaintext), nameof(EncryptionSummary), nameof(EncryptionDetail));
        }
    }

    public bool IsDatabasePlaintext => !IsDatabaseEncrypted;

    public string EncryptionSummary => IsDatabaseEncrypted ? "Password protected" : "Not encrypted";

    public string EncryptionDetail => IsDatabaseEncrypted
        ? "Encrypted in place with SQLCipher. The password is asked for every time this database is "
          + "opened, and there is no way to recover it if it is forgotten."
        : "Anyone who can open the file can read every FOM in it. Set a password to encrypt it in place.";

    /// <summary>Takes the database facts from the shell, which works them out once per change.</summary>
    public void UpdateDatabase(string path, bool encrypted)
    {
        DatabasePath = path;
        IsDatabaseEncrypted = encrypted;
    }

    // ---- about ---------------------------------------------------------------------------------

    public string ProductName => BuildInfo.ProductName;

    /// <summary>
    /// The one line that answers "which build is this?". Builds reach this application by hand as
    /// often as by tag, so the commit belongs next to the number.
    /// </summary>
    public string VersionLine
    {
        get
        {
            var line = $"Version {BuildInfo.Version}";

            // Date first, commit second: the date is what tells somebody whether they are running the
            // build they just made, and the commit is what identifies it once they are sure.
            if (BuildInfo.BuildDate is { Length: > 0 } date) line += $"  ·  built {date}";
            if (BuildInfo.Commit is { } commit) line += $"  ·  build {commit}";

            return line;
        }
    }

    public string Description => BuildInfo.Description;

    public string RepositoryUrl => BuildInfo.RepositoryUrl;

    /// <summary>The page a new build would appear on. What the Updates panel points at.</summary>
    public string ReleasesUrl => BuildInfo.ReleasesUrl;

    /// <summary>
    /// What the Updates panel says. Worded to be accurate about what pressing the button does: the
    /// app opens a page, it does not check anything.
    /// </summary>
    /// <remarks>
    /// There is deliberately no in-app update check. This application makes no network call of its
    /// own — see <see cref="OpenReleases"/> — and adding one here to poll a version endpoint would
    /// be the first, for a convenience that a link already covers.
    /// </remarks>
    public string UpdatesBody =>
        "Releases are published on GitHub. Opening the page below shows whether a newer build is "
        + "available and what changed in it — this app does not check for updates by itself, and "
        + "makes no network call of its own.";

    /// <summary>The two things that surprise somebody downloading a build for the first time.</summary>
    public string DownloadNote =>
        $"A download is a single {BuildInfo.ProductName.Replace(" ", "")}.exe — nothing to install. "
        + "It is not code-signed, so Windows SmartScreen may warn you the first time you run a new build.";

    public RelayCommand OpenReleasesCommand { get; }
    public RelayCommand CopyReleasesLinkCommand { get; }

    /// <summary>The copy button's own label, which confirms briefly instead of raising a dialog.</summary>
    public string CopyLinkLabel
    {
        get => _copyLinkLabel;
        private set => SetProperty(ref _copyLinkLabel, value);
    }

    /// <summary>
    /// Hands the URL to the shell rather than fetching anything. This application makes no network
    /// call of its own and this is not the place to start.
    /// </summary>
    private void OpenReleases()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser registered, or the shell refused. The URL is on screen either way, and a
            // link that would not open is not worth an error dialog.
        }
    }

    private void CopyReleasesLink()
    {
        try
        {
            Clipboard.SetText(ReleasesUrl);
            CopyLinkLabel = "Copied";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Another process is holding the clipboard open. Say so on the button rather than in a
            // dialog, since the URL is right there to select by hand.
            CopyLinkLabel = "Could not copy";
        }

        _copyReset.Stop();
        _copyReset.Start();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _theme = ThemeManager.Current;
        _followsSystem = ThemeManager.FollowsSystem;

        OnPropertyChanged(nameof(Theme), nameof(ThemeSourceNote));
    }

    /// <summary>
    /// Detaches from the theme manager.
    /// </summary>
    /// <remarks>
    /// The shell is rebuilt from scratch whenever the database changes, and <see cref="ThemeManager"/>
    /// is static — without this, every settings view model ever built would be kept alive by that
    /// event for the life of the process.
    /// </remarks>
    public void Dispose()
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _copyReset.Stop();
    }
}
