using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HLAFomReader.App.Infrastructure;

namespace HLAFomReader.App.Views;

/// <summary>What the message is: picks the tile colour and the glyph.</summary>
public enum MessageKind
{
    Information,
    Warning,
    Error,
    Question,
}

/// <summary>Which buttons the dialog offers.</summary>
public enum MessageButtons
{
    Ok,
    OkCancel,
    YesNo,
}

/// <summary>
/// The app's own message dialog, replacing <see cref="MessageBox"/>.
/// </summary>
/// <remarks>
/// <para>
/// Not a matter of taste. A Win32 message box is drawn by the shell, not by the app, so it ignores
/// the theme entirely — in the light theme it is a grey slab with a Windows 95 icon in the middle of
/// a window that has just been carefully coloured, and in the dark theme it is worse. It is also the
/// only surface in the app that cannot honour the theme switch, which makes it the one thing that
/// visibly does not belong.
/// </para>
/// <para>
/// The static <see cref="Show"/> falls back to a real <see cref="MessageBox"/> if this window cannot
/// be built — during shutdown, or from a thread with no dispatcher. A fatal-error report has to
/// reach the screen even when the reason it is being shown is that the app is in a bad state.
/// </para>
/// </remarks>
public sealed partial class MessageWindow : Window
{
    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets codepoints, the same in both faces.</summary>
    private const string InfoGlyph = "\uE946";
    private const string WarningGlyph = "\uE7BA";
    private const string ErrorGlyph = "\uE783";
    private const string QuestionGlyph = "\uE9CE";

    /// <summary>What the caption button and Escape mean, which is never the affirmative answer.</summary>
    private MessageResult _dismissResult = MessageResult.Cancel;

    private MessageResult _primaryResult = MessageResult.Ok;

    private MessageWindow(string title, string headline, string? body, MessageKind kind, MessageButtons buttons)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        HeadlineText.Text = headline;

        if (!string.IsNullOrWhiteSpace(body))
        {
            BodyText.Text = body;
            BodyText.Visibility = Visibility.Visible;
        }

        ApplyKind(kind);
        ApplyButtons(buttons);
    }

    /// <summary>
    /// Which answer ended the dialog.
    /// </summary>
    /// <remarks>
    /// Starts at the dismissing answer and is only moved off it by a click, so every way of closing
    /// a dialog without answering it — the caption button, Escape, Alt+F4, the owner going away —
    /// reports the same thing. A Yes/No question dismissed this way reports <c>No</c>, not a third
    /// outcome that none of its buttons offer and no caller checks for.
    /// </remarks>
    public MessageResult Result { get; private set; } = MessageResult.Cancel;

    private void ApplyKind(MessageKind kind)
    {
        // The tile is a themed brush looked up live, so the dialog follows a theme switch like
        // everything else rather than baking in whatever was loaded when it was written.
        var (fill, foreground, glyph) = kind switch
        {
            MessageKind.Warning => ("StatusChanged", "StatusForeground", WarningGlyph),
            MessageKind.Error => ("ValidationError", (string?)null, ErrorGlyph),
            MessageKind.Question => ("AccentBackground", "AccentForeground", QuestionGlyph),
            _ => ("AccentBackground", "AccentForeground", InfoGlyph),
        };

        SeverityTile.SetResourceReference(Border.BackgroundProperty, fill);
        SeverityGlyph.Text = glyph;

        if (foreground is null)
        {
            // White on the error red, per the same Windows convention the close button follows.
            SeverityGlyph.Foreground = Brushes.White;
        }
        else
        {
            SeverityGlyph.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        }
    }

    private void ApplyButtons(MessageButtons buttons)
    {
        switch (buttons)
        {
            case MessageButtons.OkCancel:
                SecondaryButton.Content = "Cancel";
                SecondaryButton.Visibility = Visibility.Visible;
                PrimaryButton.Content = "OK";
                break;

            case MessageButtons.YesNo:
                SecondaryButton.Content = "No";
                SecondaryButton.Visibility = Visibility.Visible;
                PrimaryButton.Content = "Yes";
                break;

            default:
                SecondaryButton.Visibility = Visibility.Collapsed;
                PrimaryButton.Content = "OK";

                // One button: Escape and the caption button agree with it, because there is no
                // other answer to give.
                PrimaryButton.IsCancel = true;
                break;
        }

        // On a Yes/No question there is nothing to cancel to, so the caption button, Escape and
        // Alt+F4 all have to mean "No" — a dialog whose X answers something none of its buttons
        // offer is a trap. Seeding Result with it, rather than only setting it in the close
        // handler, is what makes every one of those routes agree.
        _dismissResult = buttons == MessageButtons.YesNo ? MessageResult.No : MessageResult.Cancel;
        _primaryResult = buttons == MessageButtons.YesNo ? MessageResult.Yes : MessageResult.Ok;

        Result = _dismissResult;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        Result = _primaryResult;
        DialogResult = true;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        Result = _dismissResult;
        DialogResult = false;
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        Result = _dismissResult;
        Close();
    }

    /// <summary>
    /// Shows a themed message dialog over <paramref name="owner"/>, dimming it for the duration.
    /// </summary>
    /// <param name="owner">Window to centre on and dim. Null centres on the screen, which is the
    /// normal case during startup before the shell exists.</param>
    /// <param name="title">Caption. Defaults to the product name.</param>
    /// <param name="headline">The sentence the reader is here for.</param>
    /// <param name="body">Optional detail below it — a path, an exception, the consequences.</param>
    /// <param name="kind">Severity, which picks the tile and glyph.</param>
    /// <param name="buttons">Which answers to offer.</param>
    public static MessageResult Show(
        Window? owner,
        string title,
        string headline,
        string? body = null,
        MessageKind kind = MessageKind.Information,
        MessageButtons buttons = MessageButtons.Ok)
    {
        try
        {
            var dialog = new MessageWindow(title, headline, body, kind, buttons);

            if (owner is not null && owner.IsLoaded)
                dialog.Owner = owner;
            else
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            ModalScrim.ShowModal(dialog);
            return dialog.Result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            // No dispatcher, or the application is already tearing down. Falling back is the whole
            // point: the message still has to be delivered.
            return Fallback(headline, body, title, kind, buttons);
        }
    }

    /// <summary>
    /// Splits a message written as a headline, a blank line, then the particulars, into those two
    /// halves. A message with no blank line is all headline and keeps a null body.
    /// </summary>
    /// <remarks>
    /// Part of this dialog's contract rather than a caller's private helper: it is what decides
    /// which half of a message gets the heavier weight, so it has to mean the same thing everywhere
    /// a message is raised.
    /// </remarks>
    public static (string Headline, string? Body) Split(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return ("", null);

        var text = message.Replace("\r\n", "\n").Trim();
        var split = text.IndexOf("\n\n", StringComparison.Ordinal);

        return split < 0
            ? (text, null)
            : (text[..split].Trim(), text[(split + 2)..].Trim());
    }

    private static MessageResult Fallback(
        string headline, string? body, string title, MessageKind kind, MessageButtons buttons)
    {
        var text = string.IsNullOrWhiteSpace(body) ? headline : $"{headline}\n\n{body}";

        var image = kind switch
        {
            MessageKind.Warning => MessageBoxImage.Warning,
            MessageKind.Error => MessageBoxImage.Error,
            MessageKind.Question => MessageBoxImage.Question,
            _ => MessageBoxImage.Information,
        };

        var choice = MessageBox.Show(text, title,
            buttons switch
            {
                MessageButtons.YesNo => MessageBoxButton.YesNo,
                MessageButtons.OkCancel => MessageBoxButton.OKCancel,
                _ => MessageBoxButton.OK,
            },
            image);

        return choice switch
        {
            MessageBoxResult.Yes => MessageResult.Yes,
            MessageBoxResult.No => MessageResult.No,
            MessageBoxResult.OK => MessageResult.Ok,
            _ => MessageResult.Cancel,
        };
    }
}

/// <summary>Which answer the reader gave.</summary>
public enum MessageResult
{
    Ok,
    Cancel,
    Yes,
    No,
}
