using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.Views;
using Xunit;

namespace HLAFomReader.App.Tests;

/// <summary>
/// The app's own message dialog, which replaced <see cref="MessageBox"/>.
/// </summary>
/// <remarks>
/// A Win32 message box is drawn by the shell rather than by the app, so it was the one surface that
/// could not follow the theme — a grey slab with a system icon in the middle of a window that had
/// just been carefully coloured. These tests pin the two things that make the replacement worth
/// having: it renders in both themes, and it splits a message the way every caller writes one.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class MessageWindowTests
{
    private readonly WpfAppFixture _wpf;

    public MessageWindowTests(WpfAppFixture wpf)
    {
        _wpf = wpf;
    }

    [Theory]
    // Callers write "what happened", a blank line, then the particulars.
    [InlineData("Export failed.\n\nAccess to the path is denied.", "Export failed.", "Access to the path is denied.")]
    // Windows line endings have to split the same way, since these come from exception messages.
    [InlineData("Re-parse\r\n\r\nThe file is gone.", "Re-parse", "The file is gone.")]
    // A single sentence is all headline: an empty detail block would just be a gap.
    [InlineData("That is not the current password.", "That is not the current password.", null)]
    // One newline is a wrapped sentence, not a section break.
    [InlineData("Line one\nline two", "Line one\nline two", null)]
    // Only the first blank line splits; the rest of the detail keeps its own paragraphs.
    [InlineData("Head\n\nfirst\n\nsecond", "Head", "first\n\nsecond")]
    public void AMessageSplitsIntoAHeadlineAndItsParticulars(string message, string headline, string? body)
    {
        var split = MessageWindow.Split(message);

        Assert.Equal(headline, split.Headline);
        Assert.Equal(body, split.Body);
    }

    [Fact]
    public void AnEmptyMessageIsNotAHeadline()
    {
        Assert.Equal(("", null), MessageWindow.Split(""));
        Assert.Equal(("", null), MessageWindow.Split("   \n  "));
    }

    /// <summary>
    /// Every severity, in both themes: the tile is a themed brush, so a key that only exists in one
    /// direction would leave the dialog with no fill behind its glyph in the other.
    /// </summary>
    [Fact]
    public void EverySeverityRendersInBothThemes()
    {
        _wpf.Invoke(() =>
        {
            var starting = ThemeManager.Current;

            try
            {
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeManager.Apply(theme, persist: false);

                    foreach (var kind in Enum.GetValues<MessageKind>())
                    {
                        var window = Build("HLA FOM Reader", "Something happened.", "And here is why.",
                            kind, MessageButtons.Ok);

                        try
                        {
                            window.WindowStartupLocation = WindowStartupLocation.Manual;
                            window.Left = -10000;
                            window.Top = -10000;
                            window.ShowActivated = false;
                            window.ShowInTaskbar = false;

                            window.Show();
                            Layout(window);

                            var texts = Descendants<TextBlock>(window).Where(t => t.IsVisible).ToList();

                            Assert.Contains(texts, t => t.Text == "Something happened.");
                            Assert.Contains(texts, t => t.Text == "And here is why.");

                            // The severity tile has to be painted, not left transparent, because it
                            // is the only thing distinguishing an error from a confirmation.
                            var tile = Descendants<Border>(window)
                                .First(b => b.Width == 32d && b.Height == 32d);

                            Assert.IsType<SolidColorBrush>(tile.Background);
                            Assert.NotEqual(Colors.Transparent, ((SolidColorBrush)tile.Background).Color);
                        }
                        finally
                        {
                            window.Close();
                            Drain();
                        }
                    }
                }
            }
            finally
            {
                ThemeManager.Apply(starting, persist: false);
            }
        });
    }

    /// <summary>
    /// A dialog whose caption button answers differently from its visible buttons is a trap, so on
    /// a Yes/No question the X has to mean No rather than a third, unnamed outcome.
    /// </summary>
    [Fact]
    public void DismissingAQuestionMeansNo()
    {
        _wpf.Invoke(() =>
        {
            var window = Build("Unregister", "Remove this FOM?", null, MessageKind.Question, MessageButtons.YesNo);

            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -10000;
                window.Top = -10000;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;

                window.Show();
                Layout(window);

                var buttons = Descendants<Button>(window).Where(b => b.IsVisible).ToList();
                Assert.Contains(buttons, b => Equals(b.Content, "Yes"));
                Assert.Contains(buttons, b => Equals(b.Content, "No"));

                // Nothing has been clicked, so the dialog still reports the dismissing answer.
                Assert.Equal(MessageResult.No, ((MessageWindow)window).Result);
            }
            finally
            {
                window.Close();
                Drain();
            }
        });
    }

    /// <summary>The constructor is private because <c>Show</c> is the way in; a test may still build one.</summary>
    private static Window Build(string title, string headline, string? body, MessageKind kind, MessageButtons buttons) =>
        (Window)Activator.CreateInstance(typeof(MessageWindow),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object?[] { title, headline, body, kind, buttons }, null)!;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match) yield return match;

            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private static void Layout(Window window)
    {
        window.UpdateLayout();
        Drain();
        window.UpdateLayout();
    }

    private static void Drain()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(10);
        }
    }
}
