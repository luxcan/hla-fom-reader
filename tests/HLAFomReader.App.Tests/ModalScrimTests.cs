using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using HLAFomReader.App.Infrastructure;
using Xunit;

namespace HLAFomReader.App.Tests;

/// <summary>
/// Dimming the window behind a modal.
/// </summary>
/// <remarks>
/// The scrim is a visual affordance rather than a safety mechanism — the modal loop is what actually
/// blocks input — so what has to be pinned is that it appears, that it always lifts, and that it
/// survives a dialog opened from another dialog. A scrim that leaks is worse than none: the shell
/// stays dimmed and looks broken with nothing on screen to explain why.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class ModalScrimTests
{
    private readonly WpfAppFixture _wpf;

    public ModalScrimTests(WpfAppFixture wpf) => _wpf = wpf;

    /// <summary>A realised, off-screen window — an adorner layer only exists once one is shown.</summary>
    private static Window ShowOffScreen()
    {
        var window = new Window
        {
            Content = new Grid(),
            Width = 400,
            Height = 300,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
        };

        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static int AdornerCount(Window window)
    {
        if (window.Content is not UIElement content) return 0;

        var layer = AdornerLayer.GetAdornerLayer(content);
        return layer?.GetAdorners(content)?.Length ?? 0;
    }

    [Fact]
    public void CoveringAWindowAddsAScrimAndDisposingLiftsIt()
    {
        _wpf.Invoke(() =>
        {
            var window = ShowOffScreen();
            try
            {
                Assert.False(ModalScrim.IsCovering(window));
                Assert.Equal(0, AdornerCount(window));

                using (ModalScrim.Cover(window))
                {
                    Assert.True(ModalScrim.IsCovering(window));
                    Assert.Equal(1, AdornerCount(window));
                }

                Assert.False(ModalScrim.IsCovering(window));
                Assert.Equal(0, AdornerCount(window));
            }
            finally
            {
                window.Close();
            }

            return true;
        });
    }

    [Fact]
    public void ADialogOpenedFromADialogKeepsTheOuterScrimUntilBothClose()
    {
        // The case a naive implementation gets wrong: the inner scope closing must not undim a
        // window the outer scope is still holding.
        _wpf.Invoke(() =>
        {
            var window = ShowOffScreen();
            try
            {
                using (ModalScrim.Cover(window))
                {
                    using (ModalScrim.Cover(window))
                    {
                        Assert.True(ModalScrim.IsCovering(window));
                        Assert.Equal(1, AdornerCount(window));
                    }

                    Assert.True(ModalScrim.IsCovering(window));
                    Assert.Equal(1, AdornerCount(window));
                }

                Assert.False(ModalScrim.IsCovering(window));
                Assert.Equal(0, AdornerCount(window));
            }
            finally
            {
                window.Close();
            }

            return true;
        });
    }

    [Fact]
    public void TheScrimIsLiftedEvenWhenTheDialogThrows()
    {
        _wpf.Invoke(() =>
        {
            var window = ShowOffScreen();
            try
            {
                // Action, not the Func<Task> overload xUnit deprecates for async work.
                Action open = () =>
                {
                    using (ModalScrim.Cover(window))
                        throw new InvalidOperationException("the dialog fell over");
                };

                Assert.Throws<InvalidOperationException>(open);

                Assert.False(ModalScrim.IsCovering(window));
                Assert.Equal(0, AdornerCount(window));
            }
            finally
            {
                window.Close();
            }

            return true;
        });
    }

    [Fact]
    public void TwoWindowsAreDimmedIndependently()
    {
        _wpf.Invoke(() =>
        {
            var first = ShowOffScreen();
            var second = ShowOffScreen();

            try
            {
                using (ModalScrim.Cover(first))
                {
                    Assert.True(ModalScrim.IsCovering(first));
                    Assert.False(ModalScrim.IsCovering(second));
                }

                Assert.False(ModalScrim.IsCovering(first));
            }
            finally
            {
                first.Close();
                second.Close();
            }

            return true;
        });
    }

    [Fact]
    public void ANullOwnerIsANoOpRatherThanAThrow()
    {
        // Owner is null during startup and shutdown; a dialog then is unowned, not a crash.
        _wpf.Invoke(() =>
        {
            using (ModalScrim.Cover(null))
                Assert.False(ModalScrim.IsCovering(null));

            return true;
        });
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        _wpf.Invoke(() =>
        {
            var window = ShowOffScreen();
            try
            {
                var token = ModalScrim.Cover(window);
                Assert.True(ModalScrim.IsCovering(window));

                token.Dispose();
                token.Dispose();

                Assert.False(ModalScrim.IsCovering(window));
                Assert.Equal(0, AdornerCount(window));
            }
            finally
            {
                window.Close();
            }

            return true;
        });
    }
}
