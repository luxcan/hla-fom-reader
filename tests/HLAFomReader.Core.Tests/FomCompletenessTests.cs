using System.Linq;
using HLAFomReader.Core.Merging;
using HLAFomReader.Core.Model;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// Whether a FOM stands on its own, judged on the datatypes it reaches for.
/// </summary>
public sealed class FomCompletenessTests
{
    [Fact]
    public void ADocumentThatDefinesEverythingItUsesIsComplete()
    {
        var document = Module();
        Aircraft(document).Attributes.Add(new FomAttribute { Name = "Afterburner", DataType = "RPRboolean" });
        document.DataTypes.SimpleDataTypes.Add(new SimpleDataType { Name = "RPRboolean" });

        var report = FomCompleteness.Check(document);

        Assert.True(report.IsComplete);
        Assert.Empty(report.MissingDataTypes);
        Assert.Contains("Complete", report.Summary, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A module compiled without its base names types nothing defines.
    /// </summary>
    /// <remarks>
    /// This is the shape of NETN-Physical on its own: ninety attributes, no datatype table, every
    /// type it carries defined in a module that was not included. The check reports the names, which
    /// is what tells somebody which module to go and add.
    /// </remarks>
    [Fact]
    public void AModuleWithoutItsBaseReportsTheTypesItCannotResolve()
    {
        var document = Module();
        var aircraft = Aircraft(document);
        aircraft.Attributes.Add(new FomAttribute { Name = "UniqueId", DataType = "UuidArrayOfHLAbyte16" });
        aircraft.Attributes.Add(new FomAttribute { Name = "Status", DataType = "ActiveStatusEnum8" });

        var report = FomCompleteness.Check(document);

        Assert.False(report.IsComplete);
        Assert.Equal(2, report.MissingDataTypes.Count);
        Assert.Equal(
            new[] { "ActiveStatusEnum8", "UuidArrayOfHLAbyte16" },
            report.MissingDataTypes.Select(m => m.DataType).OrderBy(x => x, System.StringComparer.Ordinal));
        Assert.Contains("A module is missing", report.Summary, System.StringComparison.Ordinal);
    }

    /// <summary>The report names who reaches for a missing type, not merely that something does.</summary>
    [Fact]
    public void AMissingTypeCarriesTheElementsThatUseIt()
    {
        var document = Module();
        var aircraft = Aircraft(document);
        aircraft.Attributes.Add(new FomAttribute { Name = "UniqueId", DataType = "Uuid" });
        aircraft.Attributes.Add(new FomAttribute { Name = "OtherId", DataType = "Uuid" });

        var missing = Assert.Single(FomCompleteness.Check(document).MissingDataTypes);

        Assert.Equal("Uuid", missing.DataType);
        Assert.Equal(2, missing.UseCount);
        Assert.Contains("HLAobjectRoot.Aircraft.UniqueId", missing.UsedBy);
    }

    /// <summary>
    /// A record field typed by a missing definition counts.
    /// </summary>
    /// <remarks>
    /// A module can define a record whose fields are typed by something only another module defines.
    /// Checking attributes alone would call that document complete while it could not be encoded.
    /// </remarks>
    [Fact]
    public void ARecordFieldReachingForAMissingTypeIsReported()
    {
        var document = Module();
        var record = new FixedRecordDataType { Name = "WorldLocationStruct" };
        record.Fields.Add(new RecordField { Name = "X", DataType = "Float64BE" });
        document.DataTypes.FixedRecordDataTypes.Add(record);

        var missing = Assert.Single(FomCompleteness.Check(document).MissingDataTypes);

        Assert.Equal("Float64BE", missing.DataType);
        Assert.Contains("WorldLocationStruct.X", missing.UsedBy);
    }

    /// <summary>A base carrying types only its extensions use is not incomplete.</summary>
    [Fact]
    public void DefinitionsNothingUsesAreNotAProblem()
    {
        var document = Module();
        document.DataTypes.SimpleDataTypes.Add(new SimpleDataType { Name = "NobodyUsesThis" });

        Assert.True(FomCompleteness.Check(document).IsComplete);
    }

    /// <summary>
    /// HLA 1.3 is exempt, because it has no datatype table to check against.
    /// </summary>
    /// <remarks>
    /// A FED carries no definitions at all, so every type it names would report as missing — a
    /// verdict of "incomplete" that says nothing about whether the FOM is whole. Whether a 1.3 entry
    /// has its types is already answered elsewhere, by whether its OMT companion was registered.
    /// </remarks>
    [Fact]
    public void AnHla13DocumentIsNotJudgedOnDatatypes()
    {
        var document = new FomDocument { Standard = FomStandard.Hla13 };
        Aircraft(document).Attributes.Add(new FomAttribute { Name = "Afterburner", DataType = "boolean" });

        var report = FomCompleteness.Check(document);

        Assert.True(report.IsComplete);
        Assert.Empty(report.MissingDataTypes);
    }

    /// <summary>
    /// A 1.3 pair registered with its OMT is exempt too, and that is the case that matters.
    /// </summary>
    /// <remarks>
    /// Gating on "does this document have a datatype table" instead of on the standard let this one
    /// through: an OMT contributes definitions, so the table is not empty, while the types the
    /// attributes name are the prose the OMT wrote rather than references to those definitions. The
    /// check condemned every 1.3 pair in the registry and the registration simply stopped happening.
    /// </remarks>
    [Fact]
    public void AnHla13PairThatCarriesDatatypesIsStillExempt()
    {
        var document = new FomDocument { Standard = FomStandard.Hla13 };
        Aircraft(document).Attributes.Add(new FomAttribute { Name = "Afterburner", DataType = "boolean" });

        // What an OMT contributes: definitions whose names are nothing like the prose above.
        document.DataTypes.SimpleDataTypes.Add(new SimpleDataType { Name = "PartySizeType" });

        Assert.True(document.SupportsDataTypes);
        Assert.True(FomCompleteness.Check(document).IsComplete);
    }

    /// <summary>Merging the missing module makes the same document complete.</summary>
    [Fact]
    public void AddingTheMissingModuleClosesTheGap()
    {
        var module = Module();
        Aircraft(module).Attributes.Add(new FomAttribute { Name = "Status", DataType = "ActiveStatusEnum8" });
        Assert.False(FomCompleteness.Check(module).IsComplete);

        var baseModule = Module();
        baseModule.DataTypes.EnumeratedDataTypes.Add(new EnumeratedDataType { Name = "ActiveStatusEnum8" });

        var merged = FomModuleMerger.Merge(new[] { baseModule, module }).Document;

        Assert.True(FomCompleteness.Check(merged).IsComplete);
    }

    /// <summary>
    /// A type the MIM defines is never missing, because the RTI always loads the MIM.
    /// </summary>
    /// <remarks>
    /// Without this the check condemns almost every real FOM: HLAunicodeString is the normal way to
    /// carry a string, and an author who uses one would be told to go and find a module the RTI was
    /// always going to supply. All 53 MIM datatypes are HLA-prefixed because IEEE 1516 reserves that
    /// prefix for what the standard itself defines, so the rule is the standard's, not a list.
    /// </remarks>
    [Fact]
    public void TypesTheMimProvidesAreNotMissing()
    {
        var document = Module();
        Aircraft(document).Attributes.Add(new FomAttribute { Name = "Callsign", DataType = "HLAunicodeString" });

        Assert.True(FomCompleteness.Check(document).IsComplete);
    }

    /// <summary>
    /// A placeholder in the dataType field is not a type name.
    /// </summary>
    /// <remarks>
    /// Real FOMs write NA where the field does not apply — both Restaurant Evolved samples do — and
    /// reading it as a type reported them as missing a module called NA, which refused a registration
    /// that had always worked.
    /// </remarks>
    [Theory]
    [InlineData("NA")]
    [InlineData("N/A")]
    [InlineData("-")]
    [InlineData("none")]
    public void APlaceholderDatatypeIsNotTreatedAsAMissingType(string placeholder)
    {
        var document = Module();
        Aircraft(document).Attributes.Add(new FomAttribute { Name = "Unused", DataType = placeholder });

        Assert.True(FomCompleteness.Check(document).IsComplete);
    }

    // ---- builders ----------------------------------------------------------------------------

    private static FomDocument Module() => new() { Standard = FomStandard.Ieee1516_2010 };

    private static FomObjectClass Aircraft(FomDocument document)
    {
        var root = new FomObjectClass { Name = "HLAobjectRoot", QualifiedName = "HLAobjectRoot" };
        var aircraft = new FomObjectClass
        {
            Name = "Aircraft",
            QualifiedName = "HLAobjectRoot.Aircraft",
            Parent = root,
        };

        root.Children.Add(aircraft);
        document.ObjectClasses.Add(root);
        return aircraft;
    }
}
