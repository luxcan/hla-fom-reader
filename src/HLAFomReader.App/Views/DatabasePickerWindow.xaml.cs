using System.Windows;
using Microsoft.Win32;
using HLAFomReader.App.Infrastructure;

namespace HLAFomReader.App.Views;

/// <summary>
/// What the user settled on in <see cref="DatabasePickerWindow"/>.
/// </summary>
/// <param name="Path">Full path of the database file to use.</param>
/// <param name="IsNew">True when the file is to be created, false when it already exists.</param>
/// <param name="Password">Password chosen for a new encrypted database, or <c>null</c> for an
/// unencrypted one. Always <c>null</c> when <paramref name="IsNew"/> is false — the caller inspects
/// the existing file and prompts for its password itself if it needs one.</param>
public sealed record DatabaseChoice(string Path, bool IsNew, string? Password);

/// <summary>
/// First-run dialog that asks which registry database HLAFomReader should use.
/// </summary>
/// <remarks>
/// Shown before the shell window exists, so it holds nothing but dialog plumbing: it never opens,
/// creates or inspects a database. All of that is the caller's job, driven by the returned
/// <see cref="DatabaseChoice"/>.
/// </remarks>
public sealed partial class DatabasePickerWindow : Window
{
    /// <summary>
    /// Shared by both file dialogs so the same file looks the same whether it is being opened or
    /// created. The generic SQLite entry is there because a registry may have been made elsewhere.
    /// </summary>
    private const string DatabaseFilter =
        "HLAFomReader database (*.db)|*.db|" +
        "SQLite database (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|" +
        "All files (*.*)|*.*";

    private readonly string _suggestedPath;

    /// <summary>What the user picked, or <c>null</c> while the dialog is still open or was cancelled.</summary>
    private DatabaseChoice? Choice { get; set; }

    private DatabasePickerWindow(string suggestedPath)
    {
        InitializeComponent();

        _suggestedPath = suggestedPath;

        // Showing the default destination up front means the user can judge it before opening a
        // save dialog, which is the moment it stops being easy to change.
        SuggestedPathText.Text = suggestedPath;
        SuggestedPathText.ToolTip = suggestedPath;
    }

    /// <summary>
    /// The folder a file dialog should start in, or an empty string when the suggested folder does
    /// not exist yet — Windows then falls back to its own last-used location rather than to nothing.
    /// </summary>
    private string SuggestedDirectory
    {
        get
        {
            var folder = System.IO.Path.GetDirectoryName(_suggestedPath);
            return !string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder) ? folder : "";
        }
    }

    private void OpenExisting_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open registry database",
            Filter = DatabaseFilter,
            InitialDirectory = SuggestedDirectory,
            CheckFileExists = true,
            CheckPathExists = true,
        };

        if (ModalScrim.ShowModal(dialog, this) != true) return;

        // No password is asked for here: whether one is needed depends on the file itself, and only
        // the caller can tell by trying to open it.
        Choice = new DatabaseChoice(dialog.FileName, IsNew: false, Password: null);
        DialogResult = true;
    }

    private void CreateNew_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Create registry database",
            Filter = DatabaseFilter,
            InitialDirectory = SuggestedDirectory,
            FileName = System.IO.Path.GetFileName(_suggestedPath),
            DefaultExt = "db",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (ModalScrim.ShowModal(dialog, this) != true) return;

        string? password = null;

        // The tick box is read only now, not when the button was clicked, so the state the user can
        // still see on this window is the state that counts.
        if (ProtectWithPassword.IsChecked == true)
        {
            password = PasswordWindow.Prompt(
                this,
                confirmMode: true,
                "Choose a password for the new database. It encrypts the file itself, and there is no way to recover it — keep it somewhere safe.",
                "Set database password");

            // Backing out of the password step abandons the creation, not the whole picker: the
            // user is returned here so they can untick the box or open an existing file instead.
            if (password is null) return;
        }

        Choice = new DatabaseChoice(dialog.FileName, IsNew: true, password);
        DialogResult = true;
    }

    /// <summary>Closing from the caption button leaves <see cref="Window.DialogResult"/> null, which reads as a cancel.</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Shows the dialog and returns the chosen database, or <c>null</c> if the user cancelled —
    /// which the caller should treat as "quit", because there is nothing to run against.
    /// </summary>
    /// <param name="owner">Window to centre on; <c>null</c> centres on the screen, which is the
    /// normal case at startup because the shell window does not exist yet.</param>
    /// <param name="suggestedPath">Full path the "create new" flow should default to.</param>
    public static DatabaseChoice? Prompt(Window? owner, string suggestedPath)
    {
        var dialog = new DatabasePickerWindow(suggestedPath);

        if (owner is not null)
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return ModalScrim.ShowModal(dialog) == true ? dialog.Choice : null;
    }
}
