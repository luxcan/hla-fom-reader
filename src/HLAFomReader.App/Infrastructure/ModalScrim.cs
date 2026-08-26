using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace HLAFomReader.App.Infrastructure;

/// <summary>
/// Dims a window for as long as a modal dialog is open over it.
/// </summary>
/// <remarks>
/// <para>
/// This is a visual affordance, not a safety mechanism. <see cref="Window.ShowDialog"/> already
/// blocks input to the owner — that is what modal means — so nothing here is what stops a click
/// landing. What it stops is the reader trying: an unchanged screen behind a dialog invites a click
/// that silently does nothing, or worse, a Windows error chime with no explanation. Dimming says
/// "this is waiting on you" before the click rather than after it.
/// </para>
/// <para>
/// Drawn into the owner's adorner layer rather than added to its visual tree, so no window has to
/// carry scrim markup, and a window added later gets this for free by routing through
/// <see cref="ShowModal"/> or <see cref="Cover"/>. It covers the window's whole content including
/// the custom title bar, since that is what the reader must not expect to be able to use.
/// </para>
/// <para>
/// Reference counted per window: a dialog opened from another dialog covers a different window, but
/// a window covered twice for any reason must not lose its scrim when only the inner scope closes.
/// </para>
/// </remarks>
public static class ModalScrim
{
    private static readonly Dictionary<Window, Entry> Active = new();

    /// <summary>
    /// Shows <paramref name="dialog"/> modally with its owner dimmed for the duration.
    /// </summary>
    /// <remarks>
    /// The entry point every app-owned modal window should use. It guarantees the scrim is lifted
    /// even when the dialog throws, which a caller pairing Cover and ShowDialog by hand can forget.
    /// </remarks>
    /// <param name="dialog">The dialog to show. Its <see cref="Window.Owner"/> is what gets dimmed.</param>
    /// <returns>Whatever <see cref="Window.ShowDialog"/> returned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dialog"/> is null.</exception>
    public static bool? ShowModal(Window dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        using (Cover(dialog.Owner))
            return dialog.ShowDialog();
    }

    /// <summary>
    /// Shows a common dialog — a file picker — with <paramref name="owner"/> dimmed for the duration.
    /// </summary>
    /// <param name="dialog">The dialog to show.</param>
    /// <param name="owner">The window to dim and centre on. Null centres on the screen.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dialog"/> is null.</exception>
    public static bool? ShowModal(CommonDialog dialog, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        using (Cover(owner))
            return owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }

    /// <summary>
    /// Dims <paramref name="owner"/> until the returned token is disposed.
    /// </summary>
    /// <remarks>
    /// For modals this cannot wrap directly — a <see cref="Microsoft.Win32.CommonDialog"/> or a
    /// <see cref="MessageBox"/>, neither of which is a <see cref="Window"/> we own.
    /// </remarks>
    /// <param name="owner">The window to dim. Null, unloaded or unrendered windows are a no-op.</param>
    public static IDisposable Cover(Window? owner) => new Token(owner);

    private static void Push(Window owner)
    {
        if (Active.TryGetValue(owner, out var entry))
        {
            entry.Depth++;
            return;
        }

        // The adorner layer sits above the window's content. A window that has not been rendered
        // yet has no layer, which is not a failure — there is nothing on screen to dim.
        if (owner.Content is not UIElement content) return;

        var layer = AdornerLayer.GetAdornerLayer(content);
        if (layer is null) return;

        var adorner = new ScrimAdorner(content);
        layer.Add(adorner);

        Active[owner] = new Entry(layer, adorner);
    }

    private static void Pop(Window owner)
    {
        if (!Active.TryGetValue(owner, out var entry)) return;

        if (--entry.Depth > 0) return;

        entry.Layer.Remove(entry.Adorner);
        Active.Remove(owner);
    }

    /// <summary>True while <paramref name="owner"/> is dimmed. Exists so this is testable.</summary>
    public static bool IsCovering(Window? owner) => owner is not null && Active.ContainsKey(owner);

    private sealed class Entry
    {
        public Entry(AdornerLayer layer, Adorner adorner)
        {
            Layer = layer;
            Adorner = adorner;
            Depth = 1;
        }

        public AdornerLayer Layer { get; }
        public Adorner Adorner { get; }
        public int Depth { get; set; }
    }

    /// <summary>Lifts the scrim when disposed. Disposing more than once is harmless.</summary>
    private sealed class Token : IDisposable
    {
        private Window? _owner;

        internal Token(Window? owner)
        {
            if (owner is null) return;

            _owner = owner;
            Push(owner);
        }

        public void Dispose()
        {
            var owner = _owner;
            if (owner is null) return;
            _owner = null;

            Pop(owner);
        }
    }

    /// <summary>A flat wash over the whole adorned element.</summary>
    /// <remarks>
    /// Hit-test visible on purpose. The modal loop is what actually blocks input, but a scrim that
    /// let the mouse through would still show hover highlights lighting up underneath it, which
    /// reads as a screen that is merely tinted rather than one that is waiting.
    /// </remarks>
    private sealed class ScrimAdorner : Adorner
    {
        /// <summary>
        /// The wash, taken from the theme rather than baked in. An adorner is drawn outside the
        /// element it covers, so there is no XAML to hang a <c>DynamicResource</c> on — the
        /// resource reference is made here instead, which keeps the wash following the theme and
        /// repaints it when the theme changes.
        /// </summary>
        /// <remarks>
        /// The same wash the busy overlay uses, so a blocked screen looks the same however it got
        /// that way rather than introducing a second vocabulary for "not now".
        /// </remarks>
        private static readonly Brush Fallback = CreateFallback();

        private static readonly DependencyProperty WashProperty = DependencyProperty.Register(
            nameof(Wash), typeof(Brush), typeof(ScrimAdorner),
            new FrameworkPropertyMetadata(Fallback, FrameworkPropertyMetadataOptions.AffectsRender));

        internal ScrimAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = true;
            SetResourceReference(WashProperty, "Scrim");
        }

        private Brush Wash => (Brush)GetValue(WashProperty);

        protected override void OnRender(DrawingContext drawingContext)
        {
            ArgumentNullException.ThrowIfNull(drawingContext);

            drawingContext.DrawRectangle(Wash, null, new Rect(AdornedElement.RenderSize));
        }

        /// <summary>What to draw before the application resources exist, e.g. under a test host.</summary>
        private static Brush CreateFallback()
        {
            var brush = new SolidColorBrush(Color.FromArgb(0xB0, 0x14, 0x18, 0x1D));
            brush.Freeze();
            return brush;
        }
    }
}
