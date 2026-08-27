using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HLAFomReader.Core.Merging;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Reporting;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// The member tabs an export gains when the user picks classes to see in full.
/// </summary>
/// <remarks>
/// Three things have to hold. The picked classes are the ones that appear and no others; each one
/// brings the attributes it actually has, inherited ones included, marked as inherited; and the
/// hierarchy tabs are untouched by any of it, because the selection adds detail rather than
/// filtering the model.
/// </remarks>
public sealed class ClassMemberExportTests
{
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

    // ------------------------------------------------------------------- which tabs appear

    /// <summary>An empty selection is the workbook this export produced before there was a picker.</summary>
    [Fact]
    public void NothingPickedLeavesTheWorkbookAsItWas()
    {
        var sheets = ClassHierarchyExporter.BuildSheets(Restaurant(), ClassExportSelection.None);

        Assert.Equal(2, sheets.Count);
        Assert.Equal(ClassHierarchyExporter.ObjectSheetName, sheets[0].Name);
        Assert.Equal(ClassHierarchyExporter.InteractionSheetName, sheets[1].Name);
    }

    /// <summary>A kind nobody picked contributes no tab, rather than an empty one.</summary>
    [Fact]
    public void OnlyThePickedKindEarnsATab()
    {
        var document = Tree();

        var objectsOnly = ClassHierarchyExporter.BuildSheets(
            document, new ClassExportSelection(new[] { "ObjectRoot.Meal" }, null));

        Assert.Equal(3, objectsOnly.Count);
        Assert.Equal(ClassMemberExporter.AttributeSheetName, objectsOnly[2].Name);

        var interactionsOnly = ClassHierarchyExporter.BuildSheets(
            document, new ClassExportSelection(null, new[] { "InteractionRoot.Order" }));

        Assert.Equal(3, interactionsOnly.Count);
        Assert.Equal(ClassMemberExporter.ParameterSheetName, interactionsOnly[2].Name);
    }

    /// <summary>Both kinds picked gives four tabs, hierarchies first.</summary>
    [Fact]
    public void BothKindsPickedGiveFourTabsWithTheHierarchiesFirst()
    {
        var sheets = ClassHierarchyExporter.BuildSheets(
            Tree(), new ClassExportSelection(new[] { "ObjectRoot.Meal" }, new[] { "InteractionRoot.Order" }));

        Assert.Equal(
            new[]
            {
                ClassHierarchyExporter.ObjectSheetName,
                ClassHierarchyExporter.InteractionSheetName,
                ClassMemberExporter.AttributeSheetName,
                ClassMemberExporter.ParameterSheetName,
            },
            sheets.Select(s => s.Name).ToArray());
    }

    /// <summary>
    /// Every tab caption survives Excel's rules on its own, so the writer never has to rename one.
    /// </summary>
    /// <remarks>
    /// The writer sanitises and de-duplicates names rather than failing, which means a caption too
    /// long or carrying a forbidden character would be silently trimmed to something else. Pinning
    /// the captions here keeps the constants and the documentation describing the same tabs.
    /// </remarks>
    [Fact]
    public void EveryTabCaptionIsAlreadyLegal()
    {
        var names = new[]
        {
            ClassHierarchyExporter.ObjectSheetName,
            ClassHierarchyExporter.InteractionSheetName,
            ClassMemberExporter.AttributeSheetName,
            ClassMemberExporter.ParameterSheetName,
        };

        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var name in names)
        {
            Assert.InRange(name.Length, 1, 31);
            Assert.DoesNotContain(name, c => "[]:*?/\\".Contains(c));
        }
    }

    /// <summary>A picked class that this document does not hold contributes nothing at all.</summary>
    [Fact]
    public void AClassTheDocumentDoesNotHoldIsIgnored()
    {
        var sheets = ClassHierarchyExporter.BuildSheets(
            Tree(), new ClassExportSelection(new[] { "ObjectRoot.NoSuchClass" }, null));

        Assert.Equal(2, sheets.Count);
    }

    // ------------------------------------------------------------------- what the rows say

    /// <summary>A picked class brings every attribute it has, inherited ones marked as such.</summary>
    [Fact]
    public void APickedClassBringsItsInheritedAttributesToo()
    {
        var sheet = MemberSheet(Tree(), new ClassExportSelection(new[] { "ObjectRoot.Meal.Soup" }, null));
        var rows = Body(sheet);

        // Soup declares Temperature; Meal declares Price; ObjectRoot declares privilegeToDelete.
        Assert.Equal(
            new[] { "privilegeToDelete", "Price", "Temperature" },
            rows.Select(r => r[Column(sheet, "Attribute")]).ToArray());

        // Every row names the class that was picked, not the one that declared the attribute.
        Assert.All(rows, r => Assert.Equal("Soup", r[Column(sheet, "Class")]));

        Assert.Equal(
            new[] { "ObjectRoot", "Meal", "Soup" },
            rows.Select(r => r[Column(sheet, "Declared in")]).ToArray());

        Assert.Equal(
            new[] { "Yes", "Yes", "No" },
            rows.Select(r => r[Column(sheet, "Inherited")]).ToArray());
    }

    /// <summary>An attribute a subclass redeclares is written once, against the class that introduced it.</summary>
    /// <remarks>
    /// Inheritance in the OMT is by name. Writing the attribute twice would make a class look as
    /// though it had gained one, and the count on the hierarchy tab beside it would disagree.
    /// </remarks>
    [Fact]
    public void ARedeclaredAttributeIsWrittenOnce()
    {
        var document = Tree();
        var soup = document.ObjectClasses[0].Children[0].Children[0];
        soup.Attributes.Add(new FomAttribute { Name = "Price", DataType = "HLAfloat64BE" });

        var sheet = MemberSheet(document, new ClassExportSelection(new[] { "ObjectRoot.Meal.Soup" }, null));
        var attribute = Column(sheet, "Attribute");

        var price = Assert.Single(Body(sheet), r => r[attribute] == "Price");
        Assert.Equal("Meal", price[Column(sheet, "Declared in")]);
    }

    /// <summary>The OMT columns reach the sheet rather than being summarised away.</summary>
    [Fact]
    public void TheOmtColumnsAreCarriedThrough()
    {
        var sheet = MemberSheet(Tree(), new ClassExportSelection(new[] { "ObjectRoot.Meal" }, null));
        var row = Body(sheet).Single(r => r[Column(sheet, "Attribute")] == "Price");

        Assert.Equal("Meal", row[Column(sheet, "Class")]);
        Assert.Equal("ObjectRoot.Meal", row[Column(sheet, "Qualified name")]);
        Assert.Equal("HLAfloat32BE", row[Column(sheet, "DataType")]);
        Assert.Equal("currency", row[Column(sheet, "Units")]);
        Assert.Equal("Conditional", row[Column(sheet, "UpdateType")]);
        Assert.Equal("DivestAcquire", row[Column(sheet, "Ownership")]);
        Assert.Equal("HLAreliable", row[Column(sheet, "Transportation")]);
        Assert.Equal("What it costs", row[Column(sheet, "Semantics")]);
    }

    /// <summary>
    /// A picked class with no members of its own or inherited still appears, as a row naming it.
    /// </summary>
    /// <remarks>
    /// Ordinary for an interaction: <c>HLAinteractionRoot</c> declares no parameters and neither do
    /// many of its children. A class that vanished from a tab the user had ticked it into would read
    /// as a bug rather than as an answer.
    /// </remarks>
    [Fact]
    public void APickedClassWithNoMembersStillGetsARow()
    {
        var sheet = MemberSheet(Tree(), new ClassExportSelection(null, new[] { "InteractionRoot" }));
        var row = Assert.Single(Body(sheet));

        Assert.Equal("InteractionRoot", row[Column(sheet, "Class")]);
        Assert.Equal("", row[Column(sheet, "Parameter")]);
    }

    /// <summary>Picked classes come out in the order the hierarchy tab lists them.</summary>
    [Fact]
    public void ClassesComeOutInTheOrderTheHierarchyShowsThem()
    {
        var sheet = MemberSheet(
            Tree(),
            new ClassExportSelection(new[] { "ObjectRoot.Meal.Soup", "ObjectRoot", "ObjectRoot.Meal" }, null));

        Assert.Equal(
            new[] { "ObjectRoot", "Meal", "Soup" },
            Body(sheet).Select(r => r[Column(sheet, "Class")]).Distinct().ToArray());
    }

    // ------------------------------------------------------------------- agreement with the hierarchy tab

    /// <summary>
    /// A class's rows on the member tab number exactly what the hierarchy tab totals for it.
    /// </summary>
    /// <remarks>
    /// The one invariant that makes the workbook trustworthy, checked against a real FOM. Both
    /// numbers come from the same inheritance rule; if they ever come apart, one of the two tabs is
    /// lying and the reader has no way to tell which.
    /// </remarks>
    [Fact]
    public void TheRowCountAgreesWithTheHierarchyTabsTotal()
    {
        var document = Restaurant();
        var picked = document.ObjectClasses
            .SelectMany(c => c.DescendantsAndSelf())
            .Select(c => c.QualifiedName)
            .ToArray();

        var sheets = ClassHierarchyExporter.BuildSheets(document, new ClassExportSelection(picked, null));
        var hierarchy = sheets[0];
        var members = sheets[2];

        // "Attributes total" is the last column of the hierarchy sheet; the qualified name is the
        // first of the fact columns, which sit after the staircase.
        var totals = hierarchy.Rows.Skip(1).ToDictionary(
            r => r[hierarchy.Rows[0].Count - 6].Text!,
            r => (int)r[^1].Number!.Value);

        var counted = Body(members)
            .GroupBy(r => r[Column(members, "Qualified name")])
            .ToDictionary(g => g.Key, g => g.Count(r => r[Column(members, "Attribute")].Length > 0));

        Assert.NotEmpty(totals);
        foreach (var (name, total) in totals)
            Assert.Equal(total, counted.TryGetValue(name, out var rows) ? rows : 0);
    }

    /// <summary>Picking classes leaves the hierarchy tabs exactly as they were.</summary>
    /// <remarks>
    /// The selection adds detail; it never filters. A hierarchy with the unpicked branches cut away
    /// is not a smaller true picture of the model, it is a false one.
    /// </remarks>
    [Fact]
    public void TheHierarchyTabsAreUnaffectedByTheSelection()
    {
        var document = Restaurant();

        var plain = ClassHierarchyExporter.BuildSheets(document);
        var picked = ClassHierarchyExporter.BuildSheets(
            document, new ClassExportSelection(new[] { "ObjectRoot" }, new[] { "InteractionRoot" }));

        for (var sheet = 0; sheet < 2; sheet++)
        {
            Assert.Equal(plain[sheet].Rows.Count, picked[sheet].Rows.Count);
            Assert.Equal(
                plain[sheet].Merges.Select(m => m.Reference),
                picked[sheet].Merges.Select(m => m.Reference));
        }
    }

    // ------------------------------------------------------------------- the selection itself

    /// <summary>A class can be named by its qualified name or its local one.</summary>
    /// <remarks>
    /// The dialog has the qualified name to hand; anything assembling a selection by other means —
    /// a test, a saved preset — usually has only the short one, and both should work.
    /// </remarks>
    [Fact]
    public void AClassCanBeNamedEitherWay()
    {
        Assert.Single(Body(MemberSheet(Tree(), new ClassExportSelection(new[] { "Soup" }, null)))
            .Select(r => r[0]).Distinct());

        Assert.Equal(
            Body(MemberSheet(Tree(), new ClassExportSelection(new[] { "Soup" }, null))).Count,
            Body(MemberSheet(Tree(), new ClassExportSelection(new[] { "ObjectRoot.Meal.Soup" }, null))).Count);
    }

    /// <summary>Names are matched case-sensitively, because HLA identifiers are.</summary>
    [Fact]
    public void NamesAreMatchedCaseSensitively()
    {
        Assert.Equal(2, ClassHierarchyExporter.BuildSheets(
            Tree(), new ClassExportSelection(new[] { "soup" }, null)).Count);
    }

    /// <summary>Blank and null names are dropped rather than counted as a selection.</summary>
    [Fact]
    public void BlankNamesDoNotCountAsASelection()
    {
        var selection = new ClassExportSelection(new[] { "", "   " }, null);

        Assert.True(selection.IsEmpty);
        Assert.Equal(0, selection.Count);
    }

    /// <summary>The shared empty selection holds nothing of either kind.</summary>
    [Fact]
    public void TheEmptySelectionIsEmpty()
    {
        Assert.True(ClassExportSelection.None.IsEmpty);
        Assert.Empty(ClassExportSelection.None.ObjectClasses);
        Assert.Empty(ClassExportSelection.None.InteractionClasses);
    }

    // ------------------------------------------------------------------- fixtures and helpers

    /// <summary>
    /// A three-deep object tree with attributes at every level, and an interaction root with none.
    /// </summary>
    private static FomDocument Tree()
    {
        var document = new FomDocument();

        var root = new FomObjectClass { Name = "ObjectRoot", QualifiedName = "ObjectRoot" };
        root.Attributes.Add(new FomAttribute { Name = "privilegeToDelete", DataType = "NA" });

        var meal = new FomObjectClass { Name = "Meal", QualifiedName = "ObjectRoot.Meal", Parent = root };
        meal.Attributes.Add(new FomAttribute
        {
            Name = "Price",
            DataType = "HLAfloat32BE",
            Units = "currency",
            UpdateType = "Conditional",
            Ownership = "DivestAcquire",
            Transportation = "HLAreliable",
            Semantics = "What it costs",
        });

        var soup = new FomObjectClass { Name = "Soup", QualifiedName = "ObjectRoot.Meal.Soup", Parent = meal };
        soup.Attributes.Add(new FomAttribute { Name = "Temperature", DataType = "HLAfloat32BE" });

        meal.Children.Add(soup);
        root.Children.Add(meal);
        document.ObjectClasses.Add(root);

        var interactionRoot = new FomInteractionClass { Name = "InteractionRoot", QualifiedName = "InteractionRoot" };
        var order = new FomInteractionClass
        {
            Name = "Order",
            QualifiedName = "InteractionRoot.Order",
            Parent = interactionRoot,
        };
        order.Parameters.Add(new FomParameter { Name = "Table", DataType = "HLAinteger32BE" });

        interactionRoot.Children.Add(order);
        document.InteractionClasses.Add(interactionRoot);

        return document;
    }

    /// <summary>The one member sheet a single-kind selection produces.</summary>
    private static XlsxSheet MemberSheet(FomDocument document, ClassExportSelection selection) =>
        Assert.Single(ClassMemberExporter.BuildSheets(document, selection));

    /// <summary>Body rows as plain strings, so a missing value reads as "" rather than null.</summary>
    private static List<string[]> Body(XlsxSheet sheet) =>
        sheet.Rows.Skip(1).Select(r => r.Select(c => c.Text ?? "").ToArray()).ToList();

    /// <summary>The index of a column, looked up by its header rather than counted by hand.</summary>
    private static int Column(XlsxSheet sheet, string header)
    {
        var index = sheet.Rows[0].ToList().FindIndex(c => c.Text == header);
        Assert.True(index >= 0, $"sheet {sheet.Name} has no column headed '{header}'");
        return index;
    }
}
