using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.ViewModels;
using HLAFomReader.App.Views;
using HLAFomReader.Core.Registry;
using Xunit;

namespace HLAFomReader.App.Tests;

/// <summary>
/// What the Registry screen does when registering or re-parsing goes wrong.
/// </summary>
/// <remarks>
/// These exist because the failures used to be silent. A file that has been moved, locked by another
/// program or edited into something that no longer parses would be caught, counted as "1 failed" in
/// the status bar, and its reason discarded — which from the user's chair is indistinguishable from
/// a button that does nothing. Every path that can fail now has to say why.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class RegistrationFailureTests
{
    private readonly WpfAppFixture _wpf;

    public RegistrationFailureTests(WpfAppFixture wpf) => _wpf = wpf;

    [Fact]
    public void AFileThatCannotBeParsedSaysWhyRatherThanFailingQuietly()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            var broken = Path.Combine(work, "broken.xml");
            File.WriteAllText(broken, "<objectModel><this is not: well formed");

            dialogs.Next = new List<FomRegistrationRequest> { new(false, broken, null, null) };
            RegistryHarness.Execute(vm.RegisterCommand);

            Assert.Empty(vm.Entries);

            // The report has to name the file and carry a reason, not just a count.
            var error = Assert.Single(dialogs.Errors);
            Assert.Contains("broken.xml", error, StringComparison.Ordinal);
            Assert.True(error.Length > "Could not register this FOM: broken.xml".Length,
                $"the failure carried no reason: {error}");
        });
    }

    [Fact]
    public void AFileHeldOpenByAnotherProgramSaysSoInsteadOfSilentlyDoingNothing()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            var sample = Path.Combine(work, "RestaurantFOM-1516-2010.xml");

            // Exclusive, which is what an editor or a sync client can hold a FOM with.
            using var _lock = new FileStream(sample, FileMode.Open, FileAccess.Read, FileShare.None);

            dialogs.Next = new List<FomRegistrationRequest> { new(false, sample, null, null) };
            RegistryHarness.Execute(vm.RegisterCommand);

            Assert.Empty(vm.Entries);
            var error = Assert.Single(dialogs.Errors);
            Assert.Contains("RestaurantFOM-1516-2010.xml", error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AReParseThatFailsSaysSoAndLeavesTheEntryAlone()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            var sample = Path.Combine(work, "RestaurantFOM-1516-2010.xml");

            dialogs.Next = new List<FomRegistrationRequest> { new(false, sample, null, null) };
            RegistryHarness.Execute(vm.RegisterCommand);

            var registered = Assert.Single(vm.Entries);
            var originalTypes = registered.DataTypeCount;

            // Break the file underneath the registration, then ask for a re-parse.
            File.WriteAllText(sample, "<objectModel><ruined");
            vm.SelectedEntry = vm.Entries.First();
            RegistryHarness.Execute(vm.ReparseCommand);

            var error = Assert.Single(dialogs.Errors);
            Assert.Contains("Re-parse", error, StringComparison.Ordinal);

            // The stored copy is still the good one — a failed re-parse must not empty the entry.
            var after = Assert.Single(vm.Entries);
            Assert.Equal(originalTypes, after.DataTypeCount);
        });
    }

    [Fact]
    public void ReParsingAPairWhoseCompanionHasGoneIsRefusedRatherThanStrippingItsDatatypes()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            var fed = Path.Combine(work, "RestaurantFOM-1.3.fed");
            var omt = Path.Combine(work, "RestaurantFOM-1.3.omt");

            dialogs.Next = new List<FomRegistrationRequest> { new(true, fed, omt, null) };
            RegistryHarness.Execute(vm.RegisterCommand);

            var pair = Assert.Single(vm.Entries);
            Assert.True(pair.IsPair, "the 1.3 pair did not record its companion");
            Assert.True(pair.DataTypeCount > 0, "the OMT contributed no datatypes");

            // The OMT is where every datatype came from. Losing it must not silently produce a
            // typeless entry, which is what re-parsing the FED alone would do.
            File.Delete(omt);
            vm.SelectedEntry = vm.Entries.First();
            RegistryHarness.Execute(vm.ReparseCommand);

            var error = Assert.Single(dialogs.Errors);
            Assert.Contains("RestaurantFOM-1.3.omt", error, StringComparison.Ordinal);

            var after = Assert.Single(vm.Entries);
            Assert.Equal(pair.DataTypeCount, after.DataTypeCount);
        });
    }

    [Fact]
    public void ReParsingKeepsTheDateTheFomWasFirstRegistered()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, repository) =>
        {
            var sample = Path.Combine(work, "RestaurantFOM-1516-2010.xml");

            RegisterFile(vm, dialogs, sample);
            var first = Assert.Single(vm.Entries).RegisteredUtc;

            Thread.Sleep(1100);
            vm.SelectedEntry = vm.Entries.First();
            RegistryHarness.Execute(vm.ReparseCommand);

            var after = Assert.Single(vm.Entries);

            // Registered is when this FOM entered the registry, not when it was last read. Resetting
            // it also reorders the list, which is what made a re-parse look like it had done nothing
            // — the row moved.
            Assert.Equal(first, after.RegisteredUtc);
            Assert.True(after.LastParsedUtc > first, "the last-parsed time did not move");
            Assert.Single(repository.ListEntries());
        });
    }

    [Fact]
    public void UnregisteringThenRegisteringTheSameFileWorks()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, repository) =>
        {
            var sample = Path.Combine(work, "RestaurantFOM-1516-2010.xml");

            RegisterFile(vm, dialogs, sample);
            Assert.Single(vm.Entries);

            dialogs.ConfirmAnswer = true;
            vm.SelectedEntry = vm.Entries.First();
            vm.UnregisterCommand.Execute(null);
            RegistryHarness.Drain();

            Assert.Empty(vm.Entries);
            Assert.Empty(repository.ListEntries());

            RegisterFile(vm, dialogs, sample);

            Assert.Single(vm.Entries);
            Assert.Single(repository.ListEntries());
            Assert.Empty(dialogs.Errors);
        });
    }

    // ---- harness ---------------------------------------------------------------------------

    private static void RegisterFile(RegistryViewModel vm, RegistryHarness.ScriptedDialogs dialogs, string path)
    {
        dialogs.Next = new List<FomRegistrationRequest> { new(false, path, null, null) };
        RegistryHarness.Execute(vm.RegisterCommand);
    }

}
