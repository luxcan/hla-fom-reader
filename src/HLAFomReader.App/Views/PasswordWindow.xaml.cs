using System.Windows;
using HLAFomReader.App.Infrastructure;

namespace HLAFomReader.App.Views;

/// <summary>
/// Modal password prompt used at startup, both to unlock an existing encrypted registry database
/// and — in confirm mode — to choose the password for a new one.
/// </summary>
/// <remarks>
/// This runs before the shell window exists, so it deliberately owns no view model and touches no
/// database: it collects a string and hands it back to the caller, which decides what it means.
/// </remarks>
public sealed partial class PasswordWindow : Window
{
    private readonly bool _confirm;

    /// <summary>The password the user accepted. Empty until <c>OK</c> has validated successfully.</summary>
    public string Password { get; private set; } = "";

    private PasswordWindow(bool confirmMode, string prompt, string title)
    {
        InitializeComponent();

        _confirm = confirmMode;
        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;

        // The confirmation box only earns its space when a new password is being chosen.
        ConfirmPanel.Visibility = confirmMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var password = Box1.Password;

        if (string.IsNullOrEmpty(password))
        {
            ShowError("Password cannot be empty.");
            return;
        }

        if (_confirm && password != Box2.Password)
        {
            ShowError("The passwords do not match.");
            return;
        }

        Password = password;
        DialogResult = true;
    }

    /// <summary>Closing from the caption button leaves <see cref="Window.DialogResult"/> null, which reads as a cancel.</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Shows the dialog and returns the entered password, or <c>null</c> if the user cancelled.
    /// </summary>
    /// <param name="owner">Window to centre on; <c>null</c> centres on the screen, which is the
    /// normal case at startup because the shell window does not exist yet.</param>
    /// <param name="confirmMode">When true a second, matching confirmation box is required.</param>
    /// <param name="prompt">Explanatory line shown above the boxes.</param>
    /// <param name="title">Caption for the title bar.</param>
    /// <param name="initialError">Message to show immediately, e.g. "That password was not accepted."
    /// after a failed unlock attempt.</param>
    public static string? Prompt(Window? owner, bool confirmMode, string prompt, string title,
                                 string? initialError = null)
    {
        var dialog = new PasswordWindow(confirmMode, prompt, title);

        if (owner is not null)
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (initialError is not null)
            dialog.ShowError(initialError);

        return ModalScrim.ShowModal(dialog) == true ? dialog.Password : null;
    }
}
