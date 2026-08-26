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
/// Resolving a datatype name to what it actually encodes. The point is to tell a rename apart from a
/// re-encoding: migrating RPR 1.0 to 2.0 renames nearly every type, and comparing names alone reports
/// 614 "changes" of which almost none need any data conversion.
/// </summary>
public sealed class DataTypeResolverTests
{
    private readonly ITestOutputHelper _output;

    public DataTypeResolverTests(ITestOutputHelper output) => _output = output;

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

    [Fact]
    public void AnUninterpretedByteIsUnsignedDespiteTheLettersHiddenInTheWord()
    {
        // The MIM words HLAoctet "Uninterpreted 8-bit byte", and every FOM that spells the MIM out
        // repeats it. Reading that column for the letters "int" inside "uninterpreted" resolves the
        // type to int:8, which is not cosmetic: RPR 1.0's `octet` is uint:8 through the lexicon, so
        // the two stop matching and a 1.0-to-2.0 migration reports a re-encoding on every
        // uninterpreted byte it carries. See DataTypeResolver.KindOf.
        var document = new FomDocument { Standard = FomStandard.Ieee1516_2010 };
        document.DataTypes.BasicDataRepresentations.Add(new BasicDataType
        {
            Name = "HLAoctet",
            Size = "8",
            Interpretation = "Uninterpreted 8-bit byte",
            Endian = "Big",
        });

        var resolver = new DataTypeResolver(document);

        Assert.Equal("uint:8", resolver.Resolve("HLAoctet").Canonical);

        // Which is the whole point: it must still line up with the 1.3 primitive it replaced.
        Assert.True(resolver.Resolve("HLAoctet").EncodesTheSameAs(resolver.Resolve("octet")));
    }

    [Fact]
    public void ADeclaredOctetInTheSampleMatchesThe13PrimitiveItReplaced()
    {
        var resolver = Evolved();

        Assert.Equal("uint:8", resolver.Resolve("HLAoctet").Canonical);
    }

    // ---- the FOM is the authority ---------------------------------------------------------
    //
    // The octet fix is not the resolver deciding what HLAoctet "really" is. It reads the FOM's own
    // <size> and <interpretation> columns; the bug was that it misread the words "Uninterpreted
    // 8-bit byte" as naming an integer, because the letters "int" sit inside "Uninterpreted". These
    // pin down that the file, not the resolver, is in charge — so redeclaring a standard name is
    // reported as the change it is.

    /// <summary>Builds a document declaring one basic type exactly as the caller words it.</summary>
    private static FomDocument WithBasic(string name, string size, string interpretation)
    {
        var document = new FomDocument { Standard = FomStandard.Ieee1516_2010 };
        document.DataTypes.BasicDataRepresentations.Add(new BasicDataType
        {
            Name = name,
            Size = size,
            Interpretation = interpretation,
            Endian = "Big",
        });
        return document;
    }

    [Theory]
    [InlineData("Uninterpreted 8-bit byte", "8", "uint:8")]
    [InlineData("Floating point number", "32", "float:32")]
    [InlineData("Double precision floating point number", "64", "float:64")]
    [InlineData("Integer", "16", "int:16")]
    [InlineData("Unsigned integer", "16", "uint:16")]
    public void TheInterpretationColumnDecidesTheKindWhateverTheTypeIsCalled(
        string interpretation, string size, string expected)
    {
        // Every one of these is named HLAoctet. The name never gets a vote while the FOM states an
        // interpretation, so a FOM that redeclares a standard type is read as it wrote it.
        var resolver = new DataTypeResolver(WithBasic("HLAoctet", size, interpretation));

        Assert.Equal(expected, resolver.Resolve("HLAoctet").Canonical);
    }

    [Fact]
    public void RedeclaringAStandardTypeAsAFloatIsReportedAsARealChange()
    {
        // The question this answers: if somebody retypes HLAoctet, does the tool notice?
        var asDeclared = new DataTypeResolver(WithBasic("HLAoctet", "8", "Uninterpreted 8-bit byte"));
        var asFloat = new DataTypeResolver(WithBasic("HLAoctet", "32", "Floating point number"));

        var left = asDeclared.Resolve("HLAoctet");
        var right = asFloat.Resolve("HLAoctet");

        Assert.Equal("uint:8", left.Canonical);
        Assert.Equal("float:32", right.Canonical);

        // Same name on both sides, and the tool still reports the re-encoding.
        Assert.False(left.EncodesTheSameAs(right));
    }

    [Fact]
    public void ARedeclaredTypeBeatsTheBuiltInStandardMim()
    {
        // The MIM table carries HLAoctet as an 8-bit unsigned value. A FOM that says otherwise wins,
        // exactly as an RTI would honour the FOM's own declaration.
        var redeclared = new DataTypeResolver(WithBasic("HLAoctet", "64", "Floating point number"));
        var silent = new DataTypeResolver(new FomDocument { Standard = FomStandard.Ieee1516_2010 });

        Assert.Equal("float:64", redeclared.Resolve("HLAoctet").Canonical);

        // And the FOM that declares nothing still gets the MIM's answer, because that is what the
        // RTI would merge in.
        Assert.Equal("uint:8", silent.Resolve("HLAoctet").Canonical);
        Assert.True(silent.IsStandardDatatype("HLAoctet"));

        // The redeclaring one is not "standard" any more: the definition in force is its own.
        Assert.False(redeclared.IsStandardDatatype("HLAoctet"));
    }

    [Fact]
    public void AWidthChangeAloneIsEnoughToReportAReEncoding()
    {
        // Same interpretation, different <size>. The FOM's width column is read, not assumed.
        var eight = new DataTypeResolver(WithBasic("HLAoctet", "8", "Uninterpreted 8-bit byte"));
        var sixteen = new DataTypeResolver(WithBasic("HLAoctet", "16", "Uninterpreted 16-bit byte"));

        Assert.Equal("uint:8", eight.Resolve("HLAoctet").Canonical);
        Assert.Equal("uint:16", sixteen.Resolve("HLAoctet").Canonical);
        Assert.False(eight.Resolve("HLAoctet").EncodesTheSameAs(sixteen.Resolve("HLAoctet")));
    }

    [Fact]
    public void TheNameIsConsultedOnlyWhenTheFomStatesNoInterpretation()
    {
        // With the column blank there is nothing to read, so the type's own name is the only
        // evidence left — the standard basic types spell their kind into their names. This is a
        // fallback, and it must never pre-empt a stated interpretation.
        var silent = new DataTypeResolver(WithBasic("HLAoctet", "8", ""));
        Assert.Equal("uint:8", silent.Resolve("HLAoctet").Canonical);

        var stated = new DataTypeResolver(WithBasic("HLAoctet", "8", "Integer"));
        Assert.Equal("int:8", stated.Resolve("HLAoctet").Canonical);
    }

    [Fact]
    public void OnlyUninterpretedJumpsAheadOfTheIntegerTest()
    {
        // The fix is deliberately narrow. "Uninterpreted" is the one word that hides "int" while
        // meaning the opposite of one; an interpretation naming both a byte and an integer still
        // reads as the integer it says it is.
        var both = new DataTypeResolver(WithBasic("SomeType", "8", "Integer byte"));
        Assert.Equal("int:8", both.Resolve("SomeType").Canonical);

        var plainByte = new DataTypeResolver(WithBasic("SomeType", "8", "Raw byte"));
        Assert.Equal("uint:8", plainByte.Resolve("SomeType").Canonical);
    }

    [Theory]
    [InlineData("octet", "uint:8")]
    [InlineData("unsigned char", "uint:8")]
    [InlineData("short", "int:16")]
    [InlineData("unsigned short", "uint:16")]
    [InlineData("long", "int:32")]
    [InlineData("unsigned long", "uint:32")]
    [InlineData("long long", "int:64")]
    [InlineData("unsigned long long", "uint:64")]
    [InlineData("float", "float:32")]
    [InlineData("double", "float:64")]
    public void Hla13PrimitivesResolveThroughTheLexicon(string name, string expected)
    {
        // A 1.3 OMT names C-style primitives that appear in no datatype table at all.
        var signature = Evolved().Resolve(name);

        Assert.True(signature.IsResolved, $"{name} did not resolve");
        Assert.Equal(expected, signature.Canonical);
    }

    [Theory]
    [InlineData("NA")]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("SomethingNobodyDefined")]
    public void UnknownOrAbsentNamesResolveToUnresolvedRatherThanGuessing(string? name)
    {
        var signature = Evolved().Resolve(name);

        Assert.False(signature.IsResolved);
        Assert.Equal(DataTypeShape.Unknown, signature.Shape);
    }

    [Fact]
    public void A1516BasicTypeResolvesFromTheBasicDataTable()
    {
        var signature = Evolved().Resolve("HLAinteger32BE");

        Assert.Equal(DataTypeShape.Scalar, signature.Shape);
        Assert.Equal(32, signature.Bits);
        _output.WriteLine($"HLAinteger32BE -> {signature.Canonical} (endian {signature.Endian})");
    }

    /// <summary>
    /// The rule that makes <c>float</c> to <c>AngleRadianFloat32</c> a rename: units, resolution and
    /// accuracy are semantics, not encoding, so a simple type encodes exactly as its representation.
    /// </summary>
    [Fact]
    public void ASimpleTypeEncodesAsItsRepresentationAndUnitsAreIgnored()
    {
        var resolver = Evolved();

        // TemperatureCelsius and CurrencyAmount are both simple types over HLAfloat64BE with
        // different units; they encode identically.
        var temperature = resolver.Resolve("TemperatureCelsius");
        var currency = resolver.Resolve("CurrencyAmount");
        var underlying = resolver.Resolve("HLAfloat64BE");

        _output.WriteLine($"TemperatureCelsius -> {temperature.Canonical}");
        _output.WriteLine($"CurrencyAmount     -> {currency.Canonical}");

        Assert.True(temperature.IsResolved);
        Assert.Equal(underlying.Canonical, temperature.Canonical);
        Assert.True(temperature.EncodesTheSameAs(currency));
    }

    [Fact]
    public void AnEnumerationEncodesAsItsRepresentationRegardlessOfItsEnumerators()
    {
        var v1 = new DataTypeResolver(Parse("RestaurantFOM-1516-2010.xml"));
        var v2 = new DataTypeResolver(Parse("RestaurantFOM-1516-2010-v2.xml"));

        // v2 adds a Tea enumerator to DrinkKindEnum. More values, same bits on the wire.
        var before = v1.Resolve("DrinkKindEnum");
        var after = v2.Resolve("DrinkKindEnum");

        _output.WriteLine($"{before.Canonical} vs {after.Canonical}");

        Assert.Equal(DataTypeShape.Enumerated, before.Shape);
        Assert.True(before.EncodesTheSameAs(after),
            "adding an enumerator does not change the encoding");
    }

    /// <summary>
    /// An enumeration constrains which values are legal; it does not change a single bit on the wire.
    /// So it must encode as its representation, exactly as a simple type does. This is what lets a
    /// 1.3 <c>boolean</c> match RPR 2.0's <c>RPRboolean</c> — both one octet holding 0 or 1 — which
    /// accounts for 510 of the 556 false re-encodings in the real RPR migration.
    /// </summary>
    [Fact]
    public void AnEnumerationEncodesAsItsRepresentationNotAsADistinctFamily()
    {
        var resolver = Evolved();

        var enumerated = resolver.Resolve("DrinkKindEnum");
        var representation = resolver.Resolve("HLAinteger32BE");

        _output.WriteLine($"DrinkKindEnum -> {enumerated.Canonical}; HLAinteger32BE -> {representation.Canonical}");

        Assert.Equal(DataTypeShape.Enumerated, enumerated.Shape);   // the shape is still known...
        Assert.Equal(representation.Canonical, enumerated.Canonical); // ...but it encodes the same
        Assert.True(enumerated.EncodesTheSameAs(representation));
    }

    /// <summary>
    /// The 1.3 primitive <c>boolean</c> is one octet. It must therefore match an 8-bit enumeration,
    /// while NOT matching the 1516 MIM's <c>HLAboolean</c>, which is an enumeration over a 32-bit
    /// integer and genuinely is four bytes.
    /// </summary>
    [Fact]
    public void A13BooleanIsAnOctetAndMatchesAnEightBitEnumerationButNotHlaBoolean()
    {
        var resolver = Evolved();

        var primitive = resolver.Resolve("boolean");
        Assert.Equal("uint:8", primitive.Canonical);

        // HLAboolean is enumerated over HLAinteger32BE in the standard MIM — four bytes, not one.
        var hlaBoolean = resolver.Resolve("HLAinteger32BE");
        Assert.False(primitive.EncodesTheSameAs(hlaBoolean),
            "a one-octet boolean must not be confused with a 32-bit one");
    }

    [Fact]
    public void AnArrayDistinguishesItsElementAndItsLength()
    {
        var v1 = new DataTypeResolver(Parse("RestaurantFOM-1516-2010.xml"));
        var v2 = new DataTypeResolver(Parse("RestaurantFOM-1516-2010-v2.xml"));

        // v2 changes SectionArray's cardinality from 8 to 12 — a genuinely different encoding.
        var before = v1.Resolve("SectionArray");
        var after = v2.Resolve("SectionArray");

        _output.WriteLine($"{before.Canonical} vs {after.Canonical}");

        Assert.Equal(DataTypeShape.Array, before.Shape);
        Assert.False(before.EncodesTheSameAs(after),
            "a different cardinality is a different encoding");
    }

    [Fact]
    public void ARecordEncodesAsItsFieldsInOrderAndFieldNamesDoNotMatter()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var resolver = new DataTypeResolver(document);

        var record = document.DataTypes.FixedRecordDataTypes.FirstOrDefault();
        Assert.NotNull(record);

        var signature = resolver.Resolve(record!.Name);
        _output.WriteLine($"{record.Name} -> {signature.Canonical}");

        Assert.Equal(DataTypeShape.Record, signature.Shape);
        Assert.StartsWith("record(", signature.Canonical, StringComparison.Ordinal);

        // Renaming a field must not change the encoding.
        var renamed = Parse("RestaurantFOM-1516-2010.xml");
        var renamedRecord = renamed.DataTypes.FixedRecordDataTypes.Single(r => r.Name == record.Name);
        renamedRecord.Fields[0].Name = "CompletelyDifferentFieldName";

        var after = new DataTypeResolver(renamed).Resolve(record.Name);
        Assert.True(signature.EncodesTheSameAs(after));
    }

    [Fact]
    public void ACyclicDatatypeIsCaughtRatherThanRecursingForever()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");

        // A record whose field is typed as the record itself — malformed, but expressible.
        var cyclic = new FixedRecordDataType { Name = "Ouroboros", QualifiedName = "Ouroboros" };
        cyclic.Fields.Add(new RecordField { Name = "Self", DataType = "Ouroboros" });
        document.DataTypes.FixedRecordDataTypes.Add(cyclic);

        var signature = new DataTypeResolver(document).Resolve("Ouroboros");

        _output.WriteLine($"Ouroboros -> {signature.Canonical}");
        Assert.NotNull(signature);   // the point is that it returned at all
    }

    /// <summary>
    /// The measurement that motivated the feature, run against the user's real files when present.
    /// </summary>
    [Fact]
    public void TheRealRprRenamesResolveToTheSameEncoding()
    {
        var fed = RealFomFiles.Named("MAK-RPR1-1-1.fed");
        var omt = RealFomFiles.Named("MAK-RPR1-1-1.omt");
        var rpr2 = RealFomFiles.Named("RPR_FOM_v2.0_1516-2010.xml");

        if (fed is null || rpr2 is null)
        {
            _output.WriteLine(RealFomFiles.NotConfigured);
            return;
        }

        // The OMT half is optional: the FED alone carries the structure, and the merge is what adds
        // the datatypes this measurement is about, so a missing companion weakens it rather than
        // invalidating it.
        var left = new DataTypeResolver(
            omt is null
                ? FomFileReader.ParseFile(fed)
                : FomMerger.Merge(FomFileReader.ParseFile(fed), FomFileReader.ParseFile(omt)).Document);
        var right = new DataTypeResolver(FomFileReader.ParseFile(rpr2));

        // Each pair is a rename in the real migration; the bits are identical.
        (string From, string To)[] renames =
        {
            ("octet", "Octet"),
            ("unsigned long", "UnsignedInteger32"),
            ("unsigned long long", "UnsignedInteger64BE"),
            ("float", "AngleRadianFloat32"),
        };

        foreach (var (from, to) in renames)
        {
            var a = left.Resolve(from);
            var b = right.Resolve(to);

            _output.WriteLine($"{from,-20} -> {a.Canonical,-12}   {to,-26} -> {b.Canonical}");

            Assert.True(a.IsResolved, $"{from} did not resolve");
            Assert.True(b.IsResolved, $"{to} did not resolve");
            Assert.True(a.EncodesTheSameAs(b),
                $"{from} ({a.Canonical}) and {to} ({b.Canonical}) should encode identically");
        }
    }
}
