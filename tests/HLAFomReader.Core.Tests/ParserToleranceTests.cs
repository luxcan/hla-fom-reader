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
/// The 1516 parser has to cope with what real OMT tools emit: any of the three namespaces or none,
/// and scalars written either as child elements or as XML attributes. The FED parser has to cope
/// with the looser things that appear in hand-written .fed files.
/// </summary>
public sealed class ParserToleranceTests
{
    private readonly ITestOutputHelper _output;

    public ParserToleranceTests(ITestOutputHelper output) => _output = output;

    private static FomDocument ParseXml(string xml) =>
        new Ieee1516XmlParser().Parse(new StringReader(xml));

    private static FomDocument ParseFed(string fed) =>
        new FedParser().Parse(new StringReader(fed));

    private const string ElementFormFom = """
        <?xml version="1.0" encoding="UTF-8"?>
        <objectModel xmlns="http://standards.ieee.org/IEEE1516-2010">
          <modelIdentification>
            <name>Tolerance</name>
            <type>FOM</type>
            <version>1.0</version>
          </modelIdentification>
          <objects>
            <objectClass>
              <name>HLAobjectRoot</name>
              <sharing>PublishSubscribe</sharing>
              <attribute>
                <name>HLAprivilegeToDeleteObject</name>
                <dataType>NA</dataType>
                <transportation>HLAreliable</transportation>
                <order>Receive</order>
              </attribute>
              <objectClass>
                <name>Aircraft</name>
                <sharing>PublishSubscribe</sharing>
                <attribute>
                  <name>Position</name>
                  <dataType>PositionRecord</dataType>
                  <transportation>HLAbestEffort</transportation>
                  <order>TimeStamp</order>
                </attribute>
              </objectClass>
            </objectClass>
          </objects>
        </objectModel>
        """;

    /// <summary>The same FOM with every scalar moved onto XML attributes — the other common dialect.</summary>
    private const string AttributeFormFom = """
        <?xml version="1.0" encoding="UTF-8"?>
        <objectModel xmlns="http://standards.ieee.org/IEEE1516-2010">
          <modelIdentification name="Tolerance" type="FOM" version="1.0" />
          <objects>
            <objectClass name="HLAobjectRoot" sharing="PublishSubscribe">
              <attribute name="HLAprivilegeToDeleteObject" dataType="NA"
                         transportation="HLAreliable" order="Receive" />
              <objectClass name="Aircraft" sharing="PublishSubscribe">
                <attribute name="Position" dataType="PositionRecord"
                           transportation="HLAbestEffort" order="TimeStamp" />
              </objectClass>
            </objectClass>
          </objects>
        </objectModel>
        """;

    [Fact]
    public void ElementFormAndAttributeFormProduceTheSameModel()
    {
        var byElement = ParseXml(ElementFormFom);
        var byAttribute = ParseXml(AttributeFormFom);

        Assert.False(byElement.HasErrors, string.Join(" | ", byElement.Diagnostics));
        Assert.False(byAttribute.HasErrors, string.Join(" | ", byAttribute.Diagnostics));

        Assert.Equal("Tolerance", byElement.Identification.Name);
        Assert.Equal("Tolerance", byAttribute.Identification.Name);
        Assert.Equal(byElement.ObjectClassCount, byAttribute.ObjectClassCount);
        Assert.Equal(byElement.AttributeCount, byAttribute.AttributeCount);

        var result = new FomComparer().Compare(byElement, byAttribute);
        foreach (var difference in result.Differences())
            _output.WriteLine($"{difference.Kind} {difference.Path}");

        Assert.True(result.AreIdentical,
            $"The two serialisation dialects should parse identically; found {result.TotalDifferences} differences");
    }

    [Theory]
    [InlineData("http://standards.ieee.org/IEEE1516-2010", FomStandard.Ieee1516_2010)]
    [InlineData("http://standards.ieee.org/IEEE1516-2000", FomStandard.Ieee1516_2000)]
    [InlineData("http://standards.ieee.org/IEEE1516-2025", FomStandard.Ieee1516_2025)]
    public void DetectsTheStandardFromTheRootNamespace(string namespaceUri, FomStandard expected)
    {
        var xml = ElementFormFom.Replace("http://standards.ieee.org/IEEE1516-2010", namespaceUri,
            StringComparison.Ordinal);

        var document = ParseXml(xml);

        Assert.Equal(expected, document.Standard);
        Assert.Equal(namespaceUri, document.SourceNamespace);
        Assert.Equal(2, document.ObjectClassCount);
    }

    [Fact]
    public void ReadsAFomThatDeclaresNoNamespaceAtAll()
    {
        var xml = ElementFormFom.Replace(" xmlns=\"http://standards.ieee.org/IEEE1516-2010\"", "",
            StringComparison.Ordinal);

        var document = ParseXml(xml);

        Assert.Equal(2, document.ObjectClassCount);
        Assert.Equal(2, document.AttributeCount);
        Assert.Contains(document.AllObjectClasses(), c => c.Name == "Aircraft");
    }

    [Fact]
    public void ReadsAFomThatUsesANamespacePrefix()
    {
        const string prefixed = """
            <?xml version="1.0" encoding="UTF-8"?>
            <hla:objectModel xmlns:hla="http://standards.ieee.org/IEEE1516-2010">
              <hla:objects>
                <hla:objectClass>
                  <hla:name>HLAobjectRoot</hla:name>
                  <hla:objectClass>
                    <hla:name>Aircraft</hla:name>
                    <hla:attribute><hla:name>Position</hla:name><hla:dataType>PositionRecord</hla:dataType></hla:attribute>
                  </hla:objectClass>
                </hla:objectClass>
              </hla:objects>
            </hla:objectModel>
            """;

        var document = ParseXml(prefixed);

        Assert.Equal(FomStandard.Ieee1516_2010, document.Standard);
        Assert.Equal(2, document.ObjectClassCount);
        Assert.Equal("PositionRecord",
            document.AllObjectClasses().Single(c => c.Name == "Aircraft").Attributes.Single().DataType);
    }

    [Fact]
    public void MalformedXmlIsReportedAsADiagnosticRatherThanAnException()
    {
        var document = ParseXml("<objectModel><objects><objectClass></objects></objectModel>");

        Assert.True(document.HasErrors);
        Assert.Contains(document.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void FedParserAcceptsQuotedNamesCommentsAndMissingOrderTokens()
    {
        const string fed = """
            ;; a hand-written FED
            (FED
              (Federation "Test Federation")   ;; quoted, with a space
              (FEDversion v1.3)
              (objects
                (class ObjectRoot
                  (attribute privilegeToDelete reliable receive)
                  (class "Air Vehicle"
                    (attribute Callsign reliable)          ;; order token omitted
                    (attribute Position best_effort timestamp)
                  )
                )
              )
              (interactions
                (class InteractionRoot BEST_EFFORT RECEIVE
                  (class Weapon RELIABLE TIMESTAMP
                    (parameter Target)
                  )
                )
              )
            )
            """;

        var document = ParseFed(fed);

        foreach (var diagnostic in document.Diagnostics)
            _output.WriteLine(diagnostic.ToString());

        Assert.False(document.HasErrors);
        Assert.Equal("Test Federation", document.Identification.Name);
        Assert.Equal("v1.3", document.Identification.Version);

        var vehicle = document.AllObjectClasses().Single(c => c.Name == "Air Vehicle");
        Assert.Equal("ObjectRoot.Air Vehicle", vehicle.QualifiedName);
        Assert.Equal(2, vehicle.Attributes.Count);

        // The attribute missing its order token still parses, and says so.
        Assert.Null(vehicle.Attributes.Single(a => a.Name == "Callsign").Order);
        Assert.Contains(document.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning);

        var weapon = document.AllInteractionClasses().Single(c => c.Name == "Weapon");
        Assert.Equal("InteractionRoot.Weapon", weapon.QualifiedName);
        Assert.Single(weapon.Parameters);
    }

    [Fact]
    public void FedParserNeverThrowsOnGarbage()
    {
        var document = ParseFed("(((( this is not a FED file at all ");

        Assert.NotNull(document);
        Assert.Equal(FomStandard.Hla13, document.Standard);
        Assert.NotEmpty(document.Diagnostics);
    }

    [Fact]
    public void ContentDetectionBeatsTheFileExtension()
    {
        // A 1516 FOM that someone saved with a .fed extension must still be read as XML.
        Assert.Equal(FomStandard.Ieee1516_2010,
            FomFileReader.DetectStandardFromContent(ElementFormFom, ".fed"));

        // ...and a FED saved as .xml must still be read as FED.
        Assert.Equal(FomStandard.Hla13,
            FomFileReader.DetectStandardFromContent("(FED (Federation X) (FEDversion v1.3)", ".xml"));
    }

    [Fact]
    public void TurningOffRootNameNormalisationMakesTheCrossStandardDiffLiteral()
    {
        var fed = ParseFed("""
            (FED (Federation X) (FEDversion v1.3)
              (objects (class ObjectRoot (attribute privilegeToDelete reliable receive))))
            """);

        var fom = ParseXml(ElementFormFom);

        var folded = new FomComparer().Compare(fed, fom,
            new ComparisonOptions { NormalizeRootNames = true });
        var literal = new FomComparer().Compare(fed, fom,
            new ComparisonOptions { NormalizeRootNames = false });

        _output.WriteLine($"folded:  +{folded.AddedCount} -{folded.RemovedCount} ~{folded.ModifiedCount}");
        _output.WriteLine($"literal: +{literal.AddedCount} -{literal.RemovedCount} ~{literal.ModifiedCount}");

        // Without folding, ObjectRoot and HLAobjectRoot are unrelated, so the root is
        // reported once as removed and once as added instead of being matched.
        Assert.True(literal.RemovedCount > folded.RemovedCount);
        Assert.True(literal.AddedCount >= folded.AddedCount);
    }
}
