using System;
using System.Collections.Generic;
using System.Linq;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Reporting;

/// <summary>
/// Renders one FOM's class trees as a two-sheet Excel workbook: object classes on the first tab,
/// interaction classes on the second.
/// </summary>
/// <remarks>
/// <para>
/// Each sheet lays the tree out as a staircase. A class's name is written in the column matching
/// its depth — <c>ObjectRoot</c> in <b>Level 1</b>, <c>BaseEntity</c> in <b>Level 2</b>,
/// <c>PhysicalEntity</c> in <b>Level 3</b> — and every cell to its left and right stays blank.
/// Read down a single column and you have every class at that depth; read across a row and you
/// have one class's ancestry. That is the layout people already draw by hand when planning a
/// remap, which is the point of exporting it.
/// </para>
/// <para>
/// Rows come out in the same depth-first order the app's tree shows, so a printed sheet and the
/// screen can be read side by side.
/// </para>
/// </remarks>
public static class ClassHierarchyExporter
{
    /// <summary>Caption of the first tab.</summary>
    public const string ObjectSheetName = "Object Class Hierarchy";

    /// <summary>Caption of the second tab.</summary>
    public const string InteractionSheetName = "Interaction Class Hierarchy";

    /// <summary>
    /// Recursion limit. The class trees are trees, but a hand-assembled or malformed document
    /// could contain a cycle; the guard keeps the exporter from spinning.
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>Shown in place of rows when a FOM declares no classes of that kind at all.</summary>
    private const string NothingDeclared = "This FOM declares no classes of this kind.";

    /// <summary>Writes the workbook for <paramref name="document"/> to <paramref name="path"/>.</summary>
    /// <param name="document">The FOM to render. Only a null value is an exception.</param>
    /// <param name="path">Destination <c>.xlsx</c> file, replaced if it exists.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="System.IO.IOException">The file could not be written.</exception>
    public static void Export(FomDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);

        XlsxWriter.Write(path, BuildSheets(document));
    }

    /// <summary>
    /// Builds the two sheets without writing them, so the layout can be asserted directly.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static IReadOnlyList<XlsxSheet> BuildSheets(FomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new[]
        {
            BuildObjectSheet(document),
            BuildInteractionSheet(document),
        };
    }

    // ------------------------------------------------------------------- object classes

    private static XlsxSheet BuildObjectSheet(FomDocument document)
    {
        var rows = new List<ObjectRow>();
        var visited = new HashSet<FomObjectClass>(ReferenceEqualityComparer.Instance);

        foreach (var root in document.ObjectClasses)
            WalkObjects(root, new List<FomObjectClass>(), rows, visited);

        var depth = rows.Count == 0 ? 1 : rows.Max(r => r.Level);
        var sheet = NewSheet(ObjectSheetName, depth, "Attributes declared", "Attributes inherited", "Attributes total");

        foreach (var row in rows)
        {
            var (own, inherited) = MemberCounts(row.Ancestors, row.Class, c => c.Attributes.Select(a => a.Name));
            sheet.Rows.Add(BodyRow(depth, row.Level, row.Class.Name, QualifiedName(row.Class), row.Class.Sharing, own, inherited));
        }

        if (rows.Count == 0) sheet.Rows.Add(new[] { XlsxCell.Str(NothingDeclared) });

        return sheet;
    }

    private readonly record struct ObjectRow(FomObjectClass Class, int Level, IReadOnlyList<FomObjectClass> Ancestors);

    private static void WalkObjects(
        FomObjectClass? node,
        List<FomObjectClass> ancestors,
        List<ObjectRow> rows,
        HashSet<FomObjectClass> visited)
    {
        if (node is null || ancestors.Count >= MaxDepth || !visited.Add(node)) return;

        rows.Add(new ObjectRow(node, ancestors.Count + 1, ancestors.ToArray()));

        ancestors.Add(node);
        foreach (var child in node.Children)
            WalkObjects(child, ancestors, rows, visited);
        ancestors.RemoveAt(ancestors.Count - 1);
    }

    // ------------------------------------------------------------------- interaction classes

    private static XlsxSheet BuildInteractionSheet(FomDocument document)
    {
        var rows = new List<InteractionRow>();
        var visited = new HashSet<FomInteractionClass>(ReferenceEqualityComparer.Instance);

        foreach (var root in document.InteractionClasses)
            WalkInteractions(root, new List<FomInteractionClass>(), rows, visited);

        var depth = rows.Count == 0 ? 1 : rows.Max(r => r.Level);
        var sheet = NewSheet(InteractionSheetName, depth, "Parameters declared", "Parameters inherited", "Parameters total");

        foreach (var row in rows)
        {
            var (own, inherited) = MemberCounts(row.Ancestors, row.Class, c => c.Parameters.Select(p => p.Name));
            sheet.Rows.Add(BodyRow(depth, row.Level, row.Class.Name, QualifiedName(row.Class), row.Class.Sharing, own, inherited));
        }

        if (rows.Count == 0) sheet.Rows.Add(new[] { XlsxCell.Str(NothingDeclared) });

        return sheet;
    }

    private readonly record struct InteractionRow(FomInteractionClass Class, int Level, IReadOnlyList<FomInteractionClass> Ancestors);

    private static void WalkInteractions(
        FomInteractionClass? node,
        List<FomInteractionClass> ancestors,
        List<InteractionRow> rows,
        HashSet<FomInteractionClass> visited)
    {
        if (node is null || ancestors.Count >= MaxDepth || !visited.Add(node)) return;

        rows.Add(new InteractionRow(node, ancestors.Count + 1, ancestors.ToArray()));

        ancestors.Add(node);
        foreach (var child in node.Children)
            WalkInteractions(child, ancestors, rows, visited);
        ancestors.RemoveAt(ancestors.Count - 1);
    }

    // ------------------------------------------------------------------- shared shape

    /// <summary>A sheet with the staircase columns, then the three fact columns, then the counts.</summary>
    private static XlsxSheet NewSheet(string name, int depth, string ownHeader, string inheritedHeader, string totalHeader)
    {
        var sheet = new XlsxSheet(name);
        var header = new List<XlsxCell>(depth + 6);

        for (var level = 1; level <= depth; level++)
        {
            header.Add(XlsxCell.Head("Level " + level));

            // Deeper columns are narrower: names get longer with depth, but so does the run of
            // blank cells before them, and a wide sheet stops fitting on a page.
            sheet.ColumnWidths.Add(level == 1 ? 22 : 24);
        }

        header.Add(XlsxCell.Head("Qualified name"));
        header.Add(XlsxCell.Head("Sharing"));
        header.Add(XlsxCell.Head("Level"));
        header.Add(XlsxCell.Head(ownHeader));
        header.Add(XlsxCell.Head(inheritedHeader));
        header.Add(XlsxCell.Head(totalHeader));

        sheet.ColumnWidths.Add(46);
        sheet.ColumnWidths.Add(17);
        sheet.ColumnWidths.Add(7);
        sheet.ColumnWidths.Add(18);
        sheet.ColumnWidths.Add(18);
        sheet.ColumnWidths.Add(14);

        sheet.Rows.Add(header);
        return sheet;
    }

    private static IReadOnlyList<XlsxCell> BodyRow(
        int depth, int level, string name, string qualifiedName, string? sharing, int own, int inherited)
    {
        var cells = new List<XlsxCell>(depth + 6);

        for (var column = 1; column <= depth; column++)
            cells.Add(column == level ? XlsxCell.Str(name) : XlsxCell.Empty);

        cells.Add(XlsxCell.Str(qualifiedName));
        cells.Add(XlsxCell.Str(sharing));
        cells.Add(XlsxCell.Num(level));
        cells.Add(XlsxCell.Num(own));
        cells.Add(XlsxCell.Num(inherited));
        cells.Add(XlsxCell.Num(own + inherited));

        return cells;
    }

    /// <summary>
    /// Splits a class's effective members into those it declares and those it inherits.
    /// </summary>
    /// <remarks>
    /// The rule mirrors the FOM detail screen exactly — ancestors are walked root-first and a name
    /// already seen is skipped, so a redeclared member is counted once, against the ancestor that
    /// introduced it. The two must agree: a sheet that disagrees with the screen it was exported
    /// from is worse than no sheet.
    /// </remarks>
    private static (int Own, int Inherited) MemberCounts<T>(
        IReadOnlyList<T> ancestors, T self, Func<T, IEnumerable<string>> members) where T : class
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inherited = 0;

        foreach (var ancestor in ancestors)
            inherited += members(ancestor).Count(seen.Add);

        var own = members(self).Count(seen.Add);
        return (own, inherited);
    }

    /// <summary>The dotted path, falling back to the local name when the parser did not record one.</summary>
    private static string QualifiedName(FomNode node) =>
        string.IsNullOrWhiteSpace(node.QualifiedName) ? node.Name : node.QualifiedName;
}
