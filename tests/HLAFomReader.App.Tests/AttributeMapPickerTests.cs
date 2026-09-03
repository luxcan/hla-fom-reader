using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.ViewModels;
using HLAFomReader.App.Views;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Registry;
using HLAFomReader.Core.Reporting;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.App.Tests;

/// <summary>
/// The class pickers as controls, driven the way a user drives them.
/// </summary>
/// <remarks>
/// <para>
/// The view model tests cover the filtering rule. These cover the half that only exists once the
/// control template has been applied: an editable ComboBox whose drop-down no longer opens when you
/// type into it, because the theme's template gives the text box the whole field and leaves the
/// toggle owning only the chevron column. Three handlers in the code-behind put that right, and
/// nothing else in the suite would notice if they stopped being wired up.
/// </para>
/// <para>
/// Driven through the real routed events rather than by calling the handlers, so the XAML's
/// attribute wiring is part of what is under test.
/// </para>
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class AttributeMapPickerTests
{
    private readonly ITestOutputHelper _output;
    private readonly WpfAppFixture _wpf;

    public AttributeMapPickerTests(ITestOutputHelper output, WpfAppFixture wpf)
    {
        _output = output;
        _wpf = wpf;
    }

    /// <summary>Typing opens the drop-down, which the template otherwise leaves shut.</summary>
    [Fact]
    public void TypingOpensTheDropDown()
    {
        _wpf.Invoke(() => WithView((map, view) =>
        {
            var picker = Picker(view, "ClassPickerA");
            Assert.False(picker.IsDropDownOpen);

            TypeInto(picker, "a");

            Assert.True(picker.IsDropDownOpen, "typing did not open the list");
        }));
    }

    /// <summary>Backspace narrows the filter too, and it is not text input.</summary>
    [Fact]
    public void BackspaceOpensTheDropDown()
    {
        _wpf.Invoke(() => WithView((map, view) =>
        {
            var picker = Picker(view, "ClassPickerA");

            picker.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                new HwndSource(0, 0, 0, 0, 0, "t", IntPtr.Zero),
                0,
                Key.Back)
            { RoutedEvent = UIElement.PreviewKeyDownEvent });

            Assert.True(picker.IsDropDownOpen, "backspace did not open the list");
        }));
    }

    /// <summary>
    /// What the user types reaches the view model, and narrows that side's list alone.
    /// </summary>
    [Fact]
    public void TheTypedTextReachesTheViewModelAndNarrowsOneSide()
    {
        _wpf.Invoke(() => WithView((map, view) =>
        {
            var picker = Picker(view, "ClassPickerA");
            var before = map.ClassesA.Cast<ObjectClassOption>().Count();

            Type(picker, "hef");

            Assert.Equal("hef", map.ClassFilterA);

            var after = map.ClassesA.Cast<ObjectClassOption>().ToList();
            Assert.True(after.Count < before, "the picker did not narrow");
            Assert.Contains(after, o => o.LeafName == "Chef");

            // One view per side; B is untouched.
            Assert.Equal("", map.ClassFilterB);
            Assert.Equal(map.ClassOptionsB.Count, map.ClassesB.Cast<ObjectClassOption>().Count());

            _output.WriteLine($"{before} classes narrowed to {after.Count} by \"hef\"");
        }));
    }

    /// <summary>
    /// A text change the user did not type must never become a filter.
    /// </summary>
    /// <remarks>
    /// This is the regression that made the keyboard unusable. An editable ComboBox commits a
    /// selection on every arrow key while its list is open and echoes the chosen class's name into
    /// the field. While the filter was bound to ComboBox.Text, that echo arrived as filter text:
    /// typing "hef" to narrow two hundred classes to one and pressing Down re-expanded the open list
    /// to every class, and each further arrow walked document order, selecting and comparing classes
    /// containing no part of what was typed.
    /// </remarks>
    [Fact]
    public void AnEchoFromTheControlIsNotTreatedAsTyping()
    {
        _wpf.Invoke(() => WithView((map, view) =>
        {
            var picker = Picker(view, "ClassPickerA");
            var box = EditBox(picker)!;

            Type(picker, "hef");
            var narrowed = map.ClassesA.Cast<ObjectClassOption>().ToList();
            Assert.Single(narrowed);

            // Exactly what the ComboBox does to the field when an arrow key moves the selection.
            box.Text = "Waiter";
            Drain();

            Assert.Equal("hef", map.ClassFilterA);
            Assert.Equal(narrowed.Count, map.ClassesA.Cast<ObjectClassOption>().Count());
        }));
    }

    /// <summary>
    /// Closing the list spends the filter and leaves the field selected, so the next keystroke
    /// replaces the chosen class rather than appending to it.
    /// </summary>
    /// <remarks>
    /// Focus never leaves an editable ComboBox when its drop-down closes, so the select-all done on
    /// focus does not run again. Without this the ordinary gesture of picking Chef and then typing
    /// "W" for Waiter produced "ChefW" and a one-item list showing Chef.
    /// </remarks>
    [Fact]
    public void ClosingTheDropDownSpendsTheFilterAndSelectsTheField()
    {
        _wpf.Invoke(() => WithView((map, view) =>
        {
            var picker = Picker(view, "ClassPickerA");
            var box = EditBox(picker)!;

            Type(picker, "hef");
            Assert.Equal("hef", map.ClassFilterA);

            var chosen = map.ClassOptionsA.First(o => o.LeafName == "Chef");
            map.SelectedClassA = chosen;
            AttributeMapHarness.Pump(map.PendingWork);

            picker.IsDropDownOpen = true;
            picker.IsDropDownOpen = false;
            Drain();

            Assert.Equal("Chef", box.Text);
            Assert.Equal("", map.ClassFilterA);
            Assert.Equal(box.Text.Length, box.SelectionLength);

            // So the whole list is there for the next search ...
            Assert.Equal(map.ClassOptionsA.Count, map.ClassesA.Cast<ObjectClassOption>().Count());

            // ... and the next keystroke replaces rather than appends.
            Type(picker, "W", replaceSelection: true);
            Assert.Equal("W", map.ClassFilterA);
        }));
    }

    /// <summary>
    /// Searching for a replacement while a class is already chosen — the ordinary gesture, and the
    /// one with two separate ways to break.
    /// </summary>
    /// <remarks>
    /// <para>
    /// First, Selector drops SelectedItem the moment the selected item leaves the items collection,
    /// which the filtered view's predicate prevents by always admitting it.
    /// </para>
    /// <para>
    /// Second — and this is why the text is typed a character at a time rather than assigned — every
    /// refresh raises a collection Reset, and an editable ComboBox answers a Reset by rewriting its
    /// field from SelectedItem. Unhandled, that put "Chef" back over the search on every keystroke,
    /// so a seventeen-character search ended as "Chef" plus its last letter.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFilterMatchingNothingLeavesTheSelectionAndTheTypedTextAlone()
    {
        _wpf.Invoke(() => WithView((map, view) =>
        {
            var picker = Picker(view, "ClassPickerA");
            var box = EditBox(picker)!;

            var chosen = map.ClassOptionsA.First(o => o.LeafName == "Chef");
            map.SelectedClassA = chosen;
            AttributeMapHarness.Pump(map.PendingWork);
            Drain();

            Type(picker, "zzz-no-such-class", replaceSelection: true);

            Assert.Same(chosen, map.SelectedClassA);
            Assert.Equal("zzz-no-such-class", box.Text);
            Assert.Contains(chosen, map.ClassesA.Cast<ObjectClassOption>());
        }));
    }

    /// <summary>Both pickers exist, are editable, and have text search off.</summary>
    /// <remarks>
    /// IsTextSearchEnabled is the one that silently breaks everything: left on, WPF rewrites the
    /// field on every keystroke with its own prefix match, so "hef" never reaches Chef and the typed
    /// word is overwritten as it is typed.
    /// </remarks>
    [Fact]
    public void BothPickersAreEditableWithWpfTextSearchOff()
    {
        _wpf.Invoke(() => WithView((map, view) =>
        {
            foreach (var name in new[] { "ClassPickerA", "ClassPickerB" })
            {
                var picker = Picker(view, name);

                Assert.True(picker.IsEditable, $"{name} is not editable");
                Assert.False(picker.IsTextSearchEnabled, $"{name} still has WPF text search on");
                Assert.True(picker.StaysOpenOnEdit, $"{name} closes its list on every keystroke");

                // Left at its default, an ICollectionView's Refresh would drag the selection to the
                // top match on every keystroke, firing a comparison per letter.
                Assert.False(picker.IsSynchronizedWithCurrentItem ?? true,
                    $"{name} is synchronised with its view's current item");
            }
        }));
    }

    // ---- harness ------------------------------------------------------------------------------

    private static ComboBox Picker(FrameworkElement view, string name)
    {
        var picker = view.FindName(name) as ComboBox;
        Assert.True(picker is not null, $"{name} is missing from AttributeMapView");
        return picker!;
    }

    private static TextBox? EditBox(ComboBox combo) =>
        combo.Template?.FindName("PART_EditableTextBox", combo) as TextBox;

    /// <summary>
    /// Types into a picker the way a keyboard does: the preview event the view is wired to, then
    /// the character actually landing in the field, then the text change that follows.
    /// </summary>
    /// <param name="replaceSelection">
    /// True when the field is selected and the first character should replace it, which is what a
    /// real TextBox does.
    /// </param>
    private static void Type(ComboBox combo, string text, bool replaceSelection = false)
    {
        var box = EditBox(combo)!;
        var first = true;

        foreach (var character in text)
        {
            TypeInto(combo, character.ToString());

            if (first && replaceSelection && box.SelectionLength > 0) box.Text = character.ToString();
            else box.Text += character;

            first = false;
            Drain();
        }
    }

    /// <summary>Raises the real preview-text-input event the view is wired to.</summary>
    private static void TypeInto(ComboBox combo, string text)
    {
        var composition = new TextComposition(InputManager.Current, combo, text);

        combo.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = UIElement.PreviewTextInputEvent,
        });

        Drain();
    }

    private static void Drain()
    {
        for (var i = 0; i < 4; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// Builds the tab over a throwaway database, lays the real view out so its templates exist, and
    /// hands both to <paramref name="body"/> with the pickers filled.
    /// </summary>
    private static void WithView(Action<AttributeMapViewModel, FrameworkElement> body)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-picker-{Guid.NewGuid():N}.db");

        try
        {
            using var repository = new SqliteFomRepository(databasePath);

            foreach (var file in Directory.GetFiles(SamplesDirectory, "*1516-2010*.xml"))
                repository.Register(FomFileReader.ParseFile(file), Path.GetFileNameWithoutExtension(file), file);

            var entries = repository.ListEntries().ToList();

            var map = new AttributeMapViewModel(repository, new SilentDialogs());
            map.SetPair(
                entries.First(e => !e.FileName.Contains("v2", StringComparison.Ordinal)),
                entries.First(e => e.FileName.Contains("v2", StringComparison.Ordinal)));

            AttributeMapHarness.Pump(map.ActivateAsync(showBusy: false));

            var view = new AttributeMapView { DataContext = map };

            // Shown in a real window, off-screen, and not merely measured. A ComboBox coerces
            // IsDropDownOpen back to false until it is loaded, so a view that was only measured and
            // arranged can never open its list — and the drop-down is what half of these tests are
            // about. Showing it is also what applies the control template, and so what creates the
            // PART_EditableTextBox every handler here reads.
            var window = new Window
            {
                Content = view,
                Width = 1400,
                Height = 900,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
            };

            window.Show();
            try
            {
                window.UpdateLayout();
                Drain();

                body(map, view);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
        }
    }

    private static string SamplesDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "samples")))
                directory = directory.Parent;
            return Path.Combine(directory!.FullName, "samples");
        }
    }

    /// <summary>Answers nothing and raises nothing; these tests never open a dialog.</summary>
    private sealed class SilentDialogs : IDialogService
    {
        public string[]? OpenFiles(string title, string filter, bool multiSelect = true) => null;
        public IReadOnlyList<FomRegistrationRequest>? RequestRegistrations() => null;
        public string? SaveFile(string title, string filter, string defaultFileName, string defaultExt) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) =>
            throw new InvalidOperationException($"{title}: {message}");
        public void ShowInfo(string title, string message) { }
        public void ShowDataTypeDetail(DataTypeDetailViewModel model) { }
        public ClassExportSelection? RequestExportSelection(ExportSelectionViewModel model) => null;
    }
}
