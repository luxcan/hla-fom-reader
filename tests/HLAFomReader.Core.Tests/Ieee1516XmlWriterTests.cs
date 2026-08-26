using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// The 1516 writer, specified by round trip: what it writes has to read back as the model it was
/// given.
/// </summary>
/// <remarks>
/// Asserting on the XML text would pin the writer's formatting rather than its meaning, and would
/// pass just as happily for a file no other tool could read. Parsing the output back and comparing
/// the two documents with <see cref="FomComparer"/> asks the only question that matters — does this
/// file still say what the model said — and it asks it about every property the comparer knows,
/// which is every property in the OMT.
/// </remarks>
public sealed class Ieee1516XmlWriterTests
{
    private readonly ITestOutputHelper _output;

    public Ieee1516XmlWriterTests(ITestOutputHelper output) => _output = output;

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

    private static FomDocument Reread(FomDocument document)
    {
        using var reader = new StringReader(Ieee1516XmlWriter.ToXml(document));
        return new Ieee1516XmlParser().Parse(reader, "written.xml");
    }

    [Theory]
    [InlineData("RestaurantFOM-1516-2010.xml")]
    [InlineData("RestaurantFOM-1516-2010-v2.xml")]
    [InlineData("RestaurantFOM-1516-2000.xml")]
    public void WhatIsWrittenReadsBackAsTheSameModel(string fileName)
    {
        var original = Parse(fileName);
        var reread = Reread(original);

        Assert.False(reread.HasErrors, string.Join("; ", reread.Diagnostics.Select(d => d.ToString())));

        // Every OMT property, strictly, with nothing normalised away. Anything the writer drops or
        // spells differently shows up here as a difference.
        var result = new FomComparer().Compare(original, reread, new ComparisonOptions
        {
            Depth = ComparisonDepth.Full,
            IgnoreNotes = false,
            NormalizeRootNames = false,
            NormalizeTransportAndOrder = false,
            NormalizeWhitespace = false,
        });

        if (!result.AreIdentical)
        {
            foreach (var node in result.Root.DescendantsAndSelf().Where(n => n.Kind != DiffKind.Unchanged).Take(40))
            {
                _output.WriteLine($"{node.Kind} {node.Path}");
                foreach (var property in node.Properties.Where(p => p.IsDifferent))
                    _output.WriteLine($"    {property.Property}: '{property.LeftValue}' vs '{property.RightValue}'");
            }
        }

        Assert.True(result.AreIdentical, $"{result.TotalDifferences} differences survived the round trip");
    }

    /// <summary>
    /// Counts survive too, which is the coarse check a reader would make on the resulting file.
    /// </summary>
    [Fact]
    public void TheWrittenFileHoldsTheSameCountsAsTheModel()
    {
        var original = Parse("RestaurantFOM-1516-2010.xml");
        var reread = Reread(original);

        Assert.Equal(original.ObjectClassCount, reread.ObjectClassCount);
        Assert.Equal(original.AttributeCount, reread.AttributeCount);
        Assert.Equal(original.InteractionClassCount, reread.InteractionClassCount);
        Assert.Equal(original.ParameterCount, reread.ParameterCount);
        Assert.Equal(original.DataTypeCount, reread.DataTypeCount);
        Assert.Equal(original.DimensionCount, reread.DimensionCount);
    }

    /// <summary>The output declares itself as 1516-2010, whatever standard it was read from.</summary>
    /// <remarks>
    /// Not cosmetic. Another tool decides how to read the file from its root namespace, and a
    /// compiled FOM is a 1516-2010 artefact — modules are a 1516-2010 concept — so a file compiled
    /// from 1516-2000 modules still has to announce the standard it now conforms to.
    /// </remarks>
    [Fact]
    public void TheOutputDeclaresTheEvolvedNamespace()
    {
        var xml = XDocument.Parse(Ieee1516XmlWriter.ToXml(Parse("RestaurantFOM-1516-2000.xml")));

        Assert.Equal("objectModel", xml.Root!.Name.LocalName);
        Assert.Equal(Ieee1516XmlWriter.Namespace, xml.Root.Name.NamespaceName);
        Assert.Equal(FomStandard.Ieee1516_2010, Reread(Parse("RestaurantFOM-1516-2000.xml")).Standard);
    }

    /// <summary>A property the model does not carry produces no element at all.</summary>
    /// <remarks>
    /// The difference between "this FOM says nothing about ownership" and "this FOM says ownership
    /// is NoTransfer" is the difference between a module that left a decision to another module and
    /// one that made it. A writer that filled in defaults would turn every silence into a claim.
    /// </remarks>
    [Fact]
    public void PropertiesTheModelDoesNotCarryAreNotInvented()
    {
        var document = new FomDocument { Standard = FomStandard.Ieee1516_2010 };
        var root = new FomObjectClass { Name = "HLAobjectRoot", QualifiedName = "HLAobjectRoot" };
        root.Attributes.Add(new FomAttribute
        {
            Name = "Bare",
            QualifiedName = "HLAobjectRoot.Bare",
            DataType = "HLAinteger32BE",
        });
        document.ObjectClasses.Add(root);

        var xml = Ieee1516XmlWriter.ToXml(document);

        Assert.Contains("<dataType>HLAinteger32BE</dataType>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("updateType", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("ownership", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("sharing", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("dimensions", xml, StringComparison.Ordinal);

        // Empty tables produce no wrapper either, so the file does not claim an empty datatype
        // table where the model simply has none.
        Assert.DoesNotContain("dataTypes", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<interactions", xml, StringComparison.Ordinal);
    }

    /// <summary>A compiled module set round-trips as one document.</summary>
    /// <remarks>
    /// The case the writer exists for: several modules merged into the model a federation runs, then
    /// written out as the single self-contained file the registry points at from then on.
    /// </remarks>
    [Fact]
    public void ACompiledModuleSetSurvivesBeingWrittenOut()
    {
        var merged = Merging.FomModuleMerger.Merge(new[]
        {
            Parse("RestaurantFOM-1516-2010.xml"),
            Parse("RestaurantFOM-1516-2010-v2.xml"),
        }).Document;

        var reread = Reread(merged);

        Assert.False(reread.HasErrors);
        Assert.Equal(merged.ObjectClassCount, reread.ObjectClassCount);
        Assert.Equal(merged.AttributeCount, reread.AttributeCount);
        Assert.Equal(merged.DataTypeCount, reread.DataTypeCount);

        var result = new FomComparer().Compare(merged, reread, new ComparisonOptions
        {
            Depth = ComparisonDepth.Full,
            IgnoreNotes = false,
            NormalizeRootNames = false,
            NormalizeTransportAndOrder = false,
            NormalizeWhitespace = false,
        });

        if (!result.AreIdentical)
        {
            foreach (var node in result.Root.DescendantsAndSelf().Where(n => n.Kind != DiffKind.Unchanged).Take(40))
            {
                _output.WriteLine($"{node.Kind} {node.Path}");
                foreach (var property in node.Properties.Where(p => p.IsDifferent))
                    _output.WriteLine($"    {property.Property}: '{property.LeftValue}' vs '{property.RightValue}'");
            }
        }

        Assert.True(result.AreIdentical, $"{result.TotalDifferences} differences survived the round trip");
    }
}
