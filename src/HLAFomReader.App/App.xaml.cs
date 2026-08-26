using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.ViewModels;
using HLAFomReader.App.Views;
using HLAFomReader.Core.Registry;

namespace HLAFomReader.App;

/// <summary>
/// Composition root. Decides which registry database to open, unlocks it if it is encrypted, and
/// owns that repository for the process lifetime.
/// </summary>
public partial class App : Application
{
    private static SqliteFomRepository? _repository;

    /// <summary>Absolute path of the database currently open, or null before startup finishes.</summary>
    public static string? CurrentDatabasePath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // Before any window exists. The database picker and the unlock prompt can both be the first
        // thing a user sees, and a startup dialog painted in the wrong theme is the one flash of the
        // wrong colours that no later switch can undo.
        ThemeManager.Initialize();

        // Keep the process alive across the startup dialogs. Without this, closing the picker or the
        // password window — the only window open at that point — trips OnLastWindowClose and shuts
        // the app down before the shell is ever shown.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var startup = ResolveStartupDatabase(e.Args);
        if (startup is null)
        {
            Shutdown();
            return;
        }

        var repository = OpenRepository(owner: null, startup.Path, startup.Password);
        if (repository is null)
        {
            Shutdown();
            return;
        }

        _repository = repository;
        CurrentDatabasePath = startup.Path;
        AppConfig.SetLastDatabasePath(startup.Path);

        var shell = new MainViewModel(repository, new DialogService());
        var window = new MainWindow { DataContext = shell };

        MainWindow = window;
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        window.Show();

        shell.Initialize();
    }

    /// <summary>Where a database came from and, if it was just created, the password it was created with.</summary>
    private sealed record StartupDatabase(string Path, string? Password);

    /// <summary>
    /// Works out which database to open: an explicit <c>--db</c> argument, else the one remembered in
    /// <c>config.json</c>, else asks the user to open or create one.
    /// </summary>
    private static StartupDatabase? ResolveStartupDatabase(string[] args)
    {
        if (ReadDatabaseArgument(args) is { } explicitPath)
            return new StartupDatabase(explicitPath, null);

        var remembered = AppConfig.GetLastDatabasePath();

        // Remembered but gone. Say so rather than silently creating a new empty registry somewhere
        // else — a missing file is usually a disconnected drive, not a request to start over.
        if (remembered is not null && !File.Exists(remembered))
        {
            var choice = MessageWindow.Show(owner: null, "Database not found",
                "The registry database you last used could not be found.",
                $"{remembered}\n\n" +
                "Choose a different database?\n\n" +
                "•  Yes — pick or create another one\n" +
                "•  No — exit, so you can reconnect the drive or restore the file",
                MessageKind.Warning, MessageButtons.YesNo);

            if (choice != MessageResult.Yes) return null;
            remembered = null;
        }

        if (remembered is not null)
            return new StartupDatabase(remembered, null);

        // Nothing configured — first run.
        var picked = DatabasePickerWindow.Prompt(owner: null, FomDatabase.GetDefaultDatabasePath());
        if (picked is null) return null;

        return new StartupDatabase(picked.Path, picked.IsNew ? picked.Password : null);
    }

    /// <summary>Reads <c>--db &lt;path&gt;</c>, which bypasses the config file entirely.</summary>
    private static string? ReadDatabaseArgument(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--db", StringComparison.OrdinalIgnoreCase)) continue;

            var candidate = args[i + 1];
            if (!string.IsNullOrWhiteSpace(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>
    /// Opens a repository, prompting for the password when the file turns out to be encrypted and
    /// one was not supplied. Returns null if the user cancelled or it could not be opened, having
    /// already reported why.
    /// </summary>
    public static SqliteFomRepository? OpenRepository(Window? owner, string path, string? password = null)
    {
        // Only prompt when the file on disk is actually encrypted; plaintext is the default, and a
        // brand-new file created with a password already has one in hand.
        if (password is null && FomDatabase.IsEncrypted(path))
        {
            string? error = null;
            while (true)
            {
                password = PasswordWindow.Prompt(
                    owner,
                    confirmMode: false,
                    prompt: $"“{Path.GetFileName(path)}” is password-protected. Enter the password to unlock it.",
                    title: "Unlock database",
                    initialError: error);

                if (password is null) return null;
                if (FomDatabase.CanOpen(path, password)) break;

                error = "Incorrect password. Please try again.";
            }
        }

        try
        {
            return new SqliteFomRepository(path, password);
        }
        catch (Exception ex)
        {
            MessageWindow.Show(owner, "HLA FOM Reader",
                "Could not open the registry database.",
                $"{path}\n\n{Describe(ex)}",
                MessageKind.Error);
            return null;
        }
    }

    /// <summary>
    /// Closes the current database and opens <paramref name="path"/> in its place, rebuilding the
    /// shell around it. Returns false and leaves the existing database open if that fails.
    /// </summary>
    public static bool SwitchDatabase(Window owner, string path)
    {
        if (string.Equals(path, CurrentDatabasePath, StringComparison.OrdinalIgnoreCase))
            return true;

        var replacement = OpenRepository(owner, path);
        if (replacement is null) return false;

        var previous = _repository;

        _repository = replacement;
        CurrentDatabasePath = path;
        AppConfig.SetLastDatabasePath(path);

        ReplaceShell(owner, replacement);

        previous?.Dispose();
        return true;
    }

    /// <summary>
    /// Closes the connection so the file can be re-keyed, runs <paramref name="mutate"/>, then
    /// reopens with <paramref name="newPassword"/> and rebuilds the shell.
    /// </summary>
    /// <remarks>
    /// SQLCipher rekeys and exports need exclusive access to the file, so the live connection has to
    /// go first. If the mutation throws, the original database is reopened with its original password
    /// rather than leaving the app with no registry at all.
    /// </remarks>
    public static bool RekeyDatabase(Window owner, Action mutate, string? currentPassword, string? newPassword)
    {
        if (CurrentDatabasePath is not { } path) return false;

        var previous = _repository;
        _repository = null;
        previous?.Dispose();

        try
        {
            mutate();
        }
        catch (Exception ex)
        {
            MessageWindow.Show(owner, "HLA FOM Reader",
                "The database could not be changed.",
                Describe(ex),
                MessageKind.Error);

            _repository = OpenRepository(owner, path, currentPassword);
            RebuildShell(owner);
            return false;
        }

        _repository = OpenRepository(owner, path, newPassword);
        RebuildShell(owner);
        return _repository is not null;
    }

    private static void RebuildShell(Window owner)
    {
        if (_repository is null)
        {
            MessageWindow.Show(owner, "HLA FOM Reader",
                "The registry database is no longer open.",
                "HLA FOM Reader will close.",
                MessageKind.Error);
            Current.Shutdown(1);
            return;
        }

        ReplaceShell(owner, _repository);
    }

    /// <summary>
    /// Builds a shell around <paramref name="repository"/> and hands the window to it, leaving the
    /// reader on the screen they were already on.
    /// </summary>
    /// <remarks>
    /// Carrying the screen across matters most for the one that causes this: changing the database
    /// or its password is done from Settings, and being thrown back to the registry afterwards
    /// reads as the app having lost its place rather than as the change having worked.
    /// </remarks>
    private static void ReplaceShell(Window owner, SqliteFomRepository repository)
    {
        var previous = owner.DataContext as MainViewModel;
        var screen = previous?.SelectedNavigation.Screen;

        var shell = new MainViewModel(repository, new DialogService());
        owner.DataContext = shell;
        shell.Initialize();

        if (screen is { } destination) shell.Navigate(destination);

        previous?.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow?.DataContext as MainViewModel)?.Dispose();

        _repository?.Dispose();
        _repository = null;
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the window alive for anything non-fatal — a malformed FOM should never close the app.
        ShowFault("An unexpected error occurred.", e.Exception);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            ShowFault("A fatal error occurred and HLAFomReader must close.", ex);
    }

    /// <summary>Reports a fault on top of the shell window.</summary>
    /// <remarks>
    /// The owner matters more than it looks. An ownerless dialog is application-modal but is not
    /// guaranteed to sit above the main window, so it can end up behind it. The window then still
    /// repaints and reports itself as responding while refusing every click — indistinguishable from
    /// a hang. Owning the dialog, and activating the window first, keeps the error visible.
    /// </remarks>
    private static void ShowFault(string headline, Exception exception)
    {
        var detail = Describe(exception);

        try
        {
            var owner = Current?.MainWindow;
            if (owner is not null && owner.IsLoaded)
            {
                if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
                owner.Activate();

                MessageWindow.Show(owner, "HLA FOM Reader", headline, detail, MessageKind.Error);
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // The shell is gone or lives on another thread; fall through to the ownerless dialog.
        }

        MessageWindow.Show(owner: null, "HLA FOM Reader", headline, detail, MessageKind.Error);
    }

    /// <summary>Unwraps the layers a faulted command adds, so the message names the real cause.</summary>
    private static string Describe(Exception exception)
    {
        var root = exception;
        while (root is AggregateException { InnerExceptions.Count: 1 } aggregate)
            root = aggregate.InnerExceptions[0];

        return root.InnerException is null
            ? $"{root.GetType().Name}: {root.Message}"
            : $"{root.GetType().Name}: {root.Message}\n\n{root.InnerException.GetType().Name}: {root.InnerException.Message}";
    }
}
