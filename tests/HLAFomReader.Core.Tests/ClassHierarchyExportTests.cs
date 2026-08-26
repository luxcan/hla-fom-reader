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
