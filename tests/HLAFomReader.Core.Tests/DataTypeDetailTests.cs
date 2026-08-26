using System;
using System.IO;
using System.Linq;
using System.Text;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// Reading a datatype back for a human, as opposed to reducing it to the bytes it moves.
/// </summary>
/// <remarks>
/// The OMT tabulates no bounds — a <c>&lt;simpleData&gt;</c> row carries a representation, units,
/// resolution and accuracy and nothing else — so the min and max shown to a reader are derived, and
/// what they are derived from has to be right or the inspector is quietly making things up.
/// </remarks>
public sealed class DataTypeDetailTests
{
    private readonly ITestOutputHelper _output;

    public DataTypeDetailTests(ITestOutputHelper output) => _output = output;

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

    private static DataTypeResolver Evolved() => new(Parse("RestaurantFOM-1516-2010.xml"));

    // ---- derived ranges -------------------------------------------------------------------

    [Theory]
    [InlineData("uint:8", "0", "255")]
    [InlineData("uint:16", "0", "65,535")]
    [InlineData("uint:32", "0", "4,294,967,295")]
    [InlineData("uint:64", "0", "18,446,744,073,709,551,615")]
    [InlineData("int:8", "-128", "127")]
    [InlineData("int:16", "-32,768", "32,767")]
    [InlineData("int:32", "-2,147,483,648", "2,147,483,647")]
    [InlineData("int:64", "-9,223,372,036,854,775,808", "9,223,372,036,854,775,807")]
    public void IntegerBoundsComeFromTheWidthAndSign(string canonical, string min, string max)
    {
        var range = ValueRange.FromCanonical(canonical);

        Assert.NotNull(range);
        Assert.Equal(min, range!.Minimum);
        Assert.Equal(max, range.Maximum);
    }

    [Fact]
    public void FloatBoundsAreQuotedOnlyForTheWidthsIeee754Defines()
    {
        Assert.NotNull(ValueRange.FromCanonical("float:32"));
        Assert.NotNull(ValueRange.FromCanonical("float:64"));

        // A basic type calling itself a float at some other width is left unbounded rather than
        // guessed at: a made-up magnitude would read exactly like a real one.
        Assert.Null(ValueRange.FromCanonical("float:24"));
    }

    [Fact]
    public void CompositeAndWidthlessFormsBoundNothing()
    {
        Assert.Null(ValueRange.FromCanonical("record(float:32,float:32)"));
        Assert.Null(ValueRange.FromCanonical("array(uint:8,n)"));
        Assert.Null(ValueRange.FromCanonical("uint:?(Mystery)"));
        Assert.Null(ValueRange.FromCanonical("string:n"));
        Assert.Null(ValueRange.FromCanonical(null));
        Assert.Null(ValueRange.FromCanonical(""));
    }

    [Fact]
    public void EnumeratorBoundsBeatTheRepresentationsSpan()
    {
        // The representation says a value could be any of 4,294,967,296 things. The enumerators say
        // which three are legal, and that is the answer a reader wants.
        var range = ValueRange.FromEnumerators(new[] { "1", "7", "3" });

        Assert.NotNull(range);
        Assert.Equal("1", range!.Minimum);
        Assert.Equal("7", range.Maximum);
        Assert.Equal("3 declared enumerators", range.Basis);
    }

    [Fact]
    public void AnEnumeratorListingSeveralLiteralsCountsEveryOne()
    {
        var range = ValueRange.FromEnumerators(new[] { "1, 2, 9", "4" });

        Assert.Equal("1", range!.Minimum);
        Assert.Equal("9", range.Maximum);
    }

    [Fact]
    public void NonNumericEnumeratorsAreReportedRatherThanInvented()
    {
        var range = ValueRange.FromEnumerators(new[] { "Alpha", "Bravo" });

        Assert.NotNull(range);
        Assert.NotNull(range!.Note);
        Assert.Contains("not numeric", range.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Dynamic", "0", "unbounded")]
    [InlineData("3", "3", "3")]
    [InlineData("2..5", "2", "5")]
    public void ArrayCardinalityBoundsTheElementCount(string cardinality, string min, string max)
    {
        var range = ValueRange.FromCardinality(cardinality);

        Assert.NotNull(range);
        Assert.Equal(min, range!.Minimum);
        Assert.Equal(max, range.Maximum);
    }

    // ---- explaining real datatypes --------------------------------------------------------

    [Fact]
    public void APrimitiveExplainsWithTheRangeItsWidthImplies()
    {
        var detail = Evolved().Explain("unsigned long");

        Assert.Equal(DataTypeTable.Primitive, detail.Table);
        Assert.Equal("uint:32", detail.Canonical);
        Assert.Equal("0", detail.Range!.Minimum);
        Assert.Equal("4,294,967,295", detail.Range.Maximum);
    }

    [Fact]
    public void AnUndeclaredNameSaysSoInsteadOfGuessing()
    {
        var detail = Evolved().Explain("NoSuchTypeAnywhere");

        Assert.Equal(DataTypeTable.None, detail.Table);
        Assert.False(detail.IsResolved);
        Assert.Null(detail.Range);
        Assert.NotNull(detail.Truncation);
    }

    [Fact]
    public void AnAbsentNameIsAnAbsenceRatherThanAType()
    {
        foreach (var name in new string?[] { null, "", "   " })
        {
            var detail = Evolved().Explain(name);

            Assert.Equal(DataTypeTable.None, detail.Table);
            Assert.NotNull(detail.Truncation);
        }
    }

    [Fact]
    public void TheExplanationNeverContradictsTheEncodingShownBesideIt()
    {
        // Both walk the same tables in the same precedence, so every node's canonical must be
        // Resolve's own answer rather than a second derivation of it.
        var resolver = Evolved();
        var document = Parse("RestaurantFOM-1516-2010.xml");

        foreach (var type in document.DataTypes.AllDataTypes())
        {
            var detail = resolver.Explain(type.Name);
            var signature = resolver.Resolve(type.Name);

            Assert.Equal(signature.Canonical, detail.Canonical);
            Assert.Equal(signature.Shape, detail.Shape);
        }
    }

    [Fact]
    public void EveryDeclaredDatatypeExplainsWithoutThrowing()
    {
        foreach (var fileName in new[]
                 {
                     "RestaurantFOM-1516-2000.xml",
                     "RestaurantFOM-1516-2010.xml",
                     "RestaurantFOM-1516-2010-v2.xml",
                 })
        {
            var document = Parse(fileName);
            var resolver = new DataTypeResolver(document);

            foreach (var type in document.DataTypes.AllDataTypes())
            {
                var detail = resolver.Explain(type.Name);

                Assert.Equal(type.Name, detail.Name);
                Assert.NotEqual(DataTypeTable.None, detail.Table);
            }
        }
    }

    [Fact]
    public void ACompositeUnfoldsItsMembersWithTheirOwnRanges()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var resolver = new DataTypeResolver(document);

        var record = document.DataTypes.FixedRecordDataTypes.FirstOrDefault();
        Assert.True(record is not null, "the sample declares no fixed record to inspect");

        var detail = resolver.Explain(record!.Name);

        Assert.Equal(DataTypeTable.FixedRecord, detail.Table);
        Assert.Equal(record.Fields.Count, detail.Members.Count);
        Assert.All(detail.Members, m => Assert.Equal(DataTypeMemberRole.Field, m.Role));

        // A record has no single span of its own; each field bounds itself.
        Assert.Null(detail.Range);
        Assert.Contains(detail.Members, m => m.Type?.Range is not null);

        _output.WriteLine(Render(detail, 0).ToString());
    }

    [Fact]
    public void ADatatypeThatContainsItselfReportsTheLoopInsteadOfRecursing()
    {
        var document = new FomDocument { Standard = FomStandard.Ieee1516_2010 };
        var looping = new FixedRecordDataType { Name = "Loop" };
        looping.Fields.Add(new RecordField { Name = "self", DataType = "Loop" });
        document.DataTypes.FixedRecordDataTypes.Add(looping);

        var detail = new DataTypeResolver(document).Explain("Loop");

        var inner = detail.Members.Single().Type;
        Assert.NotNull(inner);
        Assert.NotNull(inner!.Truncation);
        Assert.Contains("loops", inner.Truncation!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NestingDeeperThanTheInspectorUnfoldsSaysSoRatherThanTruncatingSilently()
    {
        // Twelve records, each holding the next. The walk has to stop somewhere; what matters is
        // that the node where it stopped admits it.
        var document = new FomDocument { Standard = FomStandard.Ieee1516_2010 };
        for (var i = 0; i < 12; i++)
        {
            var record = new FixedRecordDataType { Name = $"Level{i}" };
            record.Fields.Add(new RecordField { Name = "next", DataType = $"Level{i + 1}" });
            document.DataTypes.FixedRecordDataTypes.Add(record);
        }

        var detail = new DataTypeResolver(document).Explain("Level0");

        var node = detail;
        var depth = 0;
        while (node!.Members.Count > 0 && node.Members[0].Type is { } next && next.Truncation is null)
        {
            node = next;
            depth++;
        }

        var stopped = node.Members.Count > 0 ? node.Members[0].Type! : node;
        Assert.NotNull(stopped.Truncation);
        Assert.True(depth < 12, "the walk did not stop");
    }

    /// <summary>Renders a detail tree, so a failure shows what the inspector would have drawn.</summary>
    private static StringBuilder Render(DataTypeDetail detail, int depth, StringBuilder? into = null)
    {
        var builder = into ?? new StringBuilder();
        var pad = new string(' ', depth * 2);

        builder.Append(pad).Append(detail.Name).Append("  [").Append(detail.Canonical).Append(']');
        if (detail.Range is { } range) builder.Append("  ").Append(range).Append("  (").Append(range.Basis).Append(')');
        builder.AppendLine();

        foreach (var member in detail.Members)
        {
            builder.Append(pad).Append("  ").Append(member.Role).Append(' ').Append(member.Name);
            if (member.Value is not null) builder.Append(" = ").Append(member.Value);
            builder.AppendLine();

            if (member.Type is not null) Render(member.Type, depth + 2, builder);
        }

        return builder;
    }
}
