using System;
using System.IO;
using System.Linq;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Merging;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// An HLA 1.3 federation is described by two files and neither is complete: the <c>.fed</c> the RTI
/// loads has the structure but no types, and the <c>.omt</c> has the types but is never loaded.
/// Merging them is the only way to get a whole 1.3 model.
/// </summary>
public sealed class FomMergerTests
{
    private readonly ITestOutputHelper _output;

    public FomMergerTests(ITestOutputHelper output) => _output = output;

    private static string Samples
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "samples")))
                directory = directory.Parent;
            return Path.Combine(directory!.FullName, "samples");
        }
    }

    private static FomDocument Parse(string fileName) =>
        FomFileReader.ParseFile(Path.Combine(Samples, fileName));

    private static FomMergeResult MergeSamples() =>
        FomMerger.Merge(Parse("RestaurantFOM-1.3.fed"), Parse("RestaurantFOM-1.3.omt"));

    [Fact]
    public void TheMergedDocumentKeepsTheFedStructure()
    {
        var fed = Parse("RestaurantFOM-1.3.fed");
        var merged = MergeSamples().Document;

        Assert.Equal(FomStandard.Hla13, merged.Standard);
        Assert.Equal(fed.ObjectClassCount, merged.ObjectClassCount);
        Assert.Equal(fed.AttributeCount, merged.AttributeCount);
        Assert.Equal(fed.InteractionClassCount, merged.InteractionClassCount);
        Assert.Equal(fed.ParameterCount, merged.ParameterCount);

        // The FED root survives, complete with its routing spaces.
        Assert.Contains(merged.AllObjectClasses(), c => c.Name == "ObjectRoot");
        Assert.NotEmpty(merged.RoutingSpaces);
    }

    [Fact]
    public void TheMergedDocumentGainsTheDatatypesTheFedLacked()
    {
        var fed = Parse("RestaurantFOM-1.3.fed");
        var result = MergeSamples();

        Assert.True(fed.DataTypes.IsEmpty, "a FED has no datatypes to begin with");
        Assert.False(result.Document.DataTypes.IsEmpty, "the merge should have brought the OMT's");

        Assert.NotEmpty(result.Document.DataTypes.FixedRecordDataTypes);
        Assert.NotEmpty(result.Document.DataTypes.EnumeratedDataTypes);
        Assert.True(result.EnrichedAttributeCount > 0);

        _output.WriteLine(result.Summary);
    }

    [Fact]
    public void EveryAttributeThatTheOmtDescribesEndsUpTyped()
    {
        var merged = MergeSamples().Document;

        var attributes = merged.AllObjectClasses()
            .SelectMany(c => c.Attributes)
            // The FED root's privilegeToDelete has no OMT counterpart by design.
            .Where(a => !a.Name.Contains("privilegeToDelete", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(attributes);
        Assert.All(attributes, a => Assert.False(string.IsNullOrWhiteSpace(a.DataType),
            $"{a.QualifiedName} was not typed by the merge"));
    }

    /// <summary>
    /// The FED is what the RTI actually loads, so where the two files disagree the FED must win.
    /// </summary>
    [Fact]
    public void TheFedWinsOnTransportationAndOrder()
    {
        var fed = Parse("RestaurantFOM-1.3.fed");
        var merged = MergeSamples().Document;

        foreach (var fedAttribute in fed.AllObjectClasses().SelectMany(c => c.Attributes)
                     .Where(a => a.Transportation is not null))
        {
            var mergedAttribute = merged.AllObjectClasses()
                .SelectMany(c => c.Attributes)
                .Single(a => a.QualifiedName == fedAttribute.QualifiedName);

            Assert.Equal(fedAttribute.Transportation, mergedAttribute.Transportation);
            Assert.Equal(fedAttribute.Order, mergedAttribute.Order);
            Assert.Equal(fedAttribute.RoutingSpace, mergedAttribute.RoutingSpace);
        }
    }

    [Fact]
    public void MergingDoesNotMutateEitherInput()
    {
        var fed = Parse("RestaurantFOM-1.3.fed");
        var omt = Parse("RestaurantFOM-1.3.omt");

        FomMerger.Merge(fed, omt);

        // The caller may still want the originals; the merge must work on a copy.
        Assert.True(fed.DataTypes.IsEmpty, "the FED was mutated by the merge");
        Assert.All(fed.AllObjectClasses().SelectMany(c => c.Attributes),
            a => Assert.Null(a.DataType));
    }

    [Fact]
    public void TheMergedPairIsRicherThanEitherFileAlone()
    {
        var fed = Parse("RestaurantFOM-1.3.fed");
        var omt = Parse("RestaurantFOM-1.3.omt");
        var merged = MergeSamples().Document;

        var comparer = new FomComparer();
        var options = new ComparisonOptions { Depth = ComparisonDepth.Full };

        // Against the FED, the merge adds the whole datatype world.
        Assert.False(comparer.Compare(fed, merged, options).AreIdentical);

        // Against the OMT, the merge adds the FED's routing spaces and transport/order.
        Assert.False(comparer.Compare(omt, merged, options).AreIdentical);

        Assert.True(merged.DataTypeCount == omt.DataTypeCount);
        Assert.True(merged.RoutingSpaces.Count == fed.RoutingSpaces.Count);
    }

    [Fact]
    public void AMatchedPairReportsNoStructuralMismatches()
    {
        var result = MergeSamples();

        foreach (var name in result.UnmatchedInFed) _output.WriteLine("fed only: " + name);
        foreach (var name in result.UnmatchedInOmt) _output.WriteLine("omt only: " + name);

        // The two sample files describe the same federation, so nothing should be left over —
        // and the OMT side especially, because that would mean the pair had drifted apart.
        Assert.Empty(result.UnmatchedInOmt);
    }

    [Fact]
    public void AMismatchedPairIsReportedRatherThanSilentlyAccepted()
    {
        var fed = Parse("RestaurantFOM-1.3.fed");
        var omt = Parse("RestaurantFOM-1.3.omt");

        // Pretend the OMT describes a class the FED has never heard of.
        var stray = new FomObjectClass { Name = "Sommelier", QualifiedName = "Sommelier" };
        stray.Attributes.Add(new FomAttribute
        {
            Name = "WineList",
            QualifiedName = "Sommelier.WineList",
            DataType = "HLAunicodeString",
        });
        omt.ObjectClasses.Add(stray);

        var result = FomMerger.Merge(fed, omt);

        Assert.NotEmpty(result.UnmatchedInOmt);
        Assert.True(result.HasMismatches);
        Assert.Contains(result.UnmatchedInOmt, n => n.Contains("Sommelier", StringComparison.Ordinal));
        Assert.Contains(result.Document.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public void DiagnosticsFromBothInputsAreCarriedThrough()
    {
        var fed = Parse("RestaurantFOM-1.3.fed");
        var omt = Parse("RestaurantFOM-1.3.omt");

        fed.Diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Warning, "fed-side marker"));
        omt.Diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Warning, "omt-side marker"));

        var merged = FomMerger.Merge(fed, omt).Document;

        Assert.Contains(merged.Diagnostics, d => d.Message.Contains("fed-side marker", StringComparison.Ordinal));
        Assert.Contains(merged.Diagnostics, d => d.Message.Contains("omt-side marker", StringComparison.Ordinal));
    }
}
