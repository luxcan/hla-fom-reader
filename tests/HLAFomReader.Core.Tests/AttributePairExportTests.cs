using System;
using System.IO;
using System.Linq;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Reporting;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// The side-by-side remap worksheet: two chosen classes, their attributes, and the structure inside
/// each attribute's datatype unfolded a level at a time with FOM A's columns beside FOM B's.
/// </summary>
/// <remarks>
/// The screen says which attributes re-encode. This says <em>where</em> inside them, which is where
/// the conversion someone has to write actually lives, and it is the only artefact of that question
/// that leaves the app.
/// </remarks>
public sealed class AttributePairExportTests
{
    private readonly ITestOutputHelper _output;

    public AttributePairExportTests(ITestOutputHelper output) => _output = output;

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

    /// <summary>The class whose MenuEntry attribute is a variant record, and so nests three deep.</summary>
    private const string Food = "HLAobjectRoot.Food";

    private const string Chef = "HLAobjectRoot.Employee.Chef";

    /// <summary>Chef's sibling: shares the inherited set, declares different attributes of its own.</summary>
    private const string Waiter = "HLAobjectRoot.Employee.Waiter";

    private static AttributePairSheet Sheet(
        FomDocument left, FomDocument right, string? classA, string? classB,
        AttributePairSheetOptions? options = null)
    {
        var map = AttributeMapper.BuildForClasses(left, right, classA, classB);
        return AttributePairExporter.Build(map, map.Rows, left, right, options);
    }

    /// <summary>Every attribute of the pair earns a depth-1 row, and the header names the columns.</summary>
    [Fact]
    public void EveryAttributeGetsALevelOneRow()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var map = AttributeMapper.BuildForClasses(document, document, Chef, Chef);
        var sheet = AttributePairExporter.Build(map, map.Rows, document, document);

        var levelOne = sheet.Rows.Where(r => r.Depth == 1).ToList();

        Assert.Equal(map.Rows.Count, levelOne.Count);
        Assert.All(levelOne, r => Assert.Equal("Attribute", r.Kind));
        Assert.All(levelOne, r => Assert.Null(r.Role));

        Assert.Equal(Chef, sheet.ClassA);
        Assert.Equal(Chef, sheet.ClassB);
    }

    /// <summary>
    /// The point of the sheet. A variant record unfolds into its discriminant and alternatives at
    /// level 2, and their record fields at level 3.
    /// </summary>
    [Fact]
    public void ADatatypeUnfoldsThroughThreeLevels()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var sheet = Sheet(document, document, Food, Food);

        var block = sheet.Rows
            .SkipWhile(r => r.AttributeName != "MenuEntry")
            .TakeWhile(r => r.AttributeName == "MenuEntry")
            .ToList();

        foreach (var row in block)
            _output.WriteLine($"{row.Depth} {row.Kind,-14} {row.NameA} : {row.DataTypeA} = {row.EncodingA}");

        // Level 1: the attribute, typed as the variant.
        Assert.Equal(1, block[0].Depth);
        Assert.Equal("MenuItemVariant", block[0].DataTypeA);

        // Level 2: the discriminant and the two alternatives.
        Assert.Contains(block, r => r.Depth == 2 && r.Role == DataTypeMemberRole.Discriminant);
        Assert.Contains(block, r => r.Depth == 2 && r.Role == DataTypeMemberRole.Alternative
                                    && r.NameA == "DrinkDetail");

        // Level 3: the fields inside an alternative's record.
        Assert.Contains(block, r => r.Depth == 3 && r.Role == DataTypeMemberRole.Field
                                    && r.NameA == "ServedChilled");

        // ... and nothing deeper, because the sheet stops at three.
        Assert.All(block, r => Assert.True(r.Depth <= 3));

        // Those level-3 fields are enumerations, whose only members are the suppressed
        // representation and enumerator rows. Nothing was withheld, so nothing claims it was: the
        // cut-short note has to mean "there is more here", not "this is as deep as I went".
        Assert.All(block, r => Assert.DoesNotContain("stops at level", r.Note ?? ""));
    }

    /// <summary>
    /// A row that really does have structure beneath the cap says so, rather than presenting a
    /// record as though it were a leaf.
    /// </summary>
    [Fact]
    public void ARowCutShortByTheDepthCapSaysSo()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var sheet = Sheet(document, document, Food, Food, new AttributePairSheetOptions { MaxDepth = 2 });

        var alternative = sheet.Rows.First(
            r => r.AttributeName == "MenuEntry" && r.NameA == "DrinkDetail");

        Assert.Contains("stops at level 2", alternative.Note ?? "");
    }

    /// <summary>
    /// A representation is not a level-2 row by default: the attribute's own Encoding column already
    /// <em>is</em> the representation's canonical form, so the row would restate it.
    /// </summary>
    [Fact]
    public void ASimpleTypesRepresentationIsSuppressedByDefault()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var sheet = Sheet(document, document, Chef, Chef);

        Assert.DoesNotContain(sheet.Rows, r => r.Role == DataTypeMemberRole.Representation);
        Assert.DoesNotContain(sheet.Rows, r => r.Role == DataTypeMemberRole.Enumerator);

        // Both are available for a reader who wants them.
        var opened = Sheet(document, document, Chef, Chef,
            new AttributePairSheetOptions { IncludeRepresentation = true, IncludeEnumerators = true });

        Assert.Contains(opened.Rows, r => r.Role == DataTypeMemberRole.Representation);
    }

    /// <summary>The depth cap is honoured, and level 1 alone is a legitimate request.</summary>
    [Fact]
    public void TheDepthCapIsHonoured()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");

        var flat = Sheet(document, document, Food, Food, new AttributePairSheetOptions { MaxDepth = 1 });
        Assert.All(flat.Rows, r => Assert.Equal(1, r.Depth));

        var twoDeep = Sheet(document, document, Food, Food, new AttributePairSheetOptions { MaxDepth = 2 });
        Assert.Contains(twoDeep.Rows, r => r.Depth == 2);
        Assert.DoesNotContain(twoDeep.Rows, r => r.Depth == 3);
    }

    /// <summary>Fan-out is bounded separately from depth, and the cut is announced rather than silent.</summary>
    [Fact]
    public void RunawayFanOutIsCutAndSaidSo()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");

        var sheet = Sheet(document, document, Food, Food,
            new AttributePairSheetOptions { MaxRowsPerAttribute = 2 });

        var block = sheet.Rows.Where(r => r.AttributeName == "MenuEntry").ToList();

        Assert.Contains(block, r => r.Note is not null
                                    && r.Note.Contains("Expansion stopped after 2 rows", StringComparison.Ordinal));
    }

    /// <summary>
    /// With a class on one side only, every row is Unpaired all the way down. A nested row must not
    /// claim the other FOM is missing a field it was never asked about.
    /// </summary>
    [Fact]
    public void AOneSidedSheetIsUnpairedAtEveryLevel()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var sheet = Sheet(document, document, Food, null);

        Assert.NotEmpty(sheet.Rows);
        Assert.All(sheet.Rows, r => Assert.Equal(AttributeMapStatus.Unpaired, r.Match));
        Assert.All(sheet.Rows, r => Assert.Null(r.NameB));

        Assert.Null(sheet.ClassB);
    }

    // ---- the worksheet ------------------------------------------------------------------------

    /// <summary>
    /// The layout: FOM A's staircase, a two-column gutter, then FOM B's, and a Note column.
    /// </summary>
    [Fact]
    public void TheWorksheetPutsTheTwoSidesEitherSideOfAGutter()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var sheet = Sheet(document, document, Food, Food);
        var built = AttributePairExporter.ToSheet(sheet);

        // Two header rows: which FOM and class each half is, then the columns.
        Assert.Equal(2, built.FrozenRows);
        Assert.Equal(sheet.Rows.Count + 2, built.Rows.Count);

        var banner = built.Rows[0];
        var columns = built.Rows[1];

        // MenuEntry nests three deep, so both halves carry Level 1, 2 and 3.
        var text = columns.Select(c => c.Text).ToList();
        Assert.Equal(
            new[] { "Level 1", "Level 2", "Level 3", "DataType", "Encoding" },
            text.Take(5));

        // Two blank columns, then the same five again.
        Assert.Null(text[5]);
        Assert.Null(text[6]);
        Assert.Equal(
            new[] { "Level 1", "Level 2", "Level 3", "DataType", "Encoding" },
            text.Skip(7).Take(5));

        Assert.Equal("Note", text[12]);
        Assert.Equal(13, columns.Count);

        // The banner is the only place the sheet says which pair it came from, now that the class
        // is no longer a column repeated against every row.
        Assert.StartsWith("FOM A", banner[0].Text);
        Assert.Contains(Food, banner[0].Text);
        Assert.StartsWith("FOM B", banner[7].Text);

        _output.WriteLine(string.Join(" | ", text.Select(t => t ?? "")));
    }

    /// <summary>
    /// The staircase itself: a name sits in the column matching its depth, and nowhere else.
    /// </summary>
    [Fact]
    public void ANameSitsInTheColumnMatchingItsLevel()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var sheet = Sheet(document, document, Food, Food);
        var built = AttributePairExporter.ToSheet(sheet);

        for (var i = 0; i < sheet.Rows.Count; i++)
        {
            var source = sheet.Rows[i];
            var cells = built.Rows[i + 2];

            if (source.NameA is null) continue;

            for (var level = 1; level <= 3; level++)
            {
                var cell = cells[level - 1];

                if (level == source.Depth) Assert.Equal(source.NameA, cell.Text);
                else Assert.Null(cell.Text);
            }
        }

        // Worked example, so the shape is visible rather than merely asserted.
        foreach (var row in built.Rows.Skip(2).Take(8))
            _output.WriteLine("[" + string.Join("] [", row.Take(5).Select(c => c.Text ?? "")) + "]");
    }

    /// <summary>
    /// A side with no counterpart says so once, in its Level 1 column, and stays blank below.
    /// </summary>
    [Fact]
    public void AMissingCounterpartIsNamedOnceInLevelOne()
    {
        var left = Parse("RestaurantFOM-1516-2010.xml");
        var right = Parse("RestaurantFOM-1516-2010.xml");

        // Chef declares Specialty; Waiter does not, so pairing the two leaves it with no B side.
        var map = AttributeMapper.BuildForClasses(left, right, Chef, Waiter);
        var sheet = AttributePairExporter.Build(map, map.Rows, left, right);
        var built = AttributePairExporter.ToSheet(sheet);

        var depth = sheet.Rows.Max(r => r.Depth);

        var index = sheet.Rows
            .Select((row, i) => (row, i))
            .First(x => x.row.Depth == 1 && x.row.NameA == "Specialty").i;

        var cells = built.Rows[index + 2];

        // A's Level 1 holds the attribute; B's Level 1 holds the phrase, in the mirrored position.
        Assert.Equal("Specialty", cells[0].Text);
        Assert.Equal(AttributePairExporter.NotFoundText, cells[depth + 2 + 2].Text);

        // ... and nothing else on B's side pretends to know anything.
        Assert.Null(cells[depth + 2 + 2 + depth].Text);
    }

    /// <summary>
    /// With one class chosen, the other half is simply blank. Nothing was looked for there, so
    /// nothing may be reported as not found.
    /// </summary>
    [Fact]
    public void AnUnchosenSideIsBlankRatherThanNotFound()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var sheet = Sheet(document, document, Food, null);
        var built = AttributePairExporter.ToSheet(sheet);

        Assert.DoesNotContain(
            built.Rows.SelectMany(r => r),
            c => c.Text == AttributePairExporter.NotFoundText);

        // B's half starts after A's levels, its two fact columns and the gutter.
        var depth = sheet.Rows.Max(r => r.Depth);
        Assert.Contains("no class chosen", built.Rows[0][depth + 4].Text ?? "");
    }

    /// <summary>Only as many level columns as the rows actually reach.</summary>
    [Fact]
    public void TheStaircaseIsOnlyAsWideAsTheDeepestRow()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");

        // Chef's attributes are scalars and strings; nothing nests past level 2.
        var flat = AttributePairExporter.ToSheet(
            Sheet(document, document, Chef, Chef, new AttributePairSheetOptions { MaxDepth = 1 }));

        var headers = flat.Rows[1].Select(c => c.Text).Where(t => t is not null).ToList();

        // One per side, and no deeper column at all.
        Assert.Equal(2, headers.Count(t => t == "Level 1"));
        Assert.DoesNotContain("Level 2", headers);
    }

    /// <summary>
    /// A name is joined down the block of rows its own members occupy, which is what turns the
    /// staircase into the table people draw by hand.
    /// </summary>
    [Fact]
    public void ANameIsMergedDownItsOwnBlock()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var sheet = Sheet(document, document, Food, Food);
        var built = AttributePairExporter.ToSheet(sheet);

        var depth = sheet.Rows.Max(r => r.Depth);
        var rightColumn = depth + 2 + 2 + 1;

        // MenuEntry is a variant record: one level-1 row followed by everything inside it.
        var first = sheet.Rows.Select((r, i) => (r, i))
            .First(x => x.r.Depth == 1 && x.r.NameA == "MenuEntry").i;

        var last = first;
        while (last + 1 < sheet.Rows.Count && sheet.Rows[last + 1].Depth > 1) last++;

        Assert.True(last > first, "MenuEntry unfolded into nothing, so there is no block to merge");

        // Body rows start under the two header rows; Excel counts from one.
        var expected = new XlsxMerge(3 + first, 1, 3 + last, 1);
        Assert.Contains(expected, built.Merges);

        // The same block on the other side of the gutter, in B's own Level 1 column.
        Assert.Contains(new XlsxMerge(3 + first, rightColumn, 3 + last, rightColumn), built.Merges);

        // A cell a block runs through keeps its border, or Excel opens the bottom of the block.
        Assert.True(built.Rows[3 + first][0].WhiteFill);
        Assert.False(built.Rows[3 + first][0].Unruled);

        // An attribute that unfolds into nothing spans one row and is not merged at all.
        var scalar = sheet.Rows.Select((r, i) => (r, i))
            .First(x => x.r.Depth == 1 && (x.i + 1 == sheet.Rows.Count || sheet.Rows[x.i + 1].Depth == 1)).i;

        Assert.DoesNotContain(built.Merges, m => m.FirstRow == 3 + scalar && m.FirstColumn == 1);
    }

    /// <summary>
    /// "Attribute not found" is joined down the block too, so it covers the whole of the missing
    /// side rather than sitting on one row with blanks beneath it.
    /// </summary>
    [Fact]
    public void TheNotFoundPhraseIsMergedDownTheBlockItCovers()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");

        // Chef declares Specialty, an HLAASCIIstring, so it unfolds; Waiter has no such attribute.
        var map = AttributeMapper.BuildForClasses(document, document, Chef, Waiter);
        var sheet = AttributePairExporter.Build(map, map.Rows, document, document);
        var built = AttributePairExporter.ToSheet(sheet);

        var depth = sheet.Rows.Max(r => r.Depth);
        var rightColumn = depth + 2 + 2 + 1;

        var first = sheet.Rows.Select((r, i) => (r, i))
            .First(x => x.r.Depth == 1 && x.r.NameA == "Specialty").i;

        var last = first;
        while (last + 1 < sheet.Rows.Count && sheet.Rows[last + 1].Depth > 1) last++;

        Assert.True(last > first, "Specialty unfolded into nothing");

        Assert.Equal(AttributePairExporter.NotFoundText, built.Rows[2 + first][rightColumn - 1].Text);
        Assert.Contains(new XlsxMerge(3 + first, rightColumn, 3 + last, rightColumn), built.Merges);
    }

    /// <summary>
    /// Each banner is merged across its own half, so the line naming the FOM and class is laid out
    /// across the whole span instead of being clipped at the first column's width.
    /// </summary>
    /// <remarks>
    /// The bold padding cells that carry the header fill hold an empty string, and Excel counts a
    /// cell holding an empty string as occupied — so the banner cannot spill across them.
    /// </remarks>
    [Fact]
    public void EachBannerIsMergedAcrossItsOwnHalf()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var sheet = Sheet(document, document, Chef, Chef);
        var built = AttributePairExporter.ToSheet(sheet);

        var depth = sheet.Rows.Max(r => r.Depth);
        var half = depth + 2;

        Assert.Contains(new XlsxMerge(1, 1, 1, half), built.Merges);
        Assert.Contains(new XlsxMerge(1, half + 2 + 1, 1, half * 2 + 2), built.Merges);

        // Each merge ends where its own half does, so neither runs into the gutter or off the sheet.
        var width = built.Rows.Max(r => r.Count);
        Assert.All(built.Merges, m => Assert.True(m.LastColumn <= width));

        // The text the merge exists to keep readable is longer than the column it is anchored in.
        var banner = built.Rows[0][0].Text!;
        Assert.Contains(Chef, banner);
        Assert.True(banner.Length > 28, "the banner would have fitted anyway, so this proves nothing");
    }

    /// <summary>The workbook is written and opens as a zip container, which is what .xlsx is.</summary>
    [Fact]
    public void TheWorkbookIsWritten()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var sheet = Sheet(document, document, Chef, Chef);

        var path = Path.Combine(Path.GetTempPath(), $"hlafomreader-pair-{Guid.NewGuid():N}.xlsx");

        try
        {
            AttributePairExporter.WriteXlsx(sheet, path);

            Assert.True(File.Exists(path));

            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            Assert.Contains(archive.Entries, e => e.FullName == "xl/workbook.xml");
            Assert.Contains(archive.Entries, e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>A null argument is a caller error; bad content is not.</summary>
    [Fact]
    public void NullArgumentsAreRejected()
    {
        var document = Parse("RestaurantFOM-1516-2010.xml");
        var map = AttributeMapper.BuildForClasses(document, document, Chef, Chef);

        Assert.Throws<ArgumentNullException>(
            () => AttributePairExporter.Build(null!, map.Rows, document, document));
        Assert.Throws<ArgumentNullException>(
            () => AttributePairExporter.Build(map, map.Rows, null!, document));
        Assert.Throws<ArgumentNullException>(() => AttributePairExporter.ToSheet(null!));
    }

}
