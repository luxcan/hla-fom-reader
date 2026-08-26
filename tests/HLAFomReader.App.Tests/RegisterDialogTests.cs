using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
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
