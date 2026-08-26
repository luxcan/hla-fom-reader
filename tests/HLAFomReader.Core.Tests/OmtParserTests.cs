using System;
using System.IO;
using System.Linq;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// HLA 1.3 OMT documents (.omt / .omd). Unlike a FED, an OMT carries the attribute table — so a 1.3
/// entry read from one has real datatypes, sharing and descriptions, and compares meaningfully
/// against a 1516 FOM.
/// </summary>
public sealed class OmtParserTests
{
    private readonly ITestOutputHelper _output;

    public OmtParserTests(ITestOutputHelper output) => _output = output;

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

    private static FomDocument ParseSample(string fileName) =>
        FomFileReader.ParseFile(Path.Combine(Samples, fileName));

    [Fact]
    public void AnOmtDocumentIsDetectedAsHla13()
    {
        var path = Path.Combine(Samples, "RestaurantFOM-1.3.omt");
        Assert.True(File.Exists(path), $"Missing fixture: {path}");

        Assert.Equal(FomStandard.Hla13, FomFileReader.DetectStandard(path));
    }

    [Fact]
    public void TheOmtIsReadWithoutErrors()
    {
        var document = ParseSample("RestaurantFOM-1.3.omt");

        foreach (var diagnostic in document.Diagnostics)
            _output.WriteLine(diagnostic.ToString());

        _output.WriteLine($"{document.ObjectClassCount} classes, {document.AttributeCount} attributes, " +
                          $"{document.InteractionClassCount} interactions, {document.ParameterCount} parameters, " +
                          $"{document.DataTypeCount} datatypes");

        Assert.False(document.HasErrors,
            string.Join(" | ", document.Diagnostics.Select(d => d.ToString())));
        Assert.Equal(FomStandard.Hla13, document.Standard);
    }

    /// <summary>The whole point of the feature: a 1.3 document that actually has types.</summary>
    [Fact]
    public void UnlikeAFedTheOmtCarriesDatatypesAndAttributeMetadata()
    {
        var fed = ParseSample("RestaurantFOM-1.3.fed");
        var omt = ParseSample("RestaurantFOM-1.3.omt");

        Assert.True(fed.DataTypes.IsEmpty, "a FED has no datatype table");
        Assert.False(omt.DataTypes.IsEmpty, "an OMT does");

        Assert.NotEmpty(omt.DataTypes.FixedRecordDataTypes);   // ComplexDataType
        Assert.NotEmpty(omt.DataTypes.EnumeratedDataTypes);    // EnumeratedDataType

        var typed = omt.AllObjectClasses().SelectMany(c => c.Attributes).ToList();
        Assert.All(typed, a => Assert.False(string.IsNullOrWhiteSpace(a.DataType)));

        // The OMT-only columns come through too.
        Assert.Contains(typed, a => !string.IsNullOrWhiteSpace(a.Cardinality));
        Assert.Contains(typed, a => !string.IsNullOrWhiteSpace(a.Units));

        // And sharing, which a FED cannot express at all.
        Assert.All(fed.AllObjectClasses(), c => Assert.Null(c.Sharing));
        Assert.Contains(omt.AllObjectClasses(), c => !string.IsNullOrWhiteSpace(c.Sharing));
    }

    [Fact]
    public void NoteReferencesAreStrippedFromValues()
    {
        var omt = ParseSample("RestaurantFOM-1.3.omt");

        // A name written as (Name "Foo" [2]) must not become "Foo [2]".
        Assert.All(omt.AllObjectClasses(), c =>
        {
            Assert.DoesNotContain('[', c.Name);
            Assert.DoesNotContain(']', c.Name);
        });

        Assert.All(omt.DataTypes.AllDataTypes(), d =>
        {
            Assert.DoesNotContain('[', d.Name);
            Assert.DoesNotContain(']', d.Name);
        });

        Assert.NotEmpty(omt.Notes);
    }

    /// <summary>
    /// The .fed and the .omt describe the same federation, so once the OMT-only information is set
    /// aside the class and interaction trees must line up element for element.
    /// </summary>
    [Fact]
    public void TheFedAndTheOmtDescribeTheSameStructure()
    {
        var fed = ParseSample("RestaurantFOM-1.3.fed");
        var omt = ParseSample("RestaurantFOM-1.3.omt");

        var fedClasses = fed.AllObjectClasses().Select(c => c.QualifiedName).OrderBy(n => n).ToList();
        var omtClasses = omt.AllObjectClasses().Select(c => c.QualifiedName).OrderBy(n => n).ToList();

        _output.WriteLine("fed only: " + string.Join(", ", fedClasses.Except(omtClasses)));
        _output.WriteLine("omt only: " + string.Join(", ", omtClasses.Except(fedClasses)));

        Assert.Equal(fedClasses, omtClasses);

        var fedAttributes = fed.AllObjectClasses().SelectMany(c => c.Attributes)
            .Select(a => a.QualifiedName).OrderBy(n => n).ToList();
        var omtAttributes = omt.AllObjectClasses().SelectMany(c => c.Attributes)
            .Select(a => a.QualifiedName).OrderBy(n => n).ToList();

        Assert.Equal(fedAttributes, omtAttributes);
    }

    [Fact]
    public void ComparingTheOmtAgainstAnEvolvedFomActuallyComparesDatatypes()
    {
        var omt = ParseSample("RestaurantFOM-1.3.omt");
        var evolved = ParseSample("RestaurantFOM-1516-2010.xml");

        var result = new FomComparer().Compare(omt, evolved);

        var dataTypeRows = result.Root.DescendantsAndSelf()
            .SelectMany(n => n.Properties)
            .Where(p => p.Property.Equals("DataType", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(dataTypeRows);

        // Against a FED every DataType row is a format gap; against an OMT they carry real values
        // on both sides and are compared for real.
        Assert.Contains(dataTypeRows, p => p.Reason is null
                                        && !string.IsNullOrWhiteSpace(p.LeftValue)
                                        && !string.IsNullOrWhiteSpace(p.RightValue));

        _output.WriteLine($"+{result.AddedCount} -{result.RemovedCount} ~{result.ModifiedCount}");
        _output.WriteLine($"format gaps: {result.FormatGapPropertyCount}, authored: {result.AuthoredPropertyDifferenceCount}");
    }

    [Fact]
    public void AFedComparedWithItsOwnOmtReportsTheOmtOnlyInformationAsDifferences()
    {
        var fed = ParseSample("RestaurantFOM-1.3.fed");
        var omt = ParseSample("RestaurantFOM-1.3.omt");

        var result = new FomComparer().Compare(fed, omt);

        // Same standard on both sides, so nothing here is a cross-standard format gap...
        Assert.Equal(0, result.FormatGapPropertyCount);

        // ...but the OMT genuinely knows things the FED does not, and that is a real difference.
        Assert.False(result.AreIdentical);
        Assert.Contains(
            result.Root.DescendantsAndSelf().SelectMany(n => n.Properties),
            p => p.IsDifferent && p.Property.Equals("DataType", StringComparison.Ordinal));
    }

    // ---- real files, wherever this machine keeps them ----------------------------------------
    //
    // Located through RealFomFiles rather than named here. These are large vendor-produced OMT
    // documents and none of them is in the repository; naming them by path put the folder layout of
    // other projects and the names of the models inside them into committed source, which is the
    // very thing .gitignore keeps out. Point HLAFOMREADER_REAL_FOMS at a folder to run these.

    /// <summary>
    /// Every real OMT document this machine has parses into a substantial model.
    /// </summary>
    /// <remarks>
    /// One test over all of them rather than a theory row each: the set is discovered at run time
    /// and may be empty, and a theory with no rows is an error rather than a skip.
    /// </remarks>
    [Fact]
    public void RealVendorOmtFilesParseIntoSubstantialModels()
    {
        var files = RealFomFiles.WithExtensions(".omt", ".omd");
        if (files.Count == 0)
        {
            _output.WriteLine(RealFomFiles.NotConfigured);
            return;
        }

        foreach (var path in files)
        {
            var document = FomFileReader.ParseFile(path);

            _output.WriteLine($"{Path.GetFileName(path)}: {document.ObjectClassCount} classes, " +
                              $"{document.AttributeCount} attributes, {document.InteractionClassCount} interactions, " +
                              $"{document.ParameterCount} parameters, {document.DataTypeCount} datatypes");

            foreach (var diagnostic in document.Diagnostics.Take(10))
                _output.WriteLine("  " + diagnostic);

            Assert.Equal(FomStandard.Hla13, document.Standard);

            // Every one of these is a large vendor FOM; anything much smaller means the parse
            // collapsed. The file name goes in the message because the failure has to say which.
            var which = Path.GetFileName(path);
            Assert.True(document.ObjectClassCount >= 35, $"{which}: only {document.ObjectClassCount} classes");
            Assert.True(document.AttributeCount >= 200, $"{which}: only {document.AttributeCount} attributes");
            Assert.True(document.InteractionClassCount >= 30, $"{which}: only {document.InteractionClassCount} interactions");
            Assert.True(document.ParameterCount >= 150, $"{which}: only {document.ParameterCount} parameters");
            Assert.True(document.DataTypeCount >= 100, $"{which}: only {document.DataTypeCount} datatypes");

            // Datatypes are the reason we read OMT at all.
            Assert.All(document.AllObjectClasses().SelectMany(c => c.Attributes),
                a => Assert.False(string.IsNullOrWhiteSpace(a.DataType)));

            // The class tree must actually be a tree, not a flat pile.
            Assert.Contains(document.AllObjectClasses(), c => c.Children.Count > 0);
            Assert.Contains(document.AllObjectClasses(), c => c.QualifiedName.Contains('.', StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// MAK-RPR1-1-1.omt contains an unterminated string — (POCOrgName "MAK Technologies) — which
    /// destroys the parenthesis balance for the rest of the file. Recovery must still yield the model.
    /// </summary>
    [Fact]
    public void AMalformedRealFileStillYieldsItsClasses()
    {
        if (RealFomFiles.Named("MAK-RPR1-1-1.omt") is not { } path)
        {
            _output.WriteLine(RealFomFiles.NotConfigured);
            return;
        }

        var document = FomFileReader.ParseFile(path);

        foreach (var diagnostic in document.Diagnostics.Take(12))
            _output.WriteLine(diagnostic.ToString());

        Assert.True(document.ObjectClassCount >= 40,
            $"recovery failed: only {document.ObjectClassCount} classes came back");
        Assert.True(document.InteractionClassCount >= 100,
            $"recovery failed: only {document.InteractionClassCount} interactions came back");

        // The damage should be reported rather than silently swallowed.
        Assert.Contains(document.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);

        // Known content from that FOM, proving the recovery kept real data.
        Assert.Contains(document.AllObjectClasses(), c => c.Name == "BaseEntity");
        Assert.Contains(document.AllObjectClasses(), c => c.Name == "EnvironmentalEntity");
    }
}
