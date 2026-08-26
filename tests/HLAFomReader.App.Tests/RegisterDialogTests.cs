using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HLAFomReader.App.Views;
using Xunit;

namespace HLAFomReader.App.Tests;

/// <summary>
/// What the registration dialog turns a file selection into.
/// </summary>
/// <remarks>
/// Reached reflectively rather than by widening the production API for a test's benefit, which is
/// how the other tests get at this window: its constructor is private because callers are meant to
/// use Prompt(), and the request builder is private because nothing but the click handler calls it.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class RegisterDialogTests
{
    private readonly WpfAppFixture _wpf;

    public RegisterDialogTests(WpfAppFixture wpf) => _wpf = wpf;

    /// <summary>
    /// Several 1516 files are the modules of one FOM, compiled in the order shown.
    /// </summary>
    /// <remarks>
    /// This used to be a question, with "register each one on its own" as the other answer. That
    /// answer produced a registry of modules, and a module read alone is not a small FOM but a
    /// misleading one — sixteen of NETN-Physical's twenty-seven classes look empty, so comparing it
    /// against a complete FOM reports the absent modules' attributes as deletions somebody authored.
    /// Nothing about the resulting entry says which kind it is, so the rule has to hold at the door.
    /// </remarks>
    [Fact]
    public void SeveralEvolvedFilesBecomeOneEntryCompiledInTheOrderGiven()
    {
        var modules = new[]
        {
            @"C:\foms\NETN-BASE.xml",
            @"C:\foms\RPR_FOM_v2.0_1516-2010.xml",
            @"C:\foms\NETN-Physical.xml",
        };

        var requests = BuildRequests(isHla13: false, evolvedPaths: modules, name: "NETN stack");

        var request = Assert.Single(requests);

        Assert.True(request.IsCompiled);
        Assert.Equal(modules, request.ModulePaths!.ToArray());
        Assert.Equal("NETN stack", request.DisplayName);

        // The entry is filed under the last module, which is where the merged model takes its
        // identity from — the same file an RTI would name the resulting FDD after.
        Assert.Equal(modules[^1], request.PrimaryPath);
    }

    /// <summary>One 1516 file is one plain registration, not a compile of one module.</summary>
    /// <remarks>
    /// A single-file entry carrying a module list would read as composed, which matters once
    /// anything reports on how an entry was assembled or notices that a base it was built from has
    /// been re-registered.
    /// </remarks>
    [Fact]
    public void OneEvolvedFileIsNotACompile()
    {
        var requests = BuildRequests(
            isHla13: false, evolvedPaths: new[] { @"C:\foms\RPR_FOM_v2.0_1516-2010.xml" }, name: null);

        var request = Assert.Single(requests);

        Assert.False(request.IsCompiled);
        Assert.Null(request.ModulePaths);
        Assert.Equal(@"C:\foms\RPR_FOM_v2.0_1516-2010.xml", request.PrimaryPath);
    }

    /// <summary>
    /// An HLA 1.3 registration never carries a module list, whatever else is selected.
    /// </summary>
    /// <remarks>
    /// Modules arrived with IEEE 1516-2010. A 1.3 federation loads one FED and that is the whole
    /// model; the FED/OMT pairing is a different operation that happens to also be a merge — two
    /// views of one model rather than several models.
    /// </remarks>
    [Fact]
    public void AnHla13RegistrationIsTheFedAndItsCompanionAndNothingElse()
    {
        var requests = BuildRequests(
            isHla13: true,
            evolvedPaths: new[] { @"C:\foms\ignored.xml", @"C:\foms\also-ignored.xml" },
            name: null,
            fedPath: @"C:\foms\Restaurant.fed",
            omtPath: @"C:\foms\Restaurant.omt");

        var request = Assert.Single(requests);

        Assert.True(request.IsHla13);
        Assert.False(request.IsCompiled);
        Assert.Null(request.ModulePaths);
        Assert.Equal(@"C:\foms\Restaurant.fed", request.PrimaryPath);
        Assert.Equal(@"C:\foms\Restaurant.omt", request.CompanionPath);
    }

    /// <summary>
    /// The dialog no longer offers to register a multi-file selection as separate entries.
    /// </summary>
    /// <remarks>
    /// Named elements are the dialog's contract with its own code-behind, so their absence is the
    /// cheapest honest check that the choice is gone rather than merely hidden — a collapsed radio
    /// button still answers <c>IsChecked</c> and would keep the old path alive.
    /// </remarks>
    [Fact]
    public void TheSeparateRegistrationChoiceIsGone()
    {
        _wpf.Invoke(() =>
        {
            var window = (Window)Activator.CreateInstance(typeof(RegisterFomWindow), nonPublic: true)!;
            window.ApplyTemplate();

            Assert.Null(window.FindName("SeparateChoice"));
            Assert.Null(window.FindName("CompileChoice"));

            // The order list stayed, because ordering the modules is still the whole input.
            Assert.NotNull(window.FindName("ModuleList"));
            Assert.NotNull(window.FindName("CompileSection"));
        });
    }

    /// <summary>
    /// A second browse adds to the list rather than replacing it, which is the only way a module
    /// list can hold files from more than one folder.
    /// </summary>
    /// <remarks>
    /// One OpenFileDialog returns files from the folder it is looking at and no other, so a list
    /// filled by a single browse is a list from a single folder however many files it holds. Module
    /// stacks are not filed that way: a vendor FOM and the NETN modules extending it arrive as
    /// separate drops. Replacing on each browse made "put them all in one folder first" a
    /// precondition of using the app, which is the app asking the user to rearrange their FOM
    /// library to suit a dialog.
    /// </remarks>
    [Fact]
    public void EachBrowseAddsToTheListInsteadOfReplacingIt()
    {
        _wpf.Invoke(() =>
        {
            var window = NewDialog();

            AddFiles(window, @"C:\vendor\RPR_FOM_v2.0_1516-2010.xml");
            AddFiles(window, @"D:\netn\NETN-BASE.xml", @"D:\netn\NETN-Physical.xml");

            Assert.Equal(
                new[]
                {
                    @"C:\vendor\RPR_FOM_v2.0_1516-2010.xml",
                    @"D:\netn\NETN-BASE.xml",
                    @"D:\netn\NETN-Physical.xml",
                },
                StagedPaths(window));
        });
    }

    /// <summary>A file already on the list is left where it is rather than staged a second time.</summary>
    /// <remarks>
    /// The merge would not mind — absorbing a module twice is a no-op by design, because dependency
    /// graphs hand it the same base repeatedly. The record of it is what breaks: the entry would
    /// carry a "2 modules" badge and a use-history line naming one file twice, a provenance claim
    /// nobody made. Easy to hit now that adding is incremental, since re-browsing a folder shows the
    /// files already taken from it.
    /// </remarks>
    [Fact]
    public void AFileAlreadyOnTheListIsNotAddedAgain()
    {
        _wpf.Invoke(() =>
        {
            var window = NewDialog();

            AddFiles(window, @"C:\foms\NETN-BASE.xml", @"C:\foms\NETN-Physical.xml");

            // Same file by a different spelling of the same path, which is what a second browse
            // through the same folder produces.
            AddFiles(window, @"C:\foms\..\foms\netn-base.XML", @"C:\foms\NETN-Aggregate.xml");

            Assert.Equal(
                new[]
                {
                    @"C:\foms\NETN-BASE.xml",
                    @"C:\foms\NETN-Physical.xml",
                    @"C:\foms\NETN-Aggregate.xml",
                },
                StagedPaths(window));
        });
    }

    /// <summary>Files come back off the list one at a time, and the rest keep their order.</summary>
    [Fact]
    public void RemovingTakesOneFileOffAndLeavesTheOrderAlone()
    {
        _wpf.Invoke(() =>
        {
            var window = NewDialog();
            var list = (ListBox)window.FindName("ModuleList")!;

            AddFiles(window, @"C:\foms\a.xml", @"C:\foms\b.xml", @"C:\foms\c.xml");

            list.SelectedIndex = 1;
            Invoke(window, "RemoveSelectedEvolved");

            Assert.Equal(new[] { @"C:\foms\a.xml", @"C:\foms\c.xml" }, StagedPaths(window));

            // The row that moved up into the removed one's place, so a run can be cleared with
            // repeated clicks rather than re-aimed after each one.
            Assert.Equal(1, list.SelectedIndex);

            Invoke(window, "RemoveSelectedEvolved");
            Invoke(window, "RemoveSelectedEvolved");

            Assert.Empty(StagedPaths(window));
            Assert.Equal(-1, list.SelectedIndex);
        });
    }

    /// <summary>Removing every file leaves the dialog as unfinished as it started.</summary>
    [Fact]
    public void AnEmptiedListBlocksRegisterAgain()
    {
        _wpf.Invoke(() =>
        {
            var window = NewDialog();
            var register = (Button)window.FindName("RegisterButton")!;

            AddFiles(window, @"C:\foms\a.xml");
            Assert.True(register.IsEnabled);

            Invoke(window, "ClearEvolved_Click", window, new RoutedEventArgs());

            Assert.Empty(StagedPaths(window));
            Assert.False(register.IsEnabled);
        });
    }

    /// <summary>The staged list is what the request is built from, in the order it ended up in.</summary>
    [Fact]
    public void WhatIsLeftOnTheListIsWhatGetsCompiled()
    {
        _wpf.Invoke(() =>
        {
            var window = NewDialog();
            var list = (ListBox)window.FindName("ModuleList")!;

            AddFiles(window, @"C:\a\one.xml", @"C:\b\two.xml");
            AddFiles(window, @"C:\c\three.xml");

            list.SelectedIndex = 1;
            Invoke(window, "RemoveSelectedEvolved");

            var staged = StagedPaths(window);
            var request = Assert.Single(BuildRequests(isHla13: false, evolvedPaths: staged, name: "Stack"));

            Assert.True(request.IsCompiled);
            Assert.Equal(new[] { @"C:\a\one.xml", @"C:\c\three.xml" }, request.ModulePaths!.ToArray());
            Assert.Equal(@"C:\c\three.xml", request.PrimaryPath);
        });
    }

    /// <summary>Adding brings the newest file into view, not just onto the end of the list.</summary>
    /// <remarks>
    /// Setting SelectedIndex does not move a ListBox's viewport, and this list shows about five rows
    /// of however many are staged. Left alone, a sixth file lands below the fold: the list looks
    /// untouched, the count note is the only sign anything happened, and the remove button — enabled,
    /// and aimed at a row nobody can see — takes off a file the user never pointed at. Cheap to lose
    /// again, since the selection line that causes it reads as complete on its own.
    /// </remarks>
    [Fact]
    public void AddingBringsTheNewestFileIntoView()
    {
        _wpf.Invoke(() =>
        {
            // Shown, not merely measured: bringing a row into view is deferred work that only runs
            // behind a presentation source, so a detached window reports a scroll that never
            // happened rather than one that did.
            var window = ShowOffScreen();

            try
            {
                var list = (ListBox)window.FindName("ModuleList")!;

                AddFiles(window, Enumerable.Range(1, 9)
                    .Select(n => $@"C:\foms\module-{n}.xml")
                    .ToArray());

                Settle(window);

                Assert.Equal(8, list.SelectedIndex);

                var scroller = FindScrollViewer(list);
                Assert.NotNull(scroller);

                Assert.True(scroller!.ViewportHeight < scroller.ExtentHeight,
                    "the list grew tall enough to show all nine files, so this no longer tests anything");

                Assert.True(scroller.VerticalOffset > 0,
                    "the newest file was selected but left below the fold");
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The dialog realised on a real presentation source, parked where nobody will see it.
    /// </summary>
    /// <remarks>
    /// Off-screen rather than hidden, and unactivated, which is how the capture tests host a window
    /// without stealing focus from whoever is running the suite. The caller closes it.
    /// </remarks>
    private static Window ShowOffScreen()
    {
        var window = NewDialog();

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowActivated = false;
        window.Left = -10000;
        window.Top = -10000;

        window.Show();
        Settle(window);

        return window;
    }

    /// <summary>Lets deferred layout work finish before anything is measured off the window.</summary>
    /// <remarks>
    /// The dispatcher pump is load-bearing. ScrollIntoView defers whenever the row it is asked for
    /// has not been generated yet, which for a virtualising list is most of them, and UpdateLayout
    /// does not pump. Without this a scroll that has merely not happened yet reads as one that never
    /// will.
    /// </remarks>
    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(20);
        }

        window.UpdateLayout();
    }


    /// <summary>The first ScrollViewer inside <paramref name="root"/>, which for a ListBox is its own.</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;

            if (FindScrollViewer(child) is { } found) return found;
        }

        return null;
    }

    /// <summary>A realised dialog, template applied so its named elements exist.</summary>
    private static Window NewDialog()
    {
        var window = (Window)Activator.CreateInstance(typeof(RegisterFomWindow), nonPublic: true)!;
        window.ApplyTemplate();
        return window;
    }

    /// <summary>
    /// Stages files the way a browse would, one call per trip through the file dialog.
    /// </summary>
    /// <remarks>
    /// Reflective because the only other way in is an OpenFileDialog, and a test cannot press one.
    /// This is the same seam VisualCaptureTests uses on this window rather than a wider production
    /// API kept open for the tests.
    /// </remarks>
    private static void AddFiles(Window window, params string[] paths)
    {
        Invoke(window, "AddEvolvedFiles", new object[] { paths });
        Invoke(window, "UpdateState");
    }

    /// <summary>The paths staged on the dialog right now, in merge order.</summary>
    private static string[] StagedPaths(Window window) =>
        ((List<string>)typeof(RegisterFomWindow)
            .GetField("_evolvedPaths", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!)
        .ToArray();

    private static void Invoke(Window window, string name, params object?[] arguments)
    {
        var method = typeof(RegisterFomWindow)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        method!.Invoke(window, arguments.Length == 0 ? null : arguments);
    }

    /// <summary>Calls the dialog's private request builder.</summary>
    private static FomRegistrationRequest[] BuildRequests(
        bool isHla13,
        IReadOnlyList<string> evolvedPaths,
        string? name,
        string fedPath = "",
        string? omtPath = null)
    {
        var method = typeof(RegisterFomWindow)
            .GetMethod("BuildRequests", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return (FomRegistrationRequest[])method!
            .Invoke(null, new object?[] { isHla13, fedPath, omtPath, evolvedPaths, name })!;
    }
}
