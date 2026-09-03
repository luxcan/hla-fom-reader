using System;
using System.Collections.Generic;
using System.Linq;
using HLAFomReader.Core.Comparison;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// How the side-by-side sheet decides which member of one datatype sits opposite which member of
/// the other.
/// </summary>
/// <remarks>
/// The rule is deliberately asymmetric across roles, and these pin why. Record field order places
/// every byte on the wire — the resolver's canonical form keeps the order and throws the names away
/// for exactly that reason — so pairing fields positionally when their names disagree is evidence
/// rather than a guess. Nothing else in a FOM carries that guarantee.
/// </remarks>
public sealed class DataTypeMemberPairingTests
{
    private static DataTypeDetail Scalar(string name, string canonical) => new()
    {
        Name = name,
        Table = DataTypeTable.Basic,
        Shape = DataTypeShape.Scalar,
        Canonical = canonical,
    };

    private static DataTypeDetailMember Field(string name, string type, string canonical) => new()
    {
        Name = name,
        Role = DataTypeMemberRole.Field,
        Type = Scalar(type, canonical),
    };

    private static DataTypeDetailMember Element(string type, string canonical) => new()
    {
        Name = type,
        Role = DataTypeMemberRole.Element,
        Type = Scalar(type, canonical),
    };

    private static DataTypeDetailMember Alternative(string name, string enumerator) => new()
    {
        Name = name,
        Role = DataTypeMemberRole.Alternative,
        Value = enumerator,
        Type = Scalar(name + "Record", "uint:8"),
    };

    private static List<DataTypeDetailMember> Fields(params (string Name, string Type, string Canonical)[] items) =>
        items.Select(i => Field(i.Name, i.Type, i.Canonical)).ToList();

    /// <summary>Matching names pair, whatever order the two sides declare them in.</summary>
    [Fact]
    public void FieldsPairOnTheirNamesFirst()
    {
        var left = Fields(("X", "float64", "float:64"), ("Y", "float64", "float:64"));
        var right = Fields(("Y", "double", "float:64"), ("X", "double", "float:64"));

        var pairs = DataTypeMemberPairing.Pair(left, right);

        Assert.Equal(2, pairs.Count);

        // A's declaration order leads, which is the order the reader is working through.
        Assert.Equal("X", pairs[0].Left!.Name);
        Assert.Equal("X", pairs[0].Right!.Name);
        Assert.Equal("Y", pairs[1].Left!.Name);
        Assert.Equal("Y", pairs[1].Right!.Name);

        Assert.All(pairs, p => Assert.False(p.PairedByPosition));
    }

    /// <summary>
    /// The case the fallback exists for: a version step renames every field of a record without
    /// touching a single byte.
    /// </summary>
    [Fact]
    public void FieldsWhoseNamesAllChangedPairByPosition()
    {
        var left = Fields(("X", "float64", "float:64"), ("Y", "float64", "float:64"), ("Z", "float64", "float:64"));
        var right = Fields(("XPos", "double", "float:64"), ("YPos", "double", "float:64"), ("ZPos", "double", "float:64"));

        var pairs = DataTypeMemberPairing.Pair(left, right);

        Assert.Equal(3, pairs.Count);
        Assert.Equal(new[] { "XPos", "YPos", "ZPos" }, pairs.Select(p => p.Right!.Name));

        // Flagged, every one: the reader has to know the FOM did not assert this pairing.
        Assert.All(pairs, p => Assert.True(p.PairedByPosition));
    }

    /// <summary>
    /// Three fields against five. Nothing is dropped and nothing is invented: the residual zips, and
    /// what is left over stands alone on its own side.
    /// </summary>
    [Fact]
    public void SurplusFieldsBecomeOneSidedRows()
    {
        var left = Fields(("A", "u8", "uint:8"), ("B", "u8", "uint:8"), ("C", "u8", "uint:8"));
        var right = Fields(
            ("A", "u8", "uint:8"), ("P", "u8", "uint:8"), ("Q", "u8", "uint:8"),
            ("R", "u8", "uint:8"), ("S", "u8", "uint:8"));

        var pairs = DataTypeMemberPairing.Pair(left, right);

        // Every member of both sides is accounted for exactly once.
        Assert.Equal(3, pairs.Count(p => p.Left is not null));
        Assert.Equal(5, pairs.Count(p => p.Right is not null));

        // A matched by name; B and C zipped onto the first two unmatched B fields.
        Assert.False(pairs[0].PairedByPosition);
        Assert.Equal("A", pairs[0].Right!.Name);
        Assert.Equal("P", pairs[1].Right!.Name);
        Assert.Equal("Q", pairs[2].Right!.Name);
        Assert.True(pairs[1].PairedByPosition);

        // The two B fields nothing reached come last, on their own.
        var orphans = pairs.Where(p => p.Left is null).Select(p => p.Right!.Name).ToList();
        Assert.Equal(new[] { "R", "S" }, orphans);
    }

    /// <summary>
    /// An array element is the type of every slot, not a named thing. Its "name" is a type name, so
    /// matching on it would split one element into two half-rows that compare nothing.
    /// </summary>
    [Fact]
    public void ArrayElementsPairEvenWhenTheirTypesAreNamedDifferently()
    {
        var left = new List<DataTypeDetailMember> { Element("float64", "float:64") };
        var right = new List<DataTypeDetailMember> { Element("double", "float:64") };

        var pairs = DataTypeMemberPairing.Pair(left, right);

        Assert.Single(pairs);
        Assert.NotNull(pairs[0].Left);
        Assert.NotNull(pairs[0].Right);

        // Not a positional fallback: the two are the same slot by definition, not by inference.
        Assert.False(pairs[0].PairedByPosition);
    }

    /// <summary>
    /// A variant's alternatives answer to a discriminant value, so that is what identifies them.
    /// Renaming the field carrying an alternative leaves the same value selecting the same bytes.
    /// </summary>
    [Fact]
    public void AlternativesPairOnTheirSelectingEnumerator()
    {
        var left = new List<DataTypeDetailMember>
        {
            Alternative("DrinkDetail", "DrinkItem"),
            Alternative("EntreeDetail", "EntreeItem"),
        };

        var right = new List<DataTypeDetailMember>
        {
            Alternative("EntreeInfo", "EntreeItem"),
            Alternative("BeverageDetail", "DrinkItem"),
        };

        var pairs = DataTypeMemberPairing.Pair(left, right);

        Assert.Equal(2, pairs.Count);
        Assert.Equal("BeverageDetail", pairs[0].Right!.Name);
        Assert.Equal("EntreeInfo", pairs[1].Right!.Name);

        // Never positional: an alternative sharing an index but not a selector is unrelated.
        Assert.All(pairs, p => Assert.False(p.PairedByPosition));
    }

    /// <summary>Two roles never pair with each other, however few of each are left over.</summary>
    [Fact]
    public void RolesNeverCrossPair()
    {
        var left = new List<DataTypeDetailMember> { Field("Payload", "u8", "uint:8") };
        var right = new List<DataTypeDetailMember> { Element("u8", "uint:8") };

        var pairs = DataTypeMemberPairing.Pair(left, right);

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, p => Assert.True(p.Left is null || p.Right is null));
    }

    /// <summary>An empty side leaves the other standing alone rather than vanishing.</summary>
    [Fact]
    public void AnEmptySideLeavesTheOtherIntact()
    {
        var left = Fields(("A", "u8", "uint:8"), ("B", "u8", "uint:8"));

        var pairs = DataTypeMemberPairing.Pair(left, Array.Empty<DataTypeDetailMember>());

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, p => Assert.Null(p.Right));

        Assert.Empty(DataTypeMemberPairing.Pair(null, null));
    }
}
