using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.ViewModels;
using HLAFomReader.App.Views;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Registry;
using HLAFomReader.Core.Reporting;
using Xunit;

namespace HLAFomReader.App.Tests;

/// <summary>
/// The picker the Export to Excel button opens: what its ticks mean, and what the export does with
/// the three answers it can come back with — cancelled, nothing ticked, and something ticked.
/// </summary>
/// <remarks>
/// The tri-state cascade is the part worth pinning. It is the one piece of this screen a user
/// cannot verify by looking: a parent showing the indeterminate bar is <em>not</em> going into the
/// workbook, and only the count and the summary say so.
/// </remarks>
public sealed class ExportSelectionTests
{
    // ------------------------------------------------------------------- the tree and its ticks

    /// <summary>Nothing is ticked when the dialog opens.</summary>
    /// <remarks>
    /// Opening with everything ticked would make the safe answer — "just the hierarchies", which is
    /// what this button did before there was a picker — the one that takes the most work to give.
    /// </remarks>
    [Fact]
    public void NothingIsTickedToBeginWith()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant");

        Assert.Equal(0, model.SelectedCount);
        Assert.True(model.ToSelection().IsEmpty);
    }

    /// <summary>Both trees are built, with a root each and the subclasses beneath it.</summary>
    [Fact]
    public void BothTreesAreBuiltFromTheDocument()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant");

        var root = Assert.Single(model.ObjectClasses);
        Assert.Equal("ObjectRoot", root.Name);

        var meal = Assert.Single(root.Children);
        Assert.Equal("Meal", meal.Name);
        Assert.Equal("ObjectRoot.Meal", meal.QualifiedName);

        Assert.Equal("InteractionRoot", Assert.Single(model.InteractionClasses).Name);
        Assert.True(model.HasObjectClasses);
        Assert.True(model.HasInteractionClasses);
    }

    /// <summary>A class's caption counts the attributes it really has, inherited ones included.</summary>
    /// <remarks>
    /// The same rule the detail screen and the exported hierarchy counts use. A dialog promising 3
    /// attributes and a sheet delivering 1 would leave the user to work out which had lied.
    /// </remarks>
    [Fact]
    public void TheCaptionCountsInheritedMembersToo()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant");

        var root = model.ObjectClasses[0];
        var soup = root.Children[0].Children[0];

        Assert.Equal("1 attributes", root.Detail);
        Assert.Equal("3 attributes", soup.Detail);
    }

    /// <summary>Ticking a class ticks everything beneath it.</summary>
    [Fact]
    public void TickingAClassTicksItsWholeSubtree()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant");

        model.ObjectClasses[0].IsChecked = true;

        Assert.Equal(3, model.SelectedObjectCount);
        Assert.Equal(
            new[] { "ObjectRoot", "ObjectRoot.Meal", "ObjectRoot.Meal.Soup" },
            model.ToSelection().ObjectClasses.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Ticking one deep class leaves its ancestors partly ticked, and out of the selection.</summary>
    /// <remarks>
    /// The consequence of the standard cascade, and the one that would quietly export the wrong
    /// thing if it were ever got backwards: an indeterminate ancestor is a fact about its children,
    /// not a class the user asked for.
    /// </remarks>
    [Fact]
    public void AnAncestorOfATickedClassIsIndeterminateAndUnselected()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant");

        var root = model.ObjectClasses[0];
        var meal = root.Children[0];
        var soup = meal.Children[0];

        soup.IsChecked = true;

        Assert.Null(meal.IsChecked);
        Assert.Null(root.IsChecked);

        Assert.Equal(new[] { "ObjectRoot.Meal.Soup" }, model.ToSelection().ObjectClasses.ToArray());
        Assert.Equal(1, model.SelectedObjectCount);
    }

    /// <summary>
    /// A parent is selected only by ticking the parent, never by its children all being ticked.
    /// </summary>
    /// <remarks>
    /// The difference between this tree and the usual tri-state one, and it is not cosmetic. Every
    /// node here is a class somebody can ask for, so deriving a parent's tick from its children —
    /// "all mine are ticked, therefore so am I" — would put classes on the tab that nobody chose.
    /// A class with a single subclass makes it worst: ticking the subclass would silently bring the
    /// parent and all of its attributes along.
    /// </remarks>
    [Fact]
    public void AParentIsSelectedOnlyByItsOwnTick()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant");

        var meal = model.ObjectClasses[0].Children[0];

        // Soup is Meal's only child. Ticking it must not carry Meal with it.
        meal.Children[0].IsChecked = true;
        Assert.Null(meal.IsChecked);
        Assert.False(meal.IsSelected);
        Assert.Equal(new[] { "ObjectRoot.Meal.Soup" }, model.ToSelection().ObjectClasses.ToArray());

        // Ticking Meal itself does select it, and fills its branch.
        meal.IsChecked = true;
        Assert.True(meal.IsChecked);
        Assert.True(meal.IsSelected);

        // And unticking a child leaves the parent ticked, because the parent really was asked for.
        meal.Children[0].IsChecked = false;
        Assert.True(meal.IsChecked);
        Assert.Equal(new[] { "ObjectRoot.Meal" }, model.ToSelection().ObjectClasses.ToArray());
    }

    /// <summary>Unticking a class clears its whole branch.</summary>
    [Fact]
    public void UntickingAClassClearsItsBranch()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant");

        var root = model.ObjectClasses[0];

        root.IsChecked = true;
        Assert.Equal(3, model.SelectedObjectCount);

        root.IsChecked = false;
        Assert.Equal(0, model.SelectedObjectCount);
        Assert.False(root.IsChecked);
    }

    /// <summary>
    /// While a search is on, the bulk buttons reach the matches and nothing else — and say so.
    /// </summary>
    /// <remarks>
    /// Filtering a large FOM down to a handful and pressing a button captioned <b>Select all</b>
    /// used to tick every hidden class as well. Nothing on screen would have shown it; the user
    /// would have found out from the workbook.
    /// </remarks>
    [Fact]
    public void TheBulkButtonsAreBoundedByTheSearch()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant");

        Assert.Equal("Select all", model.SelectAllCaption);
        Assert.Equal("Clear", model.SelectNoneCaption);

        model.SearchText = "Soup";
        Assert.Equal("Select matches", model.SelectAllCaption);
        Assert.Equal("Clear matches", model.SelectNoneCaption);

        model.SelectAllCommand.Execute(null);

        // ObjectRoot and Meal are on screen only as the path down to Soup, and are not matches.
        Assert.Equal(new[] { "ObjectRoot.Meal.Soup" }, model.ToSelection().ObjectClasses.ToArray());
        Assert.Equal(0, model.SelectedInteractionCount);
    }

    /// <summary>Clearing while filtered leaves ticks that the filter is hiding alone.</summary>
    [Fact]
    public void ClearingWhileFilteredSparesWhatIsHidden()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant");

        model.InteractionClasses[0].IsChecked = true;
        Assert.Equal(2, model.SelectedInteractionCount);

        model.SearchText = "Soup";
        model.SelectNoneCommand.Execute(null);

        // The interaction tree matches nothing, so nothing of it was in reach.
        Assert.Equal(2, model.SelectedInteractionCount);
    }

    /// <summary>Select all and Clear reach both trees.</summary>
    [Fact]
    public void SelectAllAndClearReachBothTrees()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant");

        model.SelectAllCommand.Execute(null);
        Assert.Equal(3, model.SelectedObjectCount);
        Assert.Equal(2, model.SelectedInteractionCount);

        model.SelectNoneCommand.Execute(null);
        Assert.Equal(0, model.SelectedCount);
        Assert.True(model.ToSelection().IsEmpty);
    }

    // ------------------------------------------------------------------- search

    /// <summary>Search hides what does not match, and keeps the path down to what does.</summary>
    [Fact]
    public void SearchHidesEverythingButTheMatchAndItsPath()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant") { SearchText = "Soup" };

        var root = model.ObjectClasses[0];
        var meal = root.Children[0];

        Assert.True(root.IsVisible);
        Assert.True(meal.IsVisible);
        Assert.True(meal.Children[0].IsVisible);

        // The interaction tree matches nothing, so its root goes.
        Assert.False(model.InteractionClasses[0].IsVisible);
    }

    /// <summary>A filtered-out class keeps its tick, and comes back when the search is cleared.</summary>
    /// <remarks>
    /// The reason the filter hides rows instead of rebuilding the tree. Searching, ticking, then
    /// searching again is the ordinary way to use this dialog on a FOM with 200 classes, and a
    /// rebuild would silently throw away everything ticked before the last keystroke.
    /// </remarks>
    [Fact]
    public void SearchingDoesNotDisturbWhatIsAlreadyTicked()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant");

        model.ObjectClasses[0].Children[0].Children[0].IsChecked = true;

        model.SearchText = "Order";
        Assert.Equal(new[] { "ObjectRoot.Meal.Soup" }, model.ToSelection().ObjectClasses.ToArray());

        model.ClearSearchCommand.Execute(null);
        Assert.Equal("", model.SearchText);
        Assert.Equal(new[] { "ObjectRoot.Meal.Soup" }, model.ToSelection().ObjectClasses.ToArray());
        Assert.All(model.ObjectClasses[0].DescendantsAndSelf(), n => Assert.True(n.IsVisible));
    }

    // ------------------------------------------------------------------- the summary line

    /// <summary>The summary names the hierarchy tabs even when nothing is ticked.</summary>
    /// <remarks>
    /// The commonest way to misread this dialog is as a filter. A summary that went quiet at zero
    /// would leave that reading standing.
    /// </remarks>
    [Fact]
    public void TheSummarySaysWhatTheWorkbookWillHold()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant");

        Assert.Equal("The workbook will hold both class hierarchies — 2 tabs.", model.Summary);

        model.ObjectClasses[0].Children[0].Children[0].IsChecked = true;
        Assert.Equal(
            "The workbook will hold both class hierarchies and the attributes of 1 object class — 3 tabs.",
            model.Summary);

        model.InteractionClasses[0].IsChecked = true;
        Assert.Equal(
            "The workbook will hold both class hierarchies, the attributes of 1 object class "
            + "and the parameters of 2 interaction classes — 4 tabs.",
            model.Summary);
    }

    /// <summary>The pane headings count what is ticked against what there is.</summary>
    [Fact]
    public void ThePaneHeadingsCountTheSelection()
    {
        var model = new ExportSelectionViewModel(Tree(), "Restaurant");

        Assert.Equal("Object classes — 3", model.ObjectHeading);

        model.ObjectClasses[0].Children[0].IsChecked = true;
        Assert.Equal("Object classes — 2 of 3 selected", model.ObjectHeading);
    }

    /// <summary>A FOM with no interaction classes says so rather than showing an empty pane.</summary>
    [Fact]
    public void AnEmptyKindIsReportedRatherThanShownBlank()
    {
        var document = new FomDocument();
        document.ObjectClasses.Add(new FomObjectClass { Name = "Alone", QualifiedName = "Alone" });

        var model = new ExportSelectionViewModel(document, "Sparse");

        Assert.True(model.HasObjectClasses);
        Assert.False(model.HasInteractionClasses);
    }

    // ------------------------------------------------------------------- the export flow

    /// <summary>Cancelling the picker abandons the export before it asks where to save.</summary>
    /// <remarks>
    /// The two answers the picker can give that look alike from a distance — cancelled, and ticked
    /// nothing — must not be confused. Cancelling means no workbook at all.
    /// </remarks>
    [Fact]
    public void CancellingThePickerWritesNothing()
    {
        WithDetailScreen((detail, dialogs) =>
        {
            dialogs.ExportSelection = null;

            detail.ExportHierarchyCommand.Execute(null);

            Assert.Single(dialogs.ExportPrompts);
            Assert.Empty(dialogs.SaveSuggestions);
            Assert.Empty(dialogs.Errors);
        });
    }

    /// <summary>Ticking nothing still exports, and gives the two-tab workbook.</summary>
    [Fact]
    public void TickingNothingStillExports()
    {
        WithDetailScreen((detail, dialogs) =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"hlafomreader-export-{Guid.NewGuid():N}.xlsx");
            dialogs.ExportSelection = ClassExportSelection.None;
            dialogs.SavePath = path;

            try
            {
                detail.ExportHierarchyCommand.Execute(null);

                Assert.Empty(dialogs.Errors);
                Assert.True(File.Exists(path), "no workbook was written");
                Assert.Equal(2, SheetNames(path).Count);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });
    }

    /// <summary>A ticked class earns its own tab in the written file.</summary>
    [Fact]
    public void ATickedClassEarnsATabInTheWrittenFile()
    {
        WithDetailScreen((detail, dialogs) =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"hlafomreader-export-{Guid.NewGuid():N}.xlsx");

            var names = detail.Document!.ObjectClasses
                .SelectMany(c => c.DescendantsAndSelf())
                .Select(c => c.QualifiedName)
                .ToArray();

            dialogs.ExportSelection = new ClassExportSelection(names, null);
            dialogs.SavePath = path;

            try
            {
                detail.ExportHierarchyCommand.Execute(null);

                Assert.Empty(dialogs.Errors);
                Assert.Equal(
                    new[]
                    {
                        ClassHierarchyExporter.ObjectSheetName,
                        ClassHierarchyExporter.InteractionSheetName,
                        ClassMemberExporter.AttributeSheetName,
                    },
                    SheetNames(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });
    }

    /// <summary>The picker is opened on the FOM the screen is showing.</summary>
    [Fact]
    public void ThePickerIsOpenedOnTheFomBeingExported()
    {
        WithDetailScreen((detail, dialogs) =>
        {
            dialogs.ExportSelection = null;

            detail.ExportHierarchyCommand.Execute(null);

            Assert.Equal("Restaurant", Assert.Single(dialogs.ExportPrompts));
        });
    }

    // ------------------------------------------------------------------- fixtures and helpers

    /// <summary>
    /// Opens a real FOM detail screen over a temporary registry, with scripted dialogs.
    /// </summary>
    /// <remarks>
    /// No WPF fixture: nothing here builds a view, and the view models are deliberately free of
    /// window dependencies — which is the whole point of putting the picker behind
    /// <see cref="IDialogService"/> rather than opening it from the view model itself.
    /// </remarks>
    private static void WithDetailScreen(Action<FomDetailViewModel, RegistryHarness.ScriptedDialogs> body)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-export-{Guid.NewGuid():N}.db");

        try
        {
            using (var repository = new SqliteFomRepository(databasePath))
            {
                var sample = Path.Combine(RegistryHarness.SamplesDirectory, "RestaurantFOM-1516-2010.xml");
                var entry = repository.Register(FomFileReader.ParseFile(sample), "Restaurant", sample);

                var dialogs = new RegistryHarness.ScriptedDialogs();
                body(new FomDetailViewModel(repository, dialogs, entry), dialogs);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
        }
    }

    /// <summary>The tab captions of a written workbook, in tab order.</summary>
    private static List<string> SheetNames(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        using var part = zip.GetEntry("xl/workbook.xml")!.Open();

        var main = System.Xml.Linq.XNamespace.Get("http://schemas.openxmlformats.org/spreadsheetml/2006/main");

        return System.Xml.Linq.XDocument.Load(part).Root!
            .Descendants(main + "sheet")
            .Select(s => (string)s.Attribute("name")!)
            .ToList();
    }

    /// <summary>A three-deep object tree and a two-deep interaction tree, with attributes to count.</summary>
    private static FomDocument Tree()
    {
        var document = new FomDocument();

        var root = new FomObjectClass { Name = "ObjectRoot", QualifiedName = "ObjectRoot" };
        root.Attributes.Add(new FomAttribute { Name = "privilegeToDelete" });

        var meal = new FomObjectClass { Name = "Meal", QualifiedName = "ObjectRoot.Meal", Parent = root };
        meal.Attributes.Add(new FomAttribute { Name = "Price" });

        var soup = new FomObjectClass { Name = "Soup", QualifiedName = "ObjectRoot.Meal.Soup", Parent = meal };
        soup.Attributes.Add(new FomAttribute { Name = "Temperature" });

        meal.Children.Add(soup);
        root.Children.Add(meal);
        document.ObjectClasses.Add(root);

        var interactionRoot = new FomInteractionClass { Name = "InteractionRoot", QualifiedName = "InteractionRoot" };
        var order = new FomInteractionClass
        {
            Name = "Order",
            QualifiedName = "InteractionRoot.Order",
            Parent = interactionRoot,
        };

        interactionRoot.Children.Add(order);
        document.InteractionClasses.Add(interactionRoot);

        return document;
    }
}

/// <summary>
/// Builds the export picker for real, with the app's resource dictionaries loaded, and lays it out.
/// </summary>
/// <remarks>
/// XAML compilation cannot catch a mistyped <c>StaticResource</c> key, a binding path that does not
/// exist on the view model, or a <c>ControlTemplate</c> part that was renamed. Those surface the
/// first time somebody opens the window — which, for a dialog reached from one button on one screen,
/// could be a long way from the change that broke it.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class ExportSelectionWindowTests
{
    private readonly WpfAppFixture _wpf;

    public ExportSelectionWindowTests(WpfAppFixture wpf) => _wpf = wpf;

    /// <summary>The window realises and lays out against a real FOM, in both themes.</summary>
    [Fact]
    public void ThePickerLaysOutInBothThemes()
    {
        _wpf.Invoke(() =>
        {
            var sample = Path.Combine(RegistryHarness.SamplesDirectory, "RestaurantFOM-1516-2010.xml");
            var model = new ExportSelectionViewModel(FomFileReader.ParseFile(sample), "Restaurant");

            var starting = ThemeManager.Current;

            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeManager.Apply(theme, persist: false);

                // Off-screen and closed straight away: a Window cannot be measured until it has been
                // realised, and a visible one would steal focus from the test run.
                var window = (Window)Activator.CreateInstance(typeof(ExportSelectionWindow), nonPublic: true)!;
                window.DataContext = model;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -10000;
                window.Top = -10000;
                window.ShowActivated = false;

                window.Show();
                window.Measure(new Size(900, 700));
                window.Arrange(new Rect(0, 0, 900, 700));
                window.UpdateLayout();

                Assert.True(window.ActualWidth > 0, $"the picker did not lay out under {theme}");

                // The trees reached the screen, rather than the panes coming up empty.
                Assert.NotEmpty(FindAll<CheckBox>(window));

                window.Close();
            }

            ThemeManager.Apply(starting, persist: false);
        });
    }

    /// <summary>Ticking a class through the tree reaches the selection the exporter is handed.</summary>
    /// <remarks>
    /// The one thing the view-model tests cannot show: that the checkbox in the item template is
    /// bound to the property the cascade actually drives.
    /// </remarks>
    [Fact]
    public void TickingACheckBoxOnScreenReachesTheSelection()
    {
        _wpf.Invoke(() =>
        {
            var sample = Path.Combine(RegistryHarness.SamplesDirectory, "RestaurantFOM-1516-2010.xml");
            var model = new ExportSelectionViewModel(FomFileReader.ParseFile(sample), "Restaurant");

            var window = (Window)Activator.CreateInstance(typeof(ExportSelectionWindow), nonPublic: true)!;
            window.DataContext = model;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000;
            window.Top = -10000;
            window.ShowActivated = false;

            window.Show();
            window.Measure(new Size(900, 700));
            window.Arrange(new Rect(0, 0, 900, 700));
            window.UpdateLayout();

            try
            {
                var box = FindAll<CheckBox>(window).First(c => c.DataContext is ExportClassNode);
                var node = (ExportClassNode)box.DataContext;

                box.IsChecked = true;
                window.UpdateLayout();

                Assert.True(node.IsSelected);
                Assert.Contains(node.QualifiedName, model.ToSelection().ObjectClasses);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();

        void Walk(DependencyObject node)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T match) found.Add(match);
                Walk(child);
            }
        }

        Walk(root);
        return found;
    }
}
