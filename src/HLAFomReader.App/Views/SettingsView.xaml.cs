using System.Windows;
using System.Windows.Controls;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.ViewModels;
using HLAFomReader.Core.Registry;
using Microsoft.Win32;

namespace HLAFomReader.App.Views;

/// <summary>
/// The Settings screen.
/// </summary>
/// <remarks>
/// The database actions sit in code-behind rather than in the view model because each of them
/// replaces the shell's whole <see cref="FrameworkElement.DataContext"/> — a window-level concern
/// that a screen's view model has no business owning, and could not do without a reference to the
/// window anyway.
/// </remarks>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private SettingsViewModel? Model => DataContext as SettingsViewModel;

    private Window? Shell => Window.GetWindow(this);

    private void OpenDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || Shell is not { } shell) return;

        var dialog = new OpenFileDialog
        {
            Title = "Open registry database",
            Filter = "HLAFomReader database (*.db)|*.db|"
                   + "SQLite database (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|"
                   + "All files (*.*)|*.*",
            InitialDirectory = model.DatabaseFolder,
            FileName = model.DatabaseName,
            CheckFileExists = true,
        };

        if (ModalScrim.ShowModal(dialog, shell) != true) return;

        App.SwitchDatabase(shell, dialog.FileName);
    }

    private void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || Shell is not { } shell) return;

        var password = PasswordWindow.Prompt(shell, confirmMode: true,
            prompt: $"Choose a password for “{model.DatabaseName}”. You will be asked for it every time "
                  + "HLAFomReader opens this database. There is no way to recover it if you forget it.",
            title: "Set database password");

        if (password is null) return;

        var path = model.DatabasePath;
        if (App.RekeyDatabase(shell, () => FomDatabase.EncryptPlaintext(path, password), null, password))
            Report(shell, "The database is now encrypted.",
                "You will be asked for this password the next time it is opened.");
    }

    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || Shell is not { } shell) return;

        var current = PasswordWindow.Prompt(shell, confirmMode: false,
            prompt: "Enter the current password.", title: "Change database password");
        if (current is null) return;

        if (!Verify(shell, model.DatabasePath, current)) return;

        var replacement = PasswordWindow.Prompt(shell, confirmMode: true,
            prompt: "Choose the new password.", title: "Change database password");
        if (replacement is null) return;

        var path = model.DatabasePath;
        if (App.RekeyDatabase(shell, () => FomDatabase.ChangePassword(path, current, replacement), current, replacement))
            Report(shell, "Password changed.",
                "The new password takes effect immediately.");
    }

    private void RemovePassword_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || Shell is not { } shell) return;

        var current = PasswordWindow.Prompt(shell, confirmMode: false,
            prompt: $"Enter the password for “{model.DatabaseName}” to decrypt it. "
                  + "The database will be stored unencrypted afterwards.",
            title: "Remove database password");
        if (current is null) return;

        if (!Verify(shell, model.DatabasePath, current)) return;

        var path = model.DatabasePath;
        if (App.RekeyDatabase(shell, () => FomDatabase.DecryptToPlaintext(path, current), current, null))
            Report(shell, "Encryption removed.",
                "The database is now stored unencrypted. Anyone who can open the file can read it.");
    }

    /// <summary>Checks a password before anything irreversible is attempted with it.</summary>
    private static bool Verify(Window shell, string path, string password)
    {
        if (FomDatabase.CanOpen(path, password)) return true;

        MessageWindow.Show(shell, "HLA FOM Reader",
            "That is not the current password.",
            "Nothing has been changed. Try again, or cancel.",
            MessageKind.Warning);

        return false;
    }

    private static void Report(Window shell, string headline, string? detail = null)
    {
        (shell.DataContext as MainViewModel)?.RefreshDatabaseState();

        MessageWindow.Show(shell, "HLA FOM Reader", headline, detail);
    }
}
