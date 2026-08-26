using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using HLAFomReader.Core.Merging;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Reporting;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// The Excel export of a FOM's class trees. Two things have to hold for the sheet to be worth
/// anything: a class's name lands in the column matching its depth, and the member counts beside
/// it agree with what the detail screen shows for the same class.
/// </summary>
public sealed class ClassHierarchyExportTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

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

    private static FomDocument Restaurant() =>
        FomMerger.Merge(
            FomFileReader.ParseFile(Path.Combine(Samples, "RestaurantFOM-1.3.fed")),
            FomFileReader.ParseFile(Path.Combine(Samples, "RestaurantFOM-1.3.omt"))).Document;

    // ------------------------------------------------------------------- layout

    [Fact]
    public void TheWorkbookHasTheTwoNamedTabs()
    {
        var sheets = ClassHierarchyExporter.BuildSheets(Restaurant());

        Assert.Equal(2, sheets.Count);
        Assert.Equal(ClassHierarchyExporter.ObjectSheetName, sheets[0].Name);
        Assert.Equal(ClassHierarchyExporter.InteractionSheetName, sheets[1].Name);
    }

    [Fact]
    public void AClassNameSitsInTheColumnMatchingItsDepth()
    {
        var document = new FomDocument();
        var root = new FomObjectClass { Name = "ObjectRoot", QualifiedName = "ObjectRoot" };
        var middle = new FomObjectClass { Name = "BaseEntity", QualifiedName = "ObjectRoot.BaseEntity" };
        var leaf = new FomObjectClass { Name = "PhysicalEntity", QualifiedName = "ObjectRoot.BaseEntity.PhysicalEntity" };

        root.Children.Add(middle);
        middle.Children.Add(leaf);
        document.ObjectClasses.Add(root);

        var rows = ClassHierarchyExporter.BuildSheets(document)[0].Rows;

        // Row 0 is the header: Level 1, Level 2, Level 3, then the fact columns.
        Assert.Equal("Level 1", rows[0][0].Text);
        Assert.Equal("Level 2", rows[0][1].Text);
        Assert.Equal("Level 3", rows[0][2].Text);
        Assert.Equal("Qualified name", rows[0][3].Text);

        Assert.Equal("ObjectRoot", rows[1][0].Text);
        Assert.True(rows[1][1].IsEmpty);
        Assert.True(rows[1][2].IsEmpty);

        Assert.True(rows[2][0].IsEmpty);
        Assert.Equal("BaseEntity", rows[2][1].Text);
        Assert.True(rows[2][2].IsEmpty);

        Assert.True(rows[3][0].IsEmpty);
        Assert.True(rows[3][1].IsEmpty);
        Assert.Equal("PhysicalEntity", rows[3][2].Text);

        // The Level column repeats the depth as a number, so the sheet can be sorted and filtered.
        Assert.Equal(1d, rows[1][5].Number);
        Assert.Equal(2d, rows[2][5].Number);
        Assert.Equal(3d, rows[3][5].Number);
    }

    [Fact]
    public void RowsComeOutInDepthFirstOrder()
    {
        var sheet = ClassHierarchyExporter.BuildSheets(Restaurant())[0];

        var names = sheet.Rows.Skip(1)
            .Select(r => r.First(c => !c.IsEmpty).Text!)
            .ToList();

        var walked = new List<string>();
        void Walk(FomObjectClass c)
        {
            walked.Add(c.Name);
            foreach (var child in c.Children) Walk(child);
        }
        foreach (var root in Restaurant().ObjectClasses) Walk(root);

        Assert.Equal(walked, names);
    }

    // ------------------------------------------------------------------- merged blocks

    /// <summary>
    /// A parent's name is written once against the whole run of its descendants.
    /// </summary>
    /// <remarks>
    /// Unmerged, the staircase leaves a parent's name on its own row above a column of blanks, and
    /// which children belong to which parent has to be read off the indentation. Merged, the blocks
    /// in a column are the families.
    /// </remarks>
    [Fact]
    public void AParentsNameSpansTheRowsOfItsSubtree()
    {
        var sheet = ClassHierarchyExporter.BuildSheets(SmallTree())[0];

        //  row 2  ObjectRoot      level 1
        //  row 3    BaseEntity    level 2
        //  row 4      Physical    level 3
        //  row 5      Lifeform    level 3
        //  row 6    Other         level 2
        Assert.Equal(
            new[] { "A2:A6", "B3:B5" },
            sheet.Merges.Select(m => m.Reference).ToArray());
    }

    /// <summary>A class with no children spans one row, and a one-cell merge is not written.</summary>
    [Fact]
    public void ALeafIsNotMerged()
    {
        var document = new FomDocument();
        document.ObjectClasses.Add(new FomObjectClass { Name = "Alone", QualifiedName = "Alone" });

        Assert.Empty(ClassHierarchyExporter.BuildSheets(document)[0].Merges);
    }

    /// <summary>
    /// No two blocks claim the same cell, on a real FOM rather than a shaped one.
    /// </summary>
    /// <remarks>
    /// Overlapping ranges are the one way merging can produce a file Excel calls damaged, and it
    /// refuses the whole workbook rather than the range — so this is worth pinning against real
    /// input, where the trees are deeper and wider than anything hand-built here.
    /// </remarks>
    [Fact]
    public void MergedBlocksNeverOverlap()
    {
        foreach (var sheet in ClassHierarchyExporter.BuildSheets(Restaurant()))
        {
            var claimed = new HashSet<(int Row, int Column)>();

            foreach (var merge in sheet.Merges)
            {
                Assert.True(merge.LastRow > merge.FirstRow, $"{merge.Reference} spans a single row");
                Assert.Equal(merge.FirstColumn, merge.LastColumn);

                for (var row = merge.FirstRow; row <= merge.LastRow; row++)
                {
                    Assert.True(claimed.Add((row, merge.FirstColumn)),
                        $"{merge.Reference} overlaps a block already merged on sheet {sheet.Name}");
                }
            }
        }
    }

    /// <summary>
    /// Only the anchor row of a block carries the name; the rest of the block is left empty.
    /// </summary>
    /// <remarks>
    /// Excel keeps whatever is written into the covered cells but never shows it, so anything there
    /// is content that survives an unmerge and nobody expects.
    /// </remarks>
    [Fact]
    public void OnlyTheTopOfABlockCarriesTheName()
    {
        var rows = ClassHierarchyExporter.BuildSheets(SmallTree())[0].Rows;

        // ObjectRoot is merged A2:A6, so column A is written on row 2 and nowhere below it.
        Assert.Equal("ObjectRoot", rows[1][0].Text);
        for (var row = 2; row <= 5; row++)
            Assert.True(rows[row][0].IsEmpty, $"row {row + 1} wrote into a merged block");
    }

    /// <summary>The name anchoring a block is pinned to the top of it, not dropped to the bottom.</summary>
    /// <remarks>
    /// Excel aligns to the bottom of a cell by default, which would print a name level with the
    /// last of its descendants and nowhere near the row carrying its own counts.
    /// </remarks>
    [Fact]
    public void ABlocksNameIsAlignedToTheTop()
    {
        var rows = ClassHierarchyExporter.BuildSheets(SmallTree())[0].Rows;

        Assert.True(rows[1][0].TopAligned);
        Assert.True(rows[2][1].TopAligned);
    }

    /// <summary>The merges survive into the written file, in the place the schema puts them.</summary>
    [Fact]
    public void TheWorkbookCarriesTheMergedBlocks()
    {
        var stream = new MemoryStream();
        XlsxWriter.Write(stream, ClassHierarchyExporter.BuildSheets(SmallTree()));
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var part = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var worksheet = XDocument.Load(part).Root!;

        var refs = worksheet.Descendants(Main + "mergeCell")
            .Select(m => (string)m.Attribute("ref")!)
            .ToArray();

        Assert.Equal(new[] { "A2:A6", "B3:B5" }, refs);
        Assert.Equal("2", (string)worksheet.Element(Main + "mergeCells")!.Attribute("count")!);

        // mergeCells must follow sheetData; Excel rejects the file outright when it does not.
        var order = worksheet.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(order.IndexOf("mergeCells") > order.IndexOf("sheetData"),
            "mergeCells was written before sheetData");
    }

    /// <summary>The tree the merge tests are read against: two levels, and a branch that forks.</summary>
    private static FomDocument SmallTree()
    {
        var document = new FomDocument();

        var root = new FomObjectClass { Name = "ObjectRoot", QualifiedName = "ObjectRoot" };
        var middle = new FomObjectClass { Name = "BaseEntity", QualifiedName = "ObjectRoot.BaseEntity" };
        var other = new FomObjectClass { Name = "Other", QualifiedName = "ObjectRoot.Other" };

        middle.Children.Add(new FomObjectClass { Name = "Physical", QualifiedName = "ObjectRoot.BaseEntity.Physical" });
        middle.Children.Add(new FomObjectClass { Name = "Lifeform", QualifiedName = "ObjectRoot.BaseEntity.Lifeform" });

        root.Children.Add(middle);
        root.Children.Add(other);
        document.ObjectClasses.Add(root);

        return document;
    }

    // ------------------------------------------------------------------- theme and grid

    /// <summary>The palette handed in is the one the header band and the grid are painted with.</summary>
    [Fact]
    public void ThePaletteReachesTheHeaderAndTheGrid()
    {
        var styles = StylesOf(new XlsxPalette("FF14181D", "FFE6EBF1", "FF39434E"));

        // Header fill is the third: Excel assumes the first two are none and gray125. The fourth is
        // the white the blank staircase cells are painted with.
        var fills = styles.Element(Main + "fills")!.Elements(Main + "fill").ToList();
        Assert.Equal(4, fills.Count);
        Assert.Equal("FF14181D",
            (string)fills[2].Descendants(Main + "fgColor").Single().Attribute("rgb")!);

        // Header font is the second, and carries the header text colour.
        var fonts = styles.Element(Main + "fonts")!.Elements(Main + "font").ToList();
        Assert.Equal(2, fonts.Count);
        Assert.NotNull(fonts[1].Element(Main + "b"));
        Assert.Equal("FFE6EBF1", (string)fonts[1].Element(Main + "color")!.Attribute("rgb")!);

        // Border 1 is the ruled one; border 0 has to stay empty.
        var borders = styles.Element(Main + "borders")!.Elements(Main + "border").ToList();
        Assert.Equal(2, borders.Count);

        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            var edge = borders[1].Element(Main + side)!;
            Assert.Equal("thin", (string)edge.Attribute("style")!);
            Assert.Equal("FF39434E", (string)edge.Element(Main + "color")!.Attribute("rgb")!);
        }
    }

    /// <summary>
    /// Every cell format rules its cell but the one written for open page, so the only squares
    /// left unruled are the ones deliberately cleared.
    /// </summary>
    [Fact]
    public void OnlyTheOpenPageFormatGoesUnruled()
    {
        var formats = StylesOf(XlsxPalette.Default)
            .Element(Main + "cellXfs")!
            .Elements(Main + "xf")
            .ToList();

        Assert.Equal(5, formats.Count);
        Assert.All(formats.Take(4), xf => Assert.Equal("1", (string)xf.Attribute("borderId")!));
        Assert.Equal("0", (string)formats[4].Attribute("borderId")!);
    }

    /// <summary>
    /// Every cell of the used range is written, blanks included, so the grid is a rectangle.
    /// </summary>
    /// <remarks>
    /// A border belongs to a cell. The writer used to leave empty cells out — cheap, and most of a
    /// staircase is empty — but an absent cell is an unruled square, which would have left the grid
    /// shot through with holes exactly where the staircase is widest.
    /// </remarks>
    [Fact]
    public void EveryCellOfTheUsedRangeIsWritten()
    {
        var worksheet = WorksheetOf(Restaurant(), XlsxPalette.Default);
        var rows = worksheet.Element(Main + "sheetData")!.Elements(Main + "row").ToList();

        Assert.NotEmpty(rows);

        var widths = rows.Select(r => r.Elements(Main + "c").Count()).Distinct().ToList();
        Assert.True(widths.Count == 1, $"rows came out ragged: widths {string.Join(", ", widths)}");

        // And every one of them is styled, since an unstyled cell is an unruled one.
        Assert.All(rows.Elements(Main + "c"), c => Assert.NotNull(c.Attribute("s")));
    }

    /// <summary>The header row is the only one wearing the header format.</summary>
    [Fact]
    public void OnlyTheHeaderRowIsPaintedAsAHeader()
    {
        var rows = WorksheetOf(Restaurant(), XlsxPalette.Default)
            .Element(Main + "sheetData")!
            .Elements(Main + "row")
            .ToList();

        Assert.All(rows[0].Elements(Main + "c"), c => Assert.Equal("1", (string)c.Attribute("s")!));

        foreach (var row in rows.Skip(1))
            Assert.DoesNotContain(row.Elements(Main + "c"), c => (string)c.Attribute("s")! == "1");
    }

    /// <summary>
    /// A colour that cannot be read falls back rather than producing a workbook Excel refuses.
    /// </summary>
    /// <remarks>
    /// The palette comes out of a resource dictionary at runtime, so a missing or retyped key
    /// reaches here as a string nobody checked. One bad character writing straight through would
    /// cost the whole export for the sake of a shade nobody would have noticed.
    /// </remarks>
    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("GGGGGG")]
    [InlineData("FF00FF00FF")]
    public void AnUnreadableColourFallsBack(string bad)
    {
        Assert.Equal(XlsxPalette.Default.HeaderFill, HeaderFillOf(new XlsxPalette(bad, bad, bad)));
    }

    /// <summary>A colour written without its alpha is taken as opaque rather than rejected.</summary>
    [Theory]
    [InlineData("#1B2430", "FF1B2430")]
    [InlineData("1b2430", "FF1B2430")]
    [InlineData("#FF1B2430", "FF1B2430")]
    public void AColourIsNormalisedToTheEightDigitsExcelWants(string given, string expected)
    {
        Assert.Equal(expected, HeaderFillOf(new XlsxPalette(given, given, given)));
    }

    /// <summary>
    /// The header's fill colour as written. Index 2 of the fills, since Excel reserves the first
    /// two and index 3 is the white the blank staircase cells take.
    /// </summary>
    private static string HeaderFillOf(XlsxPalette palette) =>
        (string)StylesOf(palette)
            .Element(Main + "fills")!
            .Elements(Main + "fill")
            .ElementAt(2)
            .Descendants(Main + "fgColor")
            .Single()
            .Attribute("rgb")!;

    /// <summary>Two themes give two different files, which is the whole point of passing one.</summary>
    [Fact]
    public void ADifferentPaletteGivesADifferentWorkbook()
    {
        var document = Restaurant();
        var light = new MemoryStream();
        var dark = new MemoryStream();

        XlsxWriter.Write(light, ClassHierarchyExporter.BuildSheets(document),
            new XlsxPalette("FFEBEFF4", "FF1B2430", "FFC3CDD9"));
        XlsxWriter.Write(dark, ClassHierarchyExporter.BuildSheets(document),
            new XlsxPalette("FF14181D", "FFE6EBF1", "FF39434E"));

        Assert.NotEqual(light.ToArray(), dark.ToArray());
    }

    /// <summary>
    /// Every blank staircase cell is painted white rather than left unfilled.
    /// </summary>
    /// <remarks>
    /// The two look identical on a default worksheet and are not the same thing. An unfilled cell
    /// has no colour of its own and shows whatever Excel puts behind it, which under Office's dark
    /// theme is a dark grey — turning the empty half of the staircase into a dark field with the
    /// named cells punched out of it.
    /// </remarks>
    [Fact]
    public void EveryBlankStaircaseCellIsPaintedWhite()
    {
        var rows = ClassHierarchyExporter.BuildSheets(SmallTree())[0].Rows;

        // Row 2 is ObjectRoot at Level 1: the two blanks to its right are painted.
        Assert.True(rows[1][1].IsEmpty);
        Assert.True(rows[1][1].WhiteFill);
        Assert.True(rows[1][2].WhiteFill);

        // Row 4 is Physical at Level 3, so the blank at Level 2 to its left is painted too.
        Assert.True(rows[3][1].IsEmpty);
        Assert.True(rows[3][1].WhiteFill);

        // A cell holding a name is not a blank and is not painted.
        Assert.False(rows[3][2].IsEmpty);
        Assert.False(rows[3][2].WhiteFill);
    }

    /// <summary>
    /// A blank right of the name is unruled; one left of it keeps its border.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ruling a blank that belongs to no block draws an empty box around nothing, and enough of
    /// them turn the staircase into a form to fill in rather than a tree. Left of the name is a
    /// different case entirely: those blanks lie under an ancestor's merged block, and Excel builds
    /// a merged range's outline out of the borders of the cells it covers, so unruling them opens
    /// the bottom of every block taller than one row.
    /// </para>
    /// <para>
    /// The two cases split exactly on the name because every ancestor of a class has a child — the
    /// next one along the path down to it — and so every ancestor is merged.
    /// </para>
    /// </remarks>
    [Fact]
    public void BlanksRightOfTheNameAreUnruledAndBlanksLeftOfItAreNot()
    {
        var rows = ClassHierarchyExporter.BuildSheets(SmallTree())[0].Rows;

        // Row 2, ObjectRoot at Level 1: everything right of it is open page.
        Assert.True(rows[1][1].Unruled);
        Assert.True(rows[1][2].Unruled);

        // Row 4, Physical at Level 3: Level 2 to its left carries BaseEntity's block.
        Assert.True(rows[3][1].IsEmpty);
        Assert.False(rows[3][1].Unruled);

        // Row 5, Lifeform at Level 3: same, and nothing to its right to unrule.
        Assert.False(rows[4][1].Unruled);

        // Row 6, Other at Level 2: Level 3 to its right is open.
        Assert.True(rows[5][2].Unruled);
    }

    /// <summary>
    /// Nothing under a merged block is ever unruled, checked against a real FOM.
    /// </summary>
    /// <remarks>
    /// This is the failure the split exists to avoid, and it is invisible in the model — it shows
    /// up only as a block whose bottom edge is missing when somebody opens the sheet.
    /// </remarks>
    [Fact]
    public void NoMergedBlockIsLeftWithAnUnruledCell()
    {
        foreach (var sheet in ClassHierarchyExporter.BuildSheets(Restaurant()))
        {
            foreach (var merge in sheet.Merges)
            {
                for (var row = merge.FirstRow; row <= merge.LastRow; row++)
                {
                    // Rows are 1-based on the sheet and the header is row 1; columns likewise.
                    var cell = sheet.Rows[row - 1][merge.FirstColumn - 1];

                    Assert.False(cell.Unruled,
                        $"{merge.Reference} covers an unruled cell at row {row}, so the block opens");
                }
            }
        }
    }

    /// <summary>Fact columns stay ruled even where a class has nothing to put in one.</summary>
    /// <remarks>
    /// Sharing is blank for some classes. Those columns hold data, so dropping their borders would
    /// punch holes in a table rather than clear space beside a tree.
    /// </remarks>
    [Fact]
    public void ABlankFactColumnKeepsItsBorder()
    {
        var document = new FomDocument();
        document.ObjectClasses.Add(new FomObjectClass { Name = "Nameless", QualifiedName = "Nameless" });

        var sheet = ClassHierarchyExporter.BuildSheets(document)[0];
        var sharing = sheet.Rows[1][sheet.Rows[0].Count - 5];

        Assert.True(sharing.IsEmpty, "this test needs a class with no sharing recorded");
        Assert.False(sharing.Unruled);
    }

    /// <summary>Both kinds of blank reach the file as real styles, not just flags on the model.</summary>
    [Fact]
    public void ThePaintedBlanksReachTheWorkbook()
    {
        var worksheet = WorksheetOf(SmallTree(), XlsxPalette.Default);
        var rows = worksheet.Element(Main + "sheetData")!.Elements(Main + "row").ToList();

        var row2 = rows.First(r => (string)r.Attribute("r")! == "2").Elements(Main + "c").ToList();
        var row4 = rows.First(r => (string)r.Attribute("r")! == "4").Elements(Main + "c").ToList();

        // A2 holds ObjectRoot and is top-aligned; B2 and C2 are open page.
        Assert.Equal("2", (string)row2[0].Attribute("s")!);
        Assert.Equal("4", (string)row2[1].Attribute("s")!);
        Assert.Equal("4", (string)row2[2].Attribute("s")!);

        // B4 lies under BaseEntity's block, so it is the ruled kind.
        Assert.Equal("3", (string)row4[1].Attribute("s")!);

        var styles = StylesOf(XlsxPalette.Default);

        // Both blanks take the same white fill and differ only in the border.
        var fills = styles.Element(Main + "fills")!.Elements(Main + "fill").ToList();
        Assert.Equal(4, fills.Count);
        Assert.Equal("FFFFFFFF", (string)fills[3].Descendants(Main + "fgColor").Single().Attribute("rgb")!);

        var formats = styles.Element(Main + "cellXfs")!.Elements(Main + "xf").ToList();
        Assert.Equal(5, formats.Count);

        Assert.Equal("3", (string)formats[3].Attribute("fillId")!);
        Assert.Equal("1", (string)formats[3].Attribute("borderId")!);

        Assert.Equal("3", (string)formats[4].Attribute("fillId")!);
        Assert.Equal("0", (string)formats[4].Attribute("borderId")!);
    }

    /// <summary>The styles part of a workbook written with <paramref name="palette"/>.</summary>
    private static XElement StylesOf(XlsxPalette palette) => PartOf(palette, "xl/styles.xml");

    /// <summary>The first worksheet of a workbook built from <paramref name="document"/>.</summary>
    private static XElement WorksheetOf(FomDocument document, XlsxPalette palette)
    {
        var stream = new MemoryStream();
        XlsxWriter.Write(stream, ClassHierarchyExporter.BuildSheets(document), palette);
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var part = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        return XDocument.Load(part).Root!;
    }

    private static XElement PartOf(XlsxPalette palette, string entry)
    {
        var document = new FomDocument();
        document.ObjectClasses.Add(new FomObjectClass { Name = "ObjectRoot", QualifiedName = "ObjectRoot" });

        var stream = new MemoryStream();
        XlsxWriter.Write(stream, ClassHierarchyExporter.BuildSheets(document), palette);
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var part = zip.GetEntry(entry)!.Open();
        return XDocument.Load(part).Root!;
    }

    // ------------------------------------------------------------------- counts

    [Fact]
    public void DeclaredAndInheritedCountsSplitTheEffectiveSet()
    {
        var document = new FomDocument();
        var root = new FomObjectClass { Name = "ObjectRoot" };
        root.Attributes.Add(new FomAttribute { Name = "privilegeToDelete" });

        var child = new FomObjectClass { Name = "Beam" };
        child.Attributes.Add(new FomAttribute { Name = "Azimuth" });
        child.Attributes.Add(new FomAttribute { Name = "Elevation" });

        root.Children.Add(child);
        document.ObjectClasses.Add(root);

        var rows = ClassHierarchyExporter.BuildSheets(document)[0].Rows;

        // The tree is two deep, so columns run: Level 1, Level 2, Qualified name, Sharing,
        // Level, declared, inherited, total.
        Assert.Equal("Attributes declared", rows[0][5].Text);

        Assert.Equal(1d, rows[1][5].Number);   // ObjectRoot declares one
        Assert.Equal(0d, rows[1][6].Number);
        Assert.Equal(1d, rows[1][7].Number);

        Assert.Equal(2d, rows[2][5].Number);   // Beam declares two, inherits one
        Assert.Equal(1d, rows[2][6].Number);
        Assert.Equal(3d, rows[2][7].Number);
    }

    [Fact]
    public void ARedeclaredAttributeIsCountedOnceAgainstTheAncestor()
    {
        var document = new FomDocument();
        var root = new FomObjectClass { Name = "Root" };
        root.Attributes.Add(new FomAttribute { Name = "Shared" });

        var child = new FomObjectClass { Name = "Child" };
        child.Attributes.Add(new FomAttribute { Name = "Shared" });   // redeclares the inherited one
        child.Attributes.Add(new FomAttribute { Name = "Own" });

        root.Children.Add(child);
        document.ObjectClasses.Add(root);

        var row = ClassHierarchyExporter.BuildSheets(document)[0].Rows[2];

        // This mirrors the detail screen: the redeclaration does not add a second row, and the
        // ancestor keeps the attribute, so Child declares one and inherits one — three is wrong.
        Assert.Equal(1d, row[5].Number);
        Assert.Equal(1d, row[6].Number);
        Assert.Equal(2d, row[7].Number);
    }

    [Fact]
    public void InteractionParametersAreCountedTheSameWay()
    {
        var document = new FomDocument();
        var root = new FomInteractionClass { Name = "InteractionRoot" };
        var child = new FomInteractionClass { Name = "Ping" };
        child.Parameters.Add(new FomParameter { Name = "Sender" });
        child.Parameters.Add(new FomParameter { Name = "Stamp" });

        root.Children.Add(child);
        document.InteractionClasses.Add(root);

        var sheet = ClassHierarchyExporter.BuildSheets(document)[1];

        Assert.Equal("Parameters declared", sheet.Rows[0][5].Text);
        Assert.Equal(2d, sheet.Rows[2][5].Number);
        Assert.Equal(0d, sheet.Rows[2][6].Number);
    }

    [Fact]
    public void CountsAgreeWithTheEffectiveAttributeSet()
    {
        var document = Restaurant();
        var sheet = ClassHierarchyExporter.BuildSheets(document)[0];

        var byQualifiedName = document.ObjectClasses
            .SelectMany(c => c.DescendantsAndSelf())
            .ToDictionary(c => string.IsNullOrEmpty(c.QualifiedName) ? c.Name : c.QualifiedName);

        foreach (var row in sheet.Rows.Skip(1))
        {
            var levelColumns = sheet.Rows[0].Count - 6;
            var qualified = row[levelColumns].Text!;
            var objectClass = byQualifiedName[qualified];

            // Recompute the effective set independently: walk to the root, keep first sighting.
            var chain = new List<FomObjectClass>();
            for (var c = objectClass; c is not null; c = c.Parent) chain.Add(c);
            chain.Reverse();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var owner in chain)
                foreach (var attribute in owner.Attributes)
                    seen.Add(attribute.Name);

            Assert.Equal((double)seen.Count, row[levelColumns + 5].Number);
        }
    }

    // ------------------------------------------------------------------- the file itself

    [Fact]
    public void TheWorkbookIsAValidPackageOfWellFormedParts()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xlsx");

        try
        {
            ClassHierarchyExporter.Export(Restaurant(), path);

            using var zip = ZipFile.OpenRead(path);

            Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
            Assert.NotNull(zip.GetEntry("_rels/.rels"));
            Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
            Assert.NotNull(zip.GetEntry("xl/_rels/workbook.xml.rels"));
            Assert.NotNull(zip.GetEntry("xl/styles.xml"));
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet1.xml"));
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet2.xml"));

            foreach (var entry in zip.Entries)
            {
                using var stream = entry.Open();
                XDocument.Load(stream);   // throws if the part is not well-formed
            }

            // Every sheet the workbook lists must have a relationship pointing at a real part.
            using var workbook = zip.GetEntry("xl/workbook.xml")!.Open();
            var sheets = XDocument.Load(workbook).Descendants(Main + "sheet").ToList();
            Assert.Equal(2, sheets.Count);
            Assert.Equal(ClassHierarchyExporter.ObjectSheetName, (string)sheets[0].Attribute("name")!);
            Assert.Equal(ClassHierarchyExporter.InteractionSheetName, (string)sheets[1].Attribute("name")!);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExportingTheSameDocumentTwiceGivesTheSameBytes()
    {
        var document = Restaurant();
        var first = new MemoryStream();
        var second = new MemoryStream();

        XlsxWriter.Write(first, ClassHierarchyExporter.BuildSheets(document));
        XlsxWriter.Write(second, ClassHierarchyExporter.BuildSheets(document));

        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public void AFomWithNoInteractionsStillProducesBothTabs()
    {
        var document = new FomDocument();
        document.ObjectClasses.Add(new FomObjectClass { Name = "ObjectRoot" });

        var sheets = ClassHierarchyExporter.BuildSheets(document);

        Assert.Equal(2, sheets.Count);
        Assert.Equal(2, sheets[1].Rows.Count);                    // header plus the explanatory row
        Assert.Contains("no classes of this kind", sheets[1].Rows[1][0].Text);

        // And it still writes without complaint.
        var stream = new MemoryStream();
        XlsxWriter.Write(stream, sheets);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void AnEmptyDocumentDoesNotThrow()
    {
        var sheets = ClassHierarchyExporter.BuildSheets(new FomDocument());

        var stream = new MemoryStream();
        XlsxWriter.Write(stream, sheets);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void ACycleInTheClassTreeIsSurvived()
    {
        // Not something a parser produces, but the exporter must not spin if one ever appears.
        var document = new FomDocument();
        var a = new FomObjectClass { Name = "A" };
        var b = new FomObjectClass { Name = "B" };
        a.Children.Add(b);
        b.Children.Add(a);
        document.ObjectClasses.Add(a);

        var rows = ClassHierarchyExporter.BuildSheets(document)[0].Rows;

        Assert.Equal(3, rows.Count);   // header, A, B — and then it stops
    }

    // ------------------------------------------------------------------- writer mechanics

    [Theory]
    [InlineData(1, "A")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    [InlineData(52, "AZ")]
    [InlineData(53, "BA")]
    [InlineData(702, "ZZ")]
    [InlineData(703, "AAA")]
    public void ColumnNamesFollowExcelsLettering(int index, string expected) =>
        Assert.Equal(expected, XlsxWriter.ColumnName(index));

    [Fact]
    public void MarkupInAClassNameIsEscapedRatherThanEmitted()
    {
        var document = new FomDocument();
        document.ObjectClasses.Add(new FomObjectClass
        {
            Name = "Bell & Howell <t>",
            QualifiedName = "ObjectRoot.Bell & Howell <t>",
        });

        var stream = new MemoryStream();
        XlsxWriter.Write(stream, ClassHierarchyExporter.BuildSheets(document));
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();

        var text = XDocument.Load(sheet).Descendants(Main + "t").Select(t => t.Value).ToList();
        Assert.Contains("Bell & Howell <t>", text);
    }

    [Fact]
    public void SheetNamesAreTrimmedAndMadeUniqueForExcel()
    {
        var sheets = new[]
        {
            new XlsxSheet("Report: 2026/08 [draft]"),
            new XlsxSheet("Report: 2026/08 [draft]"),
        };
        sheets[0].Rows.Add(new[] { XlsxCell.Str("a") });
        sheets[1].Rows.Add(new[] { XlsxCell.Str("b") });

        var stream = new MemoryStream();
        XlsxWriter.Write(stream, sheets);
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var workbook = zip.GetEntry("xl/workbook.xml")!.Open();

        var names = XDocument.Load(workbook).Descendants(Main + "sheet")
            .Select(s => (string)s.Attribute("name")!).ToList();

        Assert.Equal("Report 202608 draft", names[0]);
        Assert.Equal("Report 202608 draft (2)", names[1]);
        Assert.All(names, n => Assert.True(n.Length <= 31));
    }

    [Fact]
    public void ALongSheetNameIsCutToExcelsLimit()
    {
        var sheet = new XlsxSheet(new string('x', 60));
        sheet.Rows.Add(new[] { XlsxCell.Str("a") });

        var stream = new MemoryStream();
        XlsxWriter.Write(stream, new[] { sheet });
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var workbook = zip.GetEntry("xl/workbook.xml")!.Open();

        var name = XDocument.Load(workbook).Descendants(Main + "sheet")
            .Select(s => (string)s.Attribute("name")!).Single();

        Assert.Equal(31, name.Length);
    }

    [Fact]
    public void AWorkbookNeedsAtLeastOneSheet() =>
        Assert.Throws<ArgumentException>(() => XlsxWriter.Write(new MemoryStream(), Array.Empty<XlsxSheet>()));
}
