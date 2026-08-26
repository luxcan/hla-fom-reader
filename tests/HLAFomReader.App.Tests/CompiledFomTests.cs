using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HLAFomReader.App.Views;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Serialization;
using Xunit;

namespace HLAFomReader.App.Tests;

/// <summary>
/// Registering several 1516 modules: the model they compile to is written out as a file, and that
/// file is what the registry holds from then on.
/// </summary>
/// <remarks>
/// <para>
/// Compiling used to happen entirely in memory and register the result under the last module's path,
/// so the registry claimed a model was a file that did not contain it. Nothing looked wrong until
/// something re-read that file — a re-parse, a file-state check — at which point the entry quietly
/// became one module again, every count on screen dropping with nothing to say why.
/// </para>
/// <para>
/// The fixtures are written here rather than taken from <c>samples/</c> because the samples are all
/// variants of one FOM and therefore contradict each other on nearly every attribute. Real modules
/// do not: each carries a different part of one model, which is the case these tests are about. The
/// contradicting case gets a module of its own, further down.
/// </para>
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class CompiledFomTests
{
    private readonly WpfAppFixture _wpf;

    public CompiledFomTests(WpfAppFixture wpf) => _wpf = wpf;

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// A module holding one class and its attributes, hung off the object root.
    /// </summary>
    /// <remarks>
    /// Every datatype named here is HLA-prefixed, so the MIM accounts for it and the module is
    /// complete on its own terms. A module that named types nothing defines would be refused at
    /// registration, which is a different test.
    /// </remarks>
    private static string WriteModule(
        string folder, string fileName, string modelName,
        string className, params (string Attribute, string DataType)[] attributes)
    {
        var document = new FomDocument { Standard = FomStandard.Ieee1516_2010 };
        document.Identification.Name = modelName;

        var root = new FomObjectClass { Name = "HLAobjectRoot", Sharing = "Neither" };
        var target = new FomObjectClass { Name = className, Sharing = "PublishSubscribe" };

        foreach (var (name, dataType) in attributes)
        {
            target.Attributes.Add(new FomAttribute
            {
                Name = name,
                DataType = dataType,
                UpdateType = "Conditional",
                Ownership = "NoTransfer",
                Sharing = "PublishSubscribe",
                Transportation = "HLAreliable",
                Order = "TimeStamp",
            });
        }

        root.Children.Add(target);
        document.ObjectClasses.Add(root);

        var path = Path.Combine(folder, fileName);
        Ieee1516XmlWriter.Write(document, path);
        return path;
    }

    /// <summary>Two modules that between them describe one model, and contradict nothing.</summary>
    private static IReadOnlyList<string> Modules(string work) => new[]
    {
        WriteModule(work, "base-module.xml", "Fleet base", "Vehicle", ("Marking", "HLAASCIIstring")),
        WriteModule(work, "air-module.xml", "Fleet air", "Aircraft",
            ("Altitude", "HLAfloat32BE"), ("Heading", "HLAfloat32BE")),
    };

    private static FomRegistrationRequest CompileRequest(
        IReadOnlyList<string> modules, string? name = "Fleet") =>
        new(false, modules[^1], null, name, modules.ToArray());

    // ---------------------------------------------------------------- the happy path

    [Fact]
    public void CompilingWritesOneFileAndRegistersThatFile()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            var modules = Modules(work);
            var target = Path.Combine(work, "out", "compiled.xml");

            dialogs.Next = new List<FomRegistrationRequest> { CompileRequest(modules) };
            dialogs.SavePath = target;

            RegistryHarness.Execute(vm.RegisterCommand);

            Assert.Empty(dialogs.Errors);
            Assert.True(File.Exists(target), "the compiled FOM was never written");

            var entry = Assert.Single(vm.Entries);
            Assert.Equal(target, entry.FilePath);
            Assert.Equal("Fleet", entry.DisplayName);

            // Both modules' worth of model, not just the one the entry is filed under.
            Assert.Equal(3, entry.ObjectClassCount);
            Assert.Equal(3, entry.AttributeCount);

            var saved = FomFileReader.ParseFile(target);
            Assert.Contains(saved.AllObjectClasses(), c => c.Name == "Vehicle");
            Assert.Contains(saved.AllObjectClasses(), c => c.Name == "Aircraft");
        });
    }

    /// <summary>
    /// The registry row can say the entry was compiled, and from what.
    /// </summary>
    /// <remarks>
    /// Once saved, a FOM compiled from modules and one a vendor shipped whole are the same kind of
    /// thing — one file holding one complete model — so the list has no way to tell them apart
    /// unless the entry carries the record. Months later that is the difference between knowing
    /// where a model came from and guessing.
    /// </remarks>
    [Fact]
    public void TheEntryRemembersWhichModulesItWasCompiledFrom()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            dialogs.Next = new List<FomRegistrationRequest> { CompileRequest(Modules(work)) };
            dialogs.SavePath = Path.Combine(work, "compiled.xml");

            RegistryHarness.Execute(vm.RegisterCommand);

            var entry = Assert.Single(vm.Entries);

            Assert.True(entry.IsComposed);
            Assert.Equal("2 modules", entry.CompositionBadge);

            // File names, in compile order — not the paths they happened to be selected from, which
            // stop being true the moment somebody moves them.
            Assert.Equal(new[] { "base-module.xml", "air-module.xml" }, entry.ComposedModules);
            Assert.Contains("1.  base-module.xml", entry.CompositionDetail, StringComparison.Ordinal);
        });
    }

    /// <summary>A FOM registered from one file claims no composition.</summary>
    [Fact]
    public void AnOrdinaryRegistrationCarriesNoModuleList()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            dialogs.Next = new List<FomRegistrationRequest>
            {
                new(false, Path.Combine(work, "RestaurantFOM-1516-2010.xml"), null, null),
            };

            RegistryHarness.Execute(vm.RegisterCommand);

            var entry = Assert.Single(vm.Entries);
            Assert.False(entry.IsComposed);
            Assert.Equal("", entry.CompositionBadge);
        });
    }

    /// <summary>The name the user gave is the compiled model's own name, inside the file.</summary>
    /// <remarks>
    /// Not only the registry's label for it. The merge inherits the last module's identification, so
    /// without this the saved file would introduce itself to every other tool as whichever module
    /// happened to be last in the list.
    /// </remarks>
    [Fact]
    public void TheNameGivenBecomesTheModelsOwnName()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            var modules = Modules(work);
            var target = Path.Combine(work, "compiled.xml");

            dialogs.Next = new List<FomRegistrationRequest> { CompileRequest(modules, "Fleet 2026") };
            dialogs.SavePath = target;

            RegistryHarness.Execute(vm.RegisterCommand);

            var saved = FomFileReader.ParseFile(target);
            Assert.Equal("Fleet 2026", saved.Identification.Name);

            // And what it was built from travels with it.
            Assert.Contains(saved.Identification.UseHistory,
                entry => entry.Contains("base-module.xml", StringComparison.Ordinal)
                      && entry.Contains("air-module.xml", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// The saved file stands on its own: the modules can go and the entry is unaffected.
    /// </summary>
    /// <remarks>
    /// What "the compiled FOM is the source of truth" has to mean in practice. Re-reading the entry
    /// re-reads the file that was saved, so a module being moved, renamed or deleted afterwards
    /// cannot change what the registry says the model is.
    /// </remarks>
    [Fact]
    public void TheModulesCanBeDeletedAndTheEntryStillReReads()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            var modules = Modules(work);
            var target = Path.Combine(work, "compiled.xml");

            dialogs.Next = new List<FomRegistrationRequest> { CompileRequest(modules) };
            dialogs.SavePath = target;
            RegistryHarness.Execute(vm.RegisterCommand);

            var attributes = Assert.Single(vm.Entries).AttributeCount;
            Assert.True(attributes > 0);

            foreach (var module in modules)
                File.Delete(module);

            vm.SelectedEntry = vm.Entries.First();
            RegistryHarness.Execute(vm.ReparseCommand);

            Assert.Empty(dialogs.Errors);

            var after = Assert.Single(vm.Entries);
            Assert.Equal(attributes, after.AttributeCount);
            Assert.Equal(target, after.FilePath);
        });
    }

    /// <summary>The save prompt opens on the name the user gave the FOM.</summary>
    [Fact]
    public void TheSavePromptSuggestsTheNameTheUserGave()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            dialogs.Next = new List<FomRegistrationRequest>
            {
                CompileRequest(Modules(work), "RPR 2.0 + NETN"),
            };
            dialogs.SavePath = Path.Combine(work, "compiled.xml");

            RegistryHarness.Execute(vm.RegisterCommand);

            Assert.Equal("RPR 2.0 + NETN.xml", Assert.Single(dialogs.SaveSuggestions));
        });
    }

    // ---------------------------------------------------------------- the refusals

    /// <summary>
    /// Cancelling the save registers nothing, rather than falling back to the modules.
    /// </summary>
    /// <remarks>
    /// The fallback would be the original bug wearing a different hat: an entry pointing at one
    /// module while claiming to hold the compiled model. Declining to save is declining to register.
    /// </remarks>
    [Fact]
    public void CancellingTheSaveRegistersNothing()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            dialogs.Next = new List<FomRegistrationRequest> { CompileRequest(Modules(work)) };
            dialogs.SavePath = null;

            RegistryHarness.Execute(vm.RegisterCommand);

            Assert.Empty(vm.Entries);
            Assert.Empty(dialogs.Errors);
        });
    }

    /// <summary>A single file is registered where it lies, with no save prompt at all.</summary>
    [Fact]
    public void RegisteringOneFileNeverAsksWhereToSaveIt()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            var sample = Path.Combine(work, "RestaurantFOM-1516-2010.xml");

            dialogs.Next = new List<FomRegistrationRequest> { new(false, sample, null, null) };
            dialogs.SavePath = Path.Combine(work, "never-used.xml");

            RegistryHarness.Execute(vm.RegisterCommand);

            Assert.Empty(dialogs.SaveSuggestions);
            Assert.False(File.Exists(Path.Combine(work, "never-used.xml")));
            Assert.Equal(sample, Assert.Single(vm.Entries).FilePath);
        });
    }

    /// <summary>
    /// A failed write registers nothing and says why.
    /// </summary>
    /// <remarks>
    /// The compile succeeded and the model is sitting in memory, which is exactly the state in which
    /// it is tempting to register it from the modules anyway. That would file a model under a path
    /// holding no such model.
    /// </remarks>
    [Fact]
    public void AWriteThatFailsSaysSoAndRegistersNothing()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            var target = Path.Combine(work, "locked.xml");
            using var held = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);

            dialogs.Next = new List<FomRegistrationRequest> { CompileRequest(Modules(work)) };
            dialogs.SavePath = target;

            RegistryHarness.Execute(vm.RegisterCommand);

            Assert.Empty(vm.Entries);
            Assert.Contains("locked.xml", Assert.Single(dialogs.Errors), StringComparison.Ordinal);
        });
    }

    // ---------------------------------------------------------------- contradicting modules

    /// <summary>Two modules that disagree, so one definition will not be in the saved file.</summary>
    private static IReadOnlyList<string> ContradictingModules(string work) => new[]
    {
        WriteModule(work, "base-module.xml", "Fleet base", "Vehicle", ("Marking", "HLAASCIIstring")),
        WriteModule(work, "clash-module.xml", "Fleet clash", "Vehicle", ("Marking", "HLAunicodeString")),
    };

    /// <summary>
    /// A conflict between modules is put to the user rather than resolved behind their back.
    /// </summary>
    /// <remarks>
    /// The merge already resolves it — the earlier module wins — and until now it reported the fact
    /// to a caller that dropped it on the floor. The user was going to be handed a file missing one
    /// of the two definitions with nothing anywhere saying which, and no way to find out short of
    /// diffing the output against the module it came from.
    /// </remarks>
    [Fact]
    public void ModulesThatContradictEachOtherAreReportedBeforeAnythingIsSaved()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            dialogs.Next = new List<FomRegistrationRequest> { CompileRequest(ContradictingModules(work)) };
            dialogs.SavePath = Path.Combine(work, "compiled.xml");
            dialogs.ConfirmAnswer = false;

            RegistryHarness.Execute(vm.RegisterCommand);

            var asked = Assert.Single(dialogs.Confirmations);
            Assert.Contains("Marking", asked, StringComparison.Ordinal);

            // Declined, so nothing was written and nothing was registered.
            Assert.Empty(vm.Entries);
            Assert.False(File.Exists(Path.Combine(work, "compiled.xml")));
        });
    }

    /// <summary>Accepting the conflict saves the file with the earlier module's definition.</summary>
    [Fact]
    public void AcceptingTheConflictKeepsTheEarlierModulesDefinition()
    {
        RegistryHarness.Run(_wpf, (vm, dialogs, work, _) =>
        {
            var target = Path.Combine(work, "compiled.xml");

            dialogs.Next = new List<FomRegistrationRequest> { CompileRequest(ContradictingModules(work)) };
            dialogs.SavePath = target;
            dialogs.ConfirmAnswer = true;

            RegistryHarness.Execute(vm.RegisterCommand);

            Assert.Single(vm.Entries);

            var marking = FomFileReader.ParseFile(target)
                .AllObjectClasses()
                .SelectMany(c => c.Attributes)
                .Single(a => a.Name == "Marking");

            Assert.Equal("HLAASCIIstring", marking.DataType);
        });
    }
}
