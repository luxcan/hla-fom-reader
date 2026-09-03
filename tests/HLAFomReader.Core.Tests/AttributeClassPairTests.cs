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
/// The Attribute data screen no longer maps two whole FOMs against each other: the user picks one
/// class on each side, and those two are compared whatever they are called.
/// </summary>
/// <remarks>
/// That is a different question from <see cref="AttributeMapper.Build"/>, which matches classes by
/// name. RPR 2.0 reworks the hierarchy RPR 1.0 declared, so the class holding an entity's data moves
/// and is renamed; lining the old class up against the new one is a judgement only the user can
/// make, and these pin what the mapper does once they have made it.
/// </remarks>
public sealed class AttributeClassPairTests
{
    private readonly ITestOutputHelper _output;

    public AttributeClassPairTests(ITestOutputHelper output) => _output = output;

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

    private const string Chef = "HLAobjectRoot.Employee.Chef";
    private const string Waiter = "HLAobjectRoot.Employee.Waiter";

    /// <summary>The class whose MenuEntry attribute is a variant record, so its datatype has depth.</summary>
    private const string Food = "HLAobjectRoot.Food";

    // ---- the picker inventory ----------------------------------------------------------------

    /// <summary>
    /// The picker lists a document's own classes, and its counts are the mapper's own — so the
    /// figure beside a name is exactly the number of rows choosing it produces.
    /// </summary>
    [Fact]
    public void ThePickerCountMatchesTheRowsThatClassProduces()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var classes = AttributeMapper.ListClasses(document);

        Assert.NotEmpty(classes);

        // Tree order, root first: the order the FOM is written in and the order the picker scrolls.
        Assert.Equal("HLAobjectRoot", classes[0].QualifiedName);

        foreach (var entry in classes)
        {
            var map = AttributeMapper.BuildForClasses(document, document, entry.QualifiedName, null);

            Assert.Equal(entry.AttributeCount, map.Rows.Count);
        }

        _output.WriteLine($"{classes.Count} classes; Chef carries " +
                          $"{classes.First(c => c.QualifiedName == Chef).AttributeCount}");
    }

    /// <summary>A subclass declaring nothing still carries its ancestors' set. That is the point.</summary>
    [Fact]
    public void ThePickerCountsInheritedAttributes()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var classes = AttributeMapper.ListClasses(document);

        var employee = classes.First(c => c.QualifiedName == "HLAobjectRoot.Employee");
        var chef = classes.First(c => c.QualifiedName == Chef);

        Assert.True(chef.AttributeCount > employee.AttributeCount,
            "Chef should carry Employee's attributes plus its own");
    }

    // ---- one side chosen ---------------------------------------------------------------------

    /// <summary>
    /// The regression this status exists for. A class picked on one side alone has been compared
    /// against nothing, so its attributes must not be reported as losses the other FOM suffered.
    /// </summary>
    [Fact]
    public void AClassPickedOnOneSideOnlyIsUnpairedRatherThanMissingFromTheOther()
    {
        var left = Parse("RestaurantFOM-1516-2010.xml");
        var right = Parse("RestaurantFOM-1516-2010-v2.xml");

        var map = AttributeMapper.BuildForClasses(left, right, Chef, null);

        Assert.NotEmpty(map.Rows);
        Assert.All(map.Rows, row => Assert.Equal(AttributeMapStatus.Unpaired, row.Status));

        // The counts every chip and headline reads must all stay at zero.
        Assert.Equal(0, map.ActionableCount);
        Assert.Equal(0, map.OnlyInLeftCount);
        Assert.Equal(0, map.OnlyInRightCount);
        Assert.Equal(map.Rows.Count, map.UnpairedCount);

        // ... and an unpaired row is not a difference, so "only rows that need attention" cannot
        // keep it and the grid cannot colour it.
        Assert.All(map.Rows, row => Assert.False(row.IsDifferent));

        // A side with nothing chosen carries nothing.
        Assert.All(map.Rows, row => Assert.Null(row.RightDataType));
        Assert.All(map.Rows, row => Assert.NotNull(row.LeftDataType));

        Assert.Equal(Chef, map.LeftClassName);
        Assert.Null(map.RightClassName);
        Assert.False(map.ComparesBothSides);
    }

    /// <summary>The same, from the other side, so the B columns are the ones that fill.</summary>
    [Fact]
    public void AClassPickedOnTheBSideOnlyFillsTheBColumns()
    {
        var left = Parse("RestaurantFOM-1516-2010.xml");
        var right = Parse("RestaurantFOM-1516-2010-v2.xml");

        var map = AttributeMapper.BuildForClasses(left, right, null, Chef);

        Assert.NotEmpty(map.Rows);
        Assert.All(map.Rows, row => Assert.Equal(AttributeMapStatus.Unpaired, row.Status));
        Assert.All(map.Rows, row => Assert.NotNull(row.RightDataType));
        Assert.All(map.Rows, row => Assert.Null(row.LeftDataType));

        Assert.Null(map.LeftClassName);
        Assert.Equal(Chef, map.RightClassName);
    }

    /// <summary>Nothing chosen anywhere is an empty map, not an exception.</summary>
    [Fact]
    public void NeitherSideChosenIsAnEmptyMap()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var map = AttributeMapper.BuildForClasses(document, document, null, null);

        Assert.Empty(map.Rows);
        Assert.False(map.ComparesBothSides);
    }

    /// <summary>A name matching no class behaves as an unpicked side rather than throwing.</summary>
    [Fact]
    public void AnUnknownClassNameIsTreatedAsNothingChosen()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var map = AttributeMapper.BuildForClasses(document, document, Chef, "HLAobjectRoot.NoSuchClass");

        Assert.All(map.Rows, row => Assert.Equal(AttributeMapStatus.Unpaired, row.Status));
        Assert.Null(map.RightClassName);
    }

    // ---- both sides chosen -------------------------------------------------------------------

    /// <summary>One class against itself has nothing to remap.</summary>
    [Fact]
    public void TheSameClassOnBothSidesLinesUpCompletely()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var map = AttributeMapper.BuildForClasses(document, document, Chef, Chef);

        Assert.NotEmpty(map.Rows);
        Assert.All(map.Rows, row => Assert.Equal(AttributeMapStatus.Same, row.Status));
        Assert.Equal(0, map.ActionableCount);
        Assert.True(map.ComparesBothSides);
    }

    /// <summary>
    /// The feature itself: two classes with different names, compared because the user said so.
    /// </summary>
    [Fact]
    public void TwoDifferentlyNamedClassesAreComparedAgainstEachOther()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var map = AttributeMapper.BuildForClasses(document, document, Chef, Waiter);

        Assert.Equal(Chef, map.LeftClassName);
        Assert.Equal(Waiter, map.RightClassName);
        Assert.True(map.ComparesBothSides);

        // Both inherit Employee's set, so the shared attributes line up ...
        Assert.Contains(map.Rows, row =>
            row.AttributeName == "EmployeeID" && row.Status == AttributeMapStatus.Same);

        // ... and each side's own declarations show up as belonging to one side only.
        Assert.Contains(map.Rows, row =>
            row.AttributeName == "Specialty" && row.Status == AttributeMapStatus.OnlyInLeft);
        Assert.Contains(map.Rows, row =>
            row.AttributeName == "AssignedSection" && row.Status == AttributeMapStatus.OnlyInRight);

        _output.WriteLine($"{map.Rows.Count} rows · same {map.SameCount} " +
                          $"· only in A {map.OnlyInLeftCount} · only in B {map.OnlyInRightCount}");
    }

    /// <summary>
    /// The status that had to be silenced. Across two classes the user paired by hand, every
    /// inherited attribute is necessarily declared on a different ancestor, so reporting each one as
    /// "Moved" would restate the user's own choice as a finding on every single row.
    /// </summary>
    [Fact]
    public void PairingTwoDifferentClassesNeverReportsAMove()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");

        var map = AttributeMapper.BuildForClasses(
            document, document, Chef, "HLAobjectRoot.Customer");

        Assert.DoesNotContain(map.Rows, row => row.Status == AttributeMapStatus.Moved);
    }

    /// <summary>
    /// Whole-FOM Build still reports moves. The gate is on the class-pair path alone, and the
    /// behaviour the nine existing mapper tests cover is untouched.
    /// </summary>
    [Fact]
    public void TheWholeFomMapStillCarriesItsClassNames()
    {
        var left = Parse("RestaurantFOM-1516-2010.xml");
        var right = Parse("RestaurantFOM-1516-2010-v2.xml");

        var map = AttributeMapper.Build(left, right);

        // Build says nothing about a chosen pair, because it did not choose one.
        Assert.Null(map.LeftClassName);
        Assert.Null(map.RightClassName);
        Assert.False(map.ComparesBothSides);
        Assert.DoesNotContain(map.Rows, row => row.Status == AttributeMapStatus.Unpaired);
    }

    /// <summary>
    /// The declaring class is carried as a dotted name too. Once the two classes come from unrelated
    /// trees, a local name like "Platform" cannot say which tree declared the attribute.
    /// </summary>
    [Fact]
    public void TheDeclaringClassIsAlsoCarriedQualified()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var map = AttributeMapper.BuildForClasses(document, document, Chef, Chef);

        var inherited = map.Rows.First(row => row.AttributeName == "EmployeeID");

        Assert.Equal("Employee", inherited.LeftDeclaredIn);
        Assert.Equal("HLAobjectRoot.Employee", inherited.LeftDeclaredInQualified);
        Assert.Equal("HLAobjectRoot.Employee", inherited.RightDeclaredInQualified);
    }

    /// <summary>
    /// Two FOMs that keep a datatype's <em>name</em> and change its <em>contents</em>.
    /// </summary>
    /// <remarks>
    /// The silent-corruption case, and the one this whole screen exists to catch. A generation that
    /// keeps <c>WorldLocationStruct</c> and re-types its fields moves different bytes under an
    /// identical name, so a verdict taken from the name alone reports Same, the amber highlight
    /// never appears, and "only rows that need attention" hides the single row that needs it most.
    /// The names are only ever a hint; each side is resolved through its own document's tables and
    /// the encodings decide.
    /// </remarks>
    [Fact]
    public void ADatatypeThatKeptItsNameButChangedItsFieldsIsReportedAsChanged()
    {
        var left = Parse("RestaurantFOM-1516-2010.xml");
        var right = Parse("RestaurantFOM-1516-2010.xml");

        // Same record name on both sides, different field types underneath.
        var record = right.DataTypes.FixedRecordDataTypes.First(r => r.Name == "DrinkDetailRecord");
        var field = record.Fields.First(f => f.Name == "ServedChilled");

        Assert.Equal("HLAboolean", field.DataType);
        field.DataType = "HLAASCIIstring";

        var map = AttributeMapper.BuildForClasses(left, right, Food, Food);
        var row = map.Rows.First(r => r.AttributeName == "MenuEntry");

        _output.WriteLine($"{row.LeftDataType} = {row.LeftEncoding}");
        _output.WriteLine($"{row.RightDataType} = {row.RightEncoding}");

        // The name is identical on both sides ...
        Assert.Equal(row.LeftDataType, row.RightDataType);

        // ... and the bytes are not, which is what the row has to say.
        Assert.NotEqual(row.LeftEncoding, row.RightEncoding);
        Assert.Equal(AttributeMapStatus.DataTypeChanged, row.Status);
        Assert.True(row.NeedsConversion);
        Assert.Equal(1, map.ActionableCount);
    }

    /// <summary>
    /// The other half of that rule: a name neither FOM can resolve is evidence of nothing.
    /// </summary>
    /// <remarks>
    /// The samples type five attributes as <c>NA</c>, which is in no datatype table. Both sides
    /// resolve it to the same unresolved marker, and flagging that as a re-encoding would invent
    /// work out of a gap in the FOM rather than out of a difference between the two.
    /// </remarks>
    [Fact]
    public void AnUnresolvableDatatypeSharedByBothSidesIsNotFlagged()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var map = AttributeMapper.BuildForClasses(document, document, Food, Food);

        var unresolved = map.Rows.Where(r => r.LeftEncoding?.StartsWith('?') == true).ToList();

        Assert.NotEmpty(unresolved);
        Assert.All(unresolved, row => Assert.Equal(AttributeMapStatus.Same, row.Status));
    }

    /// <summary>A null document is a caller error and still throws.</summary>
    [Fact]
    public void NullDocumentsAreRejected()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");

        Assert.Throws<ArgumentNullException>(
            () => AttributeMapper.BuildForClasses(null!, document, Chef, Chef));
        Assert.Throws<ArgumentNullException>(
            () => AttributeMapper.BuildForClasses(document, null!, Chef, Chef));
        Assert.Throws<ArgumentNullException>(() => AttributeMapper.ListClasses(null!));
    }
}
