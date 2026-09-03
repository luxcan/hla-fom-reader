using System;
using System.Windows.Controls;
using System.Windows.Input;
using HLAFomReader.App.ViewModels;

namespace HLAFomReader.App.Views;

/// <summary>
/// Attribute data tab: one class chosen in each FOM, and every attribute they carry with its
/// datatype on each side.
/// </summary>
/// <remarks>
/// <para>
/// The handlers below exist because a filtering ComboBox needs three behaviours WPF offers no
/// declarative hook for, and all three are view concerns the view model has no business knowing
/// about: it holds classes and a filter string, not a drop-down.
/// </para>
/// <para>
/// The filtering itself is not here. That lives in the view model, as an
/// <see cref="System.ComponentModel.ICollectionView"/> per side whose predicate always admits the
/// current selection — see <see cref="AttributeMapViewModel.ClassesA"/>.
/// </para>
/// </remarks>
public partial class AttributeMapView : UserControl
{
    public AttributeMapView() => InitializeComponent();

    /// <summary>
    /// Set by a real keystroke and consumed by the very next text change.
    /// </summary>
    /// <remarks>
    /// This flag is the whole discriminator, and it is why the filter is not simply bound to
    /// <c>ComboBox.Text</c>. That property is written by the control as well as by the user: an
    /// editable ComboBox commits a selection on every arrow key while its list is open and echoes
    /// the chosen item's name into the field. Bound, those echoes would arrive as filter text, so a
    /// user who typed "airc" to narrow two hundred classes to three would watch the open list snap
    /// back to all two hundred on the first press of Down, and every further arrow would walk
    /// document order rather than the matches.
    /// </remarks>
    private bool _typed;

    /// <summary>The picker a search is currently in progress in, and the text the user typed there.</summary>
    /// <remarks>
    /// Held because refreshing a filtered view raises a collection Reset, and an editable ComboBox
    /// answers a Reset by rewriting its field from <c>SelectedItem</c>. With a class already chosen —
    /// which is the ordinary case, since the user is searching for its replacement — that rewrite
    /// lands on every keystroke and puts the chosen class's name back over the word being typed.
    /// Admitting the selection to the filtered view stops WPF <em>dropping</em> the selection; it
    /// does not stop the control re-syncing to it. This is what puts the typed text back.
    /// </remarks>
    private ComboBox? _searching;

    private string? _typedText;

    /// <summary>
    /// Opens the drop-down when the user types into a picker, and marks the change as theirs.
    /// </summary>
    /// <remarks>
    /// With <c>IsEditable</c> set, the theme's template hands the whole field over to the text box
    /// and leaves the drop-down toggle owning only the 26 DIP chevron column, so typing no longer
    /// opens the list at all — and a filter you cannot see is worse than no filter.
    /// </remarks>
    private void OnClassPickerTextInput(object sender, TextCompositionEventArgs e)
    {
        _typed = true;
        OpenDropDown(sender);
    }

    /// <summary>Backspace and Delete narrow the filter too, and they are not text input.</summary>
    private void OnClassPickerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Back or Key.Delete)) return;

        _typed = true;
        OpenDropDown(sender);
    }

    /// <summary>
    /// Pushes what the user typed into the view model — and nothing else.
    /// </summary>
    /// <remarks>
    /// Every write to the field raises this, the control's own included. Only the ones a keystroke
    /// announced are treated as a filter; see <see cref="_typed"/>.
    /// </remarks>
    private void OnClassPickerTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not ComboBox combo || EditBox(combo) is not { } box) return;

        if (_typed)
        {
            _typed = false;
            _searching = combo;
            _typedText = box.Text;

            Filter(combo, _typedText);

            // The filter refreshed the view, and the control may have answered that by putting the
            // chosen class's name back over what was typed. Restore it in the same turn, so the
            // caret never visibly jumps.
            Restore(box);
            return;
        }

        // A change nothing here asked for. While a search is in progress that can only be the
        // control re-syncing its field, which would silently undo the user's typing.
        if (ReferenceEquals(combo, _searching)) Restore(box);
    }

    /// <summary>Puts the user's search text back if something overwrote it.</summary>
    private void Restore(TextBox box)
    {
        if (_typedText is null || string.Equals(box.Text, _typedText, StringComparison.Ordinal)) return;

        // Re-entrant only once: this raises TextChanged again with _typed false, and by then the
        // text already matches, so the guard above stops it there.
        box.Text = _typedText;
        box.CaretIndex = box.Text.Length;
    }

    /// <summary>Sets the filter belonging to whichever side raised the event.</summary>
    private void Filter(ComboBox combo, string text)
    {
        if (DataContext is not AttributeMapViewModel map) return;

        if (ReferenceEquals(combo, ClassPickerB)) map.ClassFilterB = text;
        else map.ClassFilterA = text;
    }

    private static void OpenDropDown(object sender)
    {
        if (sender is ComboBox combo && !combo.IsDropDownOpen) combo.IsDropDownOpen = true;
    }

    /// <summary>
    /// Selects the whole field when a picker takes focus, so the first keystroke replaces the class
    /// already chosen rather than appending to it.
    /// </summary>
    /// <remarks>
    /// On focus rather than on the drop-down opening, which is the same gesture as typing: selecting
    /// there would make every second keystroke wipe the first.
    /// </remarks>
    private void OnClassPickerGotFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        EditBox(sender)?.SelectAll();

    /// <summary>
    /// Puts the field back to the chosen class when the drop-down closes, and drops the filter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A user who types "airc", changes their mind and presses Escape would otherwise leave the
    /// field reading "airc" while the grid still shows whatever was chosen before — a picker that
    /// disagrees with the screen beside it.
    /// </para>
    /// <para>
    /// The filter goes with it. A search is spent once the list is closed, and leaving it set would
    /// mean the next opening showed only the handful of classes that matched a word the user typed
    /// some time ago and can no longer see.
    /// </para>
    /// <para>
    /// The text is left <b>selected</b> rather than with the caret parked after it, so the next
    /// keystroke replaces the class name instead of appending to it. Focus never leaves an editable
    /// ComboBox when its list closes, so the select-all done on focus does not happen again, and
    /// without this a user picking Chef and then typing "W" for Waiter would get "ChefW".
    /// </para>
    /// </remarks>
    private void OnClassPickerDropDownClosed(object sender, EventArgs e)
    {
        if (sender is not ComboBox combo || EditBox(combo) is not { } box) return;

        // The search is over, so the field is no longer defended against the control's own writes.
        if (ReferenceEquals(combo, _searching))
        {
            _searching = null;
            _typedText = null;
        }

        var text = (combo.SelectedItem as ObjectClassOption)?.LeafName ?? "";

        if (!string.Equals(box.Text, text, StringComparison.Ordinal)) box.Text = text;

        box.SelectAll();
        Filter(combo, "");
    }

    /// <summary>
    /// The editable ComboBox's text box, or null before the template has been applied.
    /// </summary>
    /// <remarks>
    /// Only reachable through the template, and only once that template has been instantiated —
    /// which is why every caller here is an event raised well after load rather than the constructor.
    /// </remarks>
    private static TextBox? EditBox(object sender) =>
        sender is ComboBox combo
            ? combo.Template?.FindName("PART_EditableTextBox", combo) as TextBox
            : null;
}
