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
/// A name is then merged down the block of rows its descendants occupy, so a parent is written
/// once against its whole family rather than once above a column of blanks. That is the other half
/// of the hand-drawn layout, and it is what makes the sheet readable at the depths real FOMs reach:
/// with <c>ObjectRoot</c> spanning every row beneath it and each branch spanning its own, the
/// blocks in a column *are* the families, instead of something the eye has to reconstruct from
/// indentation. A leaf spans one row and is left alone.
/// </para>
/// <para>
/// The trade is narrower than merging usually costs, because the merges are confined to the
/// staircase columns. Sorting a range that takes in those columns is refused — Excel will not sort
/// across merged cells of differing heights — but sorting the fact columns on their own still
/// works, and so does filtering, including on the numeric <b>Level</b> column. What is no longer
/// possible is a sort of the whole used range, and that one destroyed the sheet: depth-first order
/// is the only thing recording which class sits under which, so re-sorting scattered the names
/// across the level columns and left a hierarchy nothing could put back.
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
    /// <param name="palette">
    /// Colours for the header band and the grid, so the sheet can follow the theme the app is
    /// wearing. Null takes <see cref="XlsxPalette.Default"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="System.IO.IOException">The file could not be written.</exception>
    public static void Export(FomDocument document, string path, XlsxPalette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);

        XlsxWriter.Write(path, BuildSheets(document), palette);
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
        else MergeSubtreeSpans(sheet, rows.Select(r => r.Level).ToList());

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
        else MergeSubtreeSpans(sheet, rows.Select(r => r.Level).ToList());

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

    /// <summary>
    /// Joins each class's name cell down the block of rows its descendants occupy.
    /// </summary>
    /// <param name="sheet">Sheet to add the merges to. Its first row is the header.</param>
    /// <param name="levels">Depth of each body row, in the order the rows were added.</param>
    /// <remarks>
    /// <para>
    /// This is what turns the staircase into the table people draw by hand. Without it a parent's
    /// name sits on its own row with a column of blanks running underneath it, and which children
    /// belong to which parent has to be worked out by eye from the indentation. Merged, the name is
    /// written once against the whole run of its descendants: read down Level 2 and the blocks
    /// are the families.
    /// </para>
    /// <para>
    /// That the range is a rectangle at all is a property of the walk. Depth-first pre-order puts a
    /// class immediately before its descendants and nothing else, so its subtree is exactly the
    /// rows that follow until one appears at its own depth or shallower. A different traversal
    /// order would leave nothing contiguous to merge.
    /// </para>
    /// <para>
    /// Blocks never overlap: two classes at the same depth hold disjoint runs, and a deeper class
    /// writes into a different column. A leaf spans one row and is skipped, since a one-cell merge
    /// changes nothing and Excel still has to carry every one of them.
    /// </para>
    /// </remarks>
    private static void MergeSubtreeSpans(XlsxSheet sheet, IReadOnlyList<int> levels)
    {
        // Body rows start under the header, and Excel counts from one.
        const int FirstBodyRow = 2;

        for (var first = 0; first < levels.Count; first++)
        {
            var last = first;
            while (last + 1 < levels.Count && levels[last + 1] > levels[first]) last++;

            if (last == first) continue;

            // Level n is column n: the staircase columns are the leftmost on the sheet.
            var column = levels[first];
            sheet.Merges.Add(new XlsxMerge(FirstBodyRow + first, column, FirstBodyRow + last, column));
        }
    }

    private static IReadOnlyList<XlsxCell> BodyRow(
        int depth, int level, string name, string qualifiedName, string? sharing, int own, int inherited)
    {
        var cells = new List<XlsxCell>(depth + 6);

        // Node rather than Str: this cell is the anchor of a merge whenever the class has children,
        // and Excel would otherwise drop the name to the bottom of the block.
        //
        // Both kinds of blank are painted white rather than left unfilled. An unfilled cell has no
        // colour of its own and takes whatever Excel puts behind it, so under Office's dark theme
        // the empty half of the staircase came out a dark field with the named cells punched out
        // of it.
        //
        // They part company over the border, and which side of the name a blank falls on decides
        // it. Everything to the left lies under an ancestor's merged block — every ancestor of this
        // class has a child, namely the next one along the path down to it, so every ancestor is
        // merged — and Excel builds a merged range's outline out of the borders of the cells it
        // covers, so those have to stay ruled or the blocks open at the bottom. Everything to the
        // right belongs to no block at all: ruling it draws an empty box around nothing, which is
        // the grid that made the staircase look like a form to fill in rather than a tree.
        for (var column = 1; column <= depth; column++)
        {
            if (column == level) cells.Add(XlsxCell.Node(name));
            else cells.Add(column < level ? XlsxCell.Paper : XlsxCell.Open);
        }

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
