using System;
using System.IO;
using System.Linq;
using HLAFomReader.Core.Merging;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Registry;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// Registering a FOM that was compiled from several modules.
/// </summary>
/// <remarks>
/// The entry stores the merged result — the model a federation would actually run — so every screen
/// reads one complete document. What it was compiled from is kept alongside it as a record, because
/// a compiled FOM and a vendor-supplied one are otherwise indistinguishable in the registry list.
/// <para>
/// This used to be a set of links to the registered FOMs an entry was composed from, which made
/// sense while the merged model existed only in memory. It is now written out as a file and
/// registered as that file, so there is nothing left to link to: the modules are a note about where
/// this came from, not a route back to it.
/// </para>
/// </remarks>
public sealed class ComposedRegistrationTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"hlafomreader-composed-{Guid.NewGuid():N}.db");

    private readonly SqliteFomRepository _repository;

    public ComposedRegistrationTests() => _repository = new SqliteFomRepository(_databasePath);

    [Fact]
    public void ACompiledEntryStoresTheMergedModelNotJustOneModule()
    {
        var merged = FomModuleMerger.Merge(new[] { BaseModule(), ExtensionModule() }).Document;

        var compiled = _repository.Register(
            merged, "NETN stack", Temp("netn-stack.xml"),
            composedFrom: new[] { "rpr.xml", "netn-physical.xml" });

        // The whole point: the entry reports the federation's figures, not one module's.
        Assert.Equal(3, compiled.AttributeCount);

        // And they came back out of the database, not just off the in-memory document.
        var reloaded = _repository.LoadDocument(compiled.Id);
        Assert.Equal(3, reloaded.AttributeCount);
        Assert.Equal(
            new[] { "Afterburner", "EntityType" },
            reloaded.AllObjectClasses().Single(c => c.Name == "Aircraft").Attributes.Select(a => a.Name));
    }

    /// <summary>What it was compiled from survives, in compile order.</summary>
    [Fact]
    public void TheModuleListIsStoredAndReadBackInOrder()
    {
        var merged = FomModuleMerger.Merge(new[] { BaseModule(), ExtensionModule() }).Document;

        var compiled = _repository.Register(
            merged, "NETN stack", Temp("netn-stack.xml"),
            composedFrom: new[] { "NETN-BASE.xml", "RPR_FOM_v2.0.xml", "NETN-Physical.xml" });

        Assert.True(compiled.IsComposed);
        Assert.Equal("3 modules", compiled.CompositionBadge);

        var reloaded = _repository.ListEntries().Single(e => e.Id == compiled.Id);

        Assert.True(reloaded.IsComposed);
        Assert.Equal(
            new[] { "NETN-BASE.xml", "RPR_FOM_v2.0.xml", "NETN-Physical.xml" },
            reloaded.ComposedModules);

        // Numbered for the tooltip, because the order is the thing that matters about the list.
        Assert.Contains("1.  NETN-BASE.xml", reloaded.CompositionDetail, StringComparison.Ordinal);
        Assert.Contains("3.  NETN-Physical.xml", reloaded.CompositionDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinaryRegistrationIsNotComposed()
    {
        var plain = _repository.Register(BaseModule(), "RPR 2.0", Temp("rpr.xml"));

        Assert.False(plain.IsComposed);
        Assert.Null(plain.ComposedFrom);
        Assert.Empty(plain.ComposedModules);
        Assert.Equal("", plain.CompositionBadge);

        var reloaded = _repository.ListEntries().Single(e => e.Id == plain.Id);
        Assert.False(reloaded.IsComposed);
    }

    /// <summary>One file is a registration, not a compile, however it is described.</summary>
    [Fact]
    public void ASingleModuleIsNotACompile()
    {
        var one = _repository.Register(
            BaseModule(), "RPR 2.0", Temp("rpr.xml"), composedFrom: new[] { "rpr.xml" });

        Assert.False(one.IsComposed);
        Assert.Equal("", one.CompositionBadge);
    }

    /// <summary>
    /// A module can be unregistered even though something was compiled from it.
    /// </summary>
    /// <remarks>
    /// This is the behaviour that changed, and the reason the old links had to go. They carried
    /// <c>ON DELETE RESTRICT</c>, so the registry refused to let go of a base while anything was
    /// composed from it — correct while the composed entry was a photocopy that might need
    /// re-taking, and wrong now. A compiled FOM is a self-contained file; the modules it was built
    /// from are somebody else's business from the moment it is saved.
    /// </remarks>
    [Fact]
    public void AModuleCanBeUnregisteredAfterSomethingIsCompiledFromIt()
    {
        var module = _repository.Register(BaseModule(), "RPR 2.0", Temp("rpr.xml"));

        var merged = FomModuleMerger.Merge(new[] { BaseModule(), ExtensionModule() }).Document;
        var compiled = _repository.Register(
            merged, "NETN stack", Temp("netn-stack.xml"),
            composedFrom: new[] { "rpr.xml", "netn-physical.xml" });

        _repository.Delete(module.Id);

        var survivor = Assert.Single(_repository.ListEntries());
        Assert.Equal(compiled.Id, survivor.Id);
        Assert.Equal(new[] { "rpr.xml", "netn-physical.xml" }, survivor.ComposedModules);
    }

    // ---- fixtures ----------------------------------------------------------------------------

    /// <summary>Stands in for merged RPR 2.0: Aircraft with attributes of its own.</summary>
    private static FomDocument BaseModule()
    {
        var document = new FomDocument { Standard = FomStandard.Ieee1516_2010 };
        document.Identification.Name = "RPR 2.0";

        var root = new FomObjectClass { Name = "HLAobjectRoot", QualifiedName = "HLAobjectRoot" };
        var aircraft = new FomObjectClass
        {
            Name = "Aircraft",
            QualifiedName = "HLAobjectRoot.Aircraft",
            Parent = root,
        };

        aircraft.Attributes.Add(new FomAttribute { Name = "Afterburner", DataType = "RPRboolean" });
        aircraft.Attributes.Add(new FomAttribute { Name = "EntityType", DataType = "EntityTypeStruct" });

        root.Children.Add(aircraft);
        document.ObjectClasses.Add(root);
        return document;
    }

    /// <summary>Stands in for NETN-Physical: Aircraft as bare scaffolding under an extension.</summary>
    private static FomDocument ExtensionModule()
    {
        var document = new FomDocument { Standard = FomStandard.Ieee1516_2010 };
        document.Identification.Name = "NETN-Physical";

        var root = new FomObjectClass { Name = "HLAobjectRoot", QualifiedName = "HLAobjectRoot" };
        var aircraft = new FomObjectClass
        {
            Name = "Aircraft",
            QualifiedName = "HLAobjectRoot.Aircraft",
            Parent = root,
        };

        var netn = new FomObjectClass
        {
            Name = "NETN_Aircraft",
            QualifiedName = "HLAobjectRoot.Aircraft.NETN_Aircraft",
            Parent = aircraft,
        };

        netn.Attributes.Add(new FomAttribute { Name = "UniqueId", DataType = "UuidArrayOfHLAbyte16" });

        aircraft.Children.Add(netn);
        root.Children.Add(aircraft);
        document.ObjectClasses.Add(root);
        return document;
    }

    private string Temp(string name) => Path.Combine(Path.GetDirectoryName(_databasePath)!, $"{Guid.NewGuid():N}-{name}");

    public void Dispose()
    {
        _repository.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(_databasePath + suffix)) File.Delete(_databasePath + suffix);
        }
    }
}
