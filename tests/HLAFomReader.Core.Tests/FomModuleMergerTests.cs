using System;
using System.Collections.Generic;
using System.Linq;
using HLAFomReader.Core.Merging;
using HLAFomReader.Core.Model;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// The module merge: what a federation actually runs, assembled from the modules it loads.
/// </summary>
/// <remarks>
/// Built from documents constructed in code rather than from sample files, because the repo has no
/// modular FOM to read and the cases worth pinning are shapes, not contents. The shapes come from a
/// real one: NETN-Physical declares <c>Aircraft</c> as a bare name so it can hang
/// <c>NETN_Aircraft</c> underneath, and every attribute of <c>Aircraft</c> lives in RPR-Physical.
/// </remarks>
public sealed class FomModuleMergerTests
{
    [Fact]
    public void ScaffoldingClassInheritsTheBaseModulesAttributes()
    {
        var baseModule = Module("RPR-Physical");
        var aircraft = Class(baseModule, "HLAobjectRoot.BaseEntity.Platform.Aircraft");
        aircraft.Attributes.Add(Attribute("Afterburner", "RPRboolean"));
        aircraft.Attributes.Add(Attribute("EntityType", "EntityTypeStruct"));

        // The module under test restates Aircraft with nothing on it, exactly as NETN-Physical does.
        var extension = Module("NETN-Physical");
        var scaffold = Class(extension, "HLAobjectRoot.BaseEntity.Platform.Aircraft");
        var netnAircraft = Child(scaffold, "NETN_Aircraft");
        netnAircraft.Attributes.Add(Attribute("UniqueId", "UuidArrayOfHLAbyte16"));

        var merged = FomModuleMerger.Merge(new[] { baseModule, extension }).Document;

        var merging = Find(merged, "HLAobjectRoot.BaseEntity.Platform.Aircraft");
        Assert.Equal(new[] { "Afterburner", "EntityType" }, merging.Attributes.Select(a => a.Name));

        // And the extension is attached beneath it rather than replacing it.
        var extended = Find(merged, "HLAobjectRoot.BaseEntity.Platform.Aircraft.NETN_Aircraft");
        Assert.Equal("UniqueId", Assert.Single(extended.Attributes).Name);
        Assert.Same(merging, extended.Parent);
    }

    /// <summary>
    /// The failure this whole feature exists to prevent, stated as a number.
    /// </summary>
    /// <remarks>
    /// Read alone, the extension module reports one attribute where the federation has three. That
    /// is what made a comparison against a complete FOM report the base module's attributes as
    /// deletions somebody had authored.
    /// </remarks>
    [Fact]
    public void MergingRecoversTheAttributesAModuleAloneAppearsToLack()
    {
        var baseModule = Module("RPR-Physical");
        var aircraft = Class(baseModule, "HLAobjectRoot.Aircraft");
        aircraft.Attributes.Add(Attribute("Afterburner", "RPRboolean"));
        aircraft.Attributes.Add(Attribute("EntityType", "EntityTypeStruct"));

        var extension = Module("NETN-Physical");
        Child(Class(extension, "HLAobjectRoot.Aircraft"), "NETN_Aircraft")
            .Attributes.Add(Attribute("UniqueId", "UuidArrayOfHLAbyte16"));

        Assert.Equal(1, extension.AttributeCount);

        var result = FomModuleMerger.Merge(new[] { baseModule, extension });

        Assert.Equal(3, result.Document.AttributeCount);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void ALaterModuleAddsAttributesToAClassThatAlreadyExists()
    {
        var baseModule = Module("base");
        Class(baseModule, "HLAobjectRoot.Platform").Attributes.Add(Attribute("Spatial", "SpatialVariantStruct"));

        var extension = Module("extension");
        Class(extension, "HLAobjectRoot.Platform").Attributes.Add(Attribute("Callsign", "HLAunicodeString"));

        var result = FomModuleMerger.Merge(new[] { baseModule, extension });

        var platform = Find(result.Document, "HLAobjectRoot.Platform");
        Assert.Equal(new[] { "Spatial", "Callsign" }, platform.Attributes.Select(a => a.Name));
        Assert.Equal(1, result.ExtendedAttributes);
        Assert.Equal(0, result.AddedClasses);
    }

    /// <summary>
    /// Two modules disagreeing about what is on the wire is reported, never silently resolved.
    /// </summary>
    /// <remarks>
    /// Restating an inherited attribute is legal and common, so a repeat is not itself a problem —
    /// only a repeat that changes the datatype, which would make the merged document describe a
    /// federation neither author wrote. First writer wins so the result stays usable; the conflict
    /// is what tells somebody the module set is wrong.
    /// </remarks>
    [Fact]
    public void ContradictoryDatatypesAreReportedAndTheFirstWins()
    {
        var baseModule = Module("base");
        Class(baseModule, "HLAobjectRoot.Platform").Attributes.Add(Attribute("Callsign", "HLAunicodeString"));

        var extension = Module("extension");
        Class(extension, "HLAobjectRoot.Platform").Attributes.Add(Attribute("Callsign", "HLAASCIIstring"));

        var result = FomModuleMerger.Merge(new[] { baseModule, extension });

        var conflict = Assert.Single(result.Conflicts);
        Assert.Contains("Callsign", conflict, StringComparison.Ordinal);
        Assert.Contains("HLAunicodeString", conflict, StringComparison.Ordinal);
        Assert.Contains("HLAASCIIstring", conflict, StringComparison.Ordinal);

        var platform = Find(result.Document, "HLAobjectRoot.Platform");
        Assert.Equal("HLAunicodeString", Assert.Single(platform.Attributes).DataType);
    }

    /// <summary>Restating an attribute identically is normal and must stay quiet.</summary>
    [Fact]
    public void RestatingAnAttributeIdenticallyIsNotAConflict()
    {
        var baseModule = Module("base");
        Class(baseModule, "HLAobjectRoot.Platform").Attributes.Add(Attribute("Callsign", "HLAunicodeString"));

        var extension = Module("extension");
        Class(extension, "HLAobjectRoot.Platform").Attributes.Add(Attribute("Callsign", "HLAunicodeString"));

        var result = FomModuleMerger.Merge(new[] { baseModule, extension });

        Assert.Empty(result.Conflicts);
        Assert.Single(Find(result.Document, "HLAobjectRoot.Platform").Attributes);
    }

    /// <summary>A module that states a property the base left blank fills it in.</summary>
    [Fact]
    public void AnUnstatedPropertyIsFilledByALaterModuleButNeverOverwritten()
    {
        var baseModule = Module("base");
        var a = Attribute("Callsign", "HLAunicodeString");
        a.Sharing = null;
        a.Ownership = "NoTransfer";
        Class(baseModule, "HLAobjectRoot.Platform").Attributes.Add(a);

        var extension = Module("extension");
        var b = Attribute("Callsign", "HLAunicodeString");
        b.Sharing = "PublishSubscribe";
        b.Ownership = "DivestAcquire";
        Class(extension, "HLAobjectRoot.Platform").Attributes.Add(b);

        var merged = FomModuleMerger.Merge(new[] { baseModule, extension }).Document;
        var attribute = Assert.Single(Find(merged, "HLAobjectRoot.Platform").Attributes);

        Assert.Equal("PublishSubscribe", attribute.Sharing);   // was blank, so filled
        Assert.Equal("NoTransfer", attribute.Ownership);       // was stated, so kept
    }

    [Fact]
    public void DatatypesFromEveryModuleEndUpInOneSetOfTables()
    {
        var baseModule = Module("base");
        baseModule.DataTypes.SimpleDataTypes.Add(new SimpleDataType { Name = "RPRboolean" });
        baseModule.DataTypes.FixedRecordDataTypes.Add(new FixedRecordDataType { Name = "EntityTypeStruct" });

        var extension = Module("extension");
        extension.DataTypes.SimpleDataTypes.Add(new SimpleDataType { Name = "RPRboolean" });   // restated
        extension.DataTypes.ArrayDataTypes.Add(new ArrayDataType { Name = "UuidArrayOfHLAbyte16" });

        var result = FomModuleMerger.Merge(new[] { baseModule, extension });

        Assert.Equal(3, result.Document.DataTypeCount);
        Assert.Equal(1, result.AddedDataTypes);
        Assert.Empty(result.Conflicts);
    }

    /// <summary>The union is named after the module being registered, not after its base.</summary>
    [Fact]
    public void IdentityComesFromTheLastModule()
    {
        var merged = FomModuleMerger.Merge(new[] { Module("RPR-Physical"), Module("NETN-Physical") }).Document;
        Assert.Equal("NETN-Physical", merged.Identification.Name);
    }

    /// <summary>Every input is left exactly as it was parsed.</summary>
    [Fact]
    public void InputsAreNotMutated()
    {
        var baseModule = Module("base");
        Class(baseModule, "HLAobjectRoot.Platform").Attributes.Add(Attribute("Spatial", "SpatialVariantStruct"));

        var extension = Module("extension");
        Class(extension, "HLAobjectRoot.Platform").Attributes.Add(Attribute("Callsign", "HLAunicodeString"));

        FomModuleMerger.Merge(new[] { baseModule, extension });

        Assert.Equal(1, baseModule.AttributeCount);
        Assert.Equal(1, extension.AttributeCount);
        Assert.Single(baseModule.ObjectClasses[0].Children[0].Attributes);
    }

    [Fact]
    public void ASingleModuleMergesToItself()
    {
        var only = Module("only");
        Class(only, "HLAobjectRoot.Platform").Attributes.Add(Attribute("Spatial", "SpatialVariantStruct"));

        var result = FomModuleMerger.Merge(new[] { only });

        Assert.Equal(1, result.Document.AttributeCount);
        Assert.Equal(0, result.AddedClasses);
        Assert.Equal(0, result.ExtendedAttributes);
    }

    /// <summary>
    /// The same base arriving twice contributes once.
    /// </summary>
    /// <remarks>
    /// This is the shape a real module set has, not an edge case. NETN-Physical depends on
    /// NETN-BASE and on RPR; NETN-BASE depends on RPR too. Both paths lead to the same RPR entry, so
    /// the merge is handed it twice. Everything here matches on name — classes on qualified name,
    /// attributes within a class, datatypes across the flat type namespace — which makes absorbing
    /// a document a second time a no-op rather than a duplication, and means the caller never has to
    /// flatten the graph before asking.
    /// </remarks>
    [Fact]
    public void AbsorbingTheSameBaseTwiceChangesNothing()
    {
        var rpr = Module("RPR");
        Class(rpr, "HLAobjectRoot.Aircraft").Attributes.Add(Attribute("Afterburner", "RPRboolean"));
        rpr.DataTypes.SimpleDataTypes.Add(new SimpleDataType { Name = "RPRboolean" });

        var netnBase = Module("NETN-BASE");
        Class(netnBase, "HLAobjectRoot.NETN_Base").Attributes.Add(Attribute("UniqueId", "Uuid"));

        var physical = Module("NETN-Physical");
        Child(Class(physical, "HLAobjectRoot.Aircraft"), "NETN_Aircraft")
            .Attributes.Add(Attribute("Status", "ActiveStatusEnum8"));

        var once = FomModuleMerger.Merge(new[] { rpr, netnBase, physical });

        // RPR reached twice: once as NETN-BASE's dependency, once as NETN-Physical's.
        var twice = FomModuleMerger.Merge(new[] { rpr, netnBase, rpr, physical });

        Assert.Equal(once.Document.ObjectClassCount, twice.Document.ObjectClassCount);
        Assert.Equal(once.Document.AttributeCount, twice.Document.AttributeCount);
        Assert.Equal(once.Document.DataTypeCount, twice.Document.DataTypeCount);
        Assert.Empty(twice.Conflicts);

        Assert.Single(Find(twice.Document, "HLAobjectRoot.Aircraft").Attributes);
    }

    /// <summary>
    /// A dependency answered with a superset brings the superset, which is the point.
    /// </summary>
    /// <remarks>
    /// NETN-Physical asks for RPR-Physical_v2.0, and MAK ships no such file — RPR 2.0 arrives as one
    /// merged document carrying Base, Physical, Aggregate and Warfare together. Pointing the
    /// dependency at it is the correct answer and it brings all of RPR in, because that is what the
    /// federation loads: the RTI is given whole modules, not the parts of them something references.
    /// Trying to take only the Physical subset would need a marker saying which classes came from
    /// which SISO module, which a merged file does not carry, and would break the datatype
    /// references that reach across the type system anyway.
    /// </remarks>
    [Fact]
    public void ADependencyAnsweredWithASupersetBringsAllOfIt()
    {
        var mergedRpr = Module("SISO-STD-001.1-2015 - Real-time Platform Reference FOM");
        Class(mergedRpr, "HLAobjectRoot.Aircraft").Attributes.Add(Attribute("Afterburner", "RPRboolean"));
        Class(mergedRpr, "HLAobjectRoot.AggregateEntity").Attributes.Add(Attribute("Formation", "FormationEnum32"));
        Class(mergedRpr, "HLAobjectRoot.Munition").Attributes.Add(Attribute("LauncherFlashPresent", "RPRboolean"));

        var physical = Module("NETN-Physical");
        Child(Class(physical, "HLAobjectRoot.Aircraft"), "NETN_Aircraft")
            .Attributes.Add(Attribute("UniqueId", "Uuid"));

        var merged = FomModuleMerger.Merge(new[] { mergedRpr, physical }).Document;

        // Aggregate and Munition are no part of "Physical" and are present regardless.
        Assert.Equal(4, merged.AttributeCount);
        Assert.NotNull(Find(merged, "HLAobjectRoot.AggregateEntity"));
        Assert.NotNull(Find(merged, "HLAobjectRoot.Munition"));
    }

    [Fact]
    public void AnEmptyModuleListIsRejected() =>
        Assert.Throws<ArgumentException>(() => FomModuleMerger.Merge(Array.Empty<FomDocument>()));

    // ---- builders ---------------------------------------------------------------------------

    private static FomDocument Module(string name)
    {
        var document = new FomDocument { Standard = FomStandard.Ieee1516_2010 };
        document.Identification.Name = name;
        return document;
    }

    /// <summary>Creates the dotted path as nested classes and returns the leaf.</summary>
    private static FomObjectClass Class(FomDocument document, string qualifiedName)
    {
        FomObjectClass? parent = null;
        var path = "";

        foreach (var segment in qualifiedName.Split('.'))
        {
            path = path.Length == 0 ? segment : $"{path}.{segment}";

            var siblings = parent?.Children ?? document.ObjectClasses;
            var existing = siblings.FirstOrDefault(c => c.Name == segment);

            if (existing is null)
            {
                existing = new FomObjectClass { Name = segment, QualifiedName = path, Parent = parent };
                siblings.Add(existing);
            }

            parent = existing;
        }

        return parent!;
    }

    // ---------------------------------------------------------------- the compiled identity

    /// <summary>
    /// The compiled FOM stops asking for the modules it now contains.
    /// </summary>
    /// <remarks>
    /// A module declares what it must be loaded alongside, and the merge inherits the last module's
    /// identification wholesale. Left alone, the compiled file would tell anything reading it — this
    /// application included — to go and find modules that are already inside it.
    /// </remarks>
    [Fact]
    public void StampingDropsTheDependenciesTheCompiledFomNowSatisfies()
    {
        var compiled = Module("inherited");
        compiled.Identification.Reference =
            "Dependency: NETN-BASE; Standard: SISO-STD-001.1-2015; Dependency: RPR-Physical_v2.0";

        FomModuleMerger.StampAsCompiled(compiled, "NETN stack");

        Assert.Equal("Standard: SISO-STD-001.1-2015", compiled.Identification.Reference);
    }

    /// <summary>A citation is not a loader instruction, so it stays.</summary>
    [Fact]
    public void StampingKeepsAReferenceThatIsNotADependency()
    {
        var compiled = Module("inherited");
        compiled.Identification.Reference = "Standard: SISO-STD-001.1-2015";

        FomModuleMerger.StampAsCompiled(compiled, "NETN stack");

        Assert.Equal("Standard: SISO-STD-001.1-2015", compiled.Identification.Reference);
    }

    /// <summary>A reference list that was nothing but dependencies ends up empty, not blank.</summary>
    [Fact]
    public void StampingAwayEveryReferenceLeavesNoneRatherThanAnEmptyOne()
    {
        var compiled = Module("inherited");
        compiled.Identification.Reference = "Dependency: NETN-BASE; Dependency: RPR-Physical_v2.0";

        FomModuleMerger.StampAsCompiled(compiled, "NETN stack");

        Assert.Null(compiled.Identification.Reference);
    }

    [Fact]
    public void StampingNamesTheCompiledModel()
    {
        var compiled = Module("inherited");
        compiled.Identification.Name = "NETN-Physical";

        FomModuleMerger.StampAsCompiled(compiled, "  NETN stack  ");

        Assert.Equal("NETN stack", compiled.Identification.Name);
    }

    /// <summary>Without a name to give it, the inherited one is better than nothing.</summary>
    [Fact]
    public void StampingWithoutANameLeavesTheInheritedOne()
    {
        var compiled = Module("inherited");
        compiled.Identification.Name = "NETN-Physical";

        FomModuleMerger.StampAsCompiled(compiled, "   ");

        Assert.Equal("NETN-Physical", compiled.Identification.Name);
    }

    /// <summary>
    /// What went into the compile travels inside the file, without displacing anything.
    /// </summary>
    /// <remarks>
    /// In the use history rather than the description, because the description is something the
    /// modules said about themselves and overwriting it would lose it. File names rather than paths,
    /// so the record survives the file being moved to another machine.
    /// </remarks>
    [Fact]
    public void StampingRecordsWhatWasCompiledAndInWhatOrder()
    {
        var compiled = Module("inherited");
        compiled.Identification.Description = "The physical layer.";

        FomModuleMerger.StampAsCompiled(compiled, "NETN stack",
            new[] { "NETN-BASE.xml", "RPR_FOM_v2.0.xml", "NETN-Physical.xml" });

        var recorded = Assert.Single(compiled.Identification.UseHistory);
        Assert.Contains("3 modules", recorded, StringComparison.Ordinal);
        Assert.Contains("NETN-BASE.xml, RPR_FOM_v2.0.xml, NETN-Physical.xml", recorded, StringComparison.Ordinal);

        Assert.Equal("The physical layer.", compiled.Identification.Description);
    }

    /// <summary>One file is not a compile, so it gets no compile record.</summary>
    [Fact]
    public void StampingASingleModuleRecordsNoHistory()
    {
        var compiled = Module("inherited");

        FomModuleMerger.StampAsCompiled(compiled, "Just the one", new[] { "OnlyModule.xml" });

        Assert.Empty(compiled.Identification.UseHistory);
    }

    private static FomObjectClass Child(FomObjectClass parent, string name)
    {
        var child = new FomObjectClass
        {
            Name = name,
            QualifiedName = $"{parent.QualifiedName}.{name}",
            Parent = parent,
        };

        parent.Children.Add(child);
        return child;
    }

    private static FomAttribute Attribute(string name, string dataType) =>
        new() { Name = name, DataType = dataType };

    private static FomObjectClass Find(FomDocument document, string qualifiedName) =>
        document.AllObjectClasses().Single(c => c.QualifiedName == qualifiedName);
}
