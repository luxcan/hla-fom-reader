using System;
using System.Collections.Generic;
using System.Linq;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Reporting;

/// <summary>
/// Builds and renders the side-by-side remap worksheet: the attributes of two chosen classes,
/// unfolded a level at a time through the structure of their datatypes, with FOM A's columns beside
/// FOM B's.
/// </summary>
/// <remarks>
/// <para>
/// The screen answers "which attributes re-encode?". This answers the question that follows it —
/// "and <em>where</em> inside them?" — which is where the remapping work actually is. An attribute
/// typed as a three-field record on both sides can differ in one field, and a sheet that stops at
/// the attribute reports one line of "Changed" against a name, leaving the reader to open the
/// datatype inspector on both sides and compare by eye. Unfolding the record puts the two field
/// lists next to each other, and the row where they part is the conversion somebody has to write.
/// </para>
/// <para>
/// Nested members are lined up by <see cref="DataTypeMemberPairing"/>, which pairs on names first
/// and falls back to position for record fields alone, because field order is what places the bytes.
/// Every positional pairing says so in its Note, so the reader always knows which pairings the FOM
/// asserts and which this inferred.
/// </para>
/// <para>
/// Content problems never throw. An unresolvable datatype yields a row that says so and no children.
/// </para>
/// </remarks>
public static class AttributePairExporter
{
    /// <summary>The name of the worksheet the pair is written to.</summary>
    public const string SheetName = "Attribute data";

    /// <summary>What a side's Level 1 column says when the other side has this attribute and it does not.</summary>
    public const string NotFoundText = "Attribute not found";

    /// <summary>The two empty columns that separate FOM A's block from FOM B's.</summary>
    /// <remarks>
    /// A gutter rather than a rule. The two halves are read as a pair — across a row to see whether
    /// the same bytes arrive on the other side — and a border between them says "two tables" where
    /// white space says "two halves of one".
    /// </remarks>
    private const int GapColumns = 2;

    /// <summary>
    /// Expands a set of already-filtered map rows into the leveled sheet.
    /// </summary>
    /// <param name="map">The class-pair map the rows came from; supplies the two class names and labels.</param>
    /// <param name="rows">
    /// The rows to write. The caller passes what is on screen rather than the whole map, so the file
    /// holds exactly what the reader narrowed down to.
    /// </param>
    /// <param name="left">FOM A, for resolving A's datatypes through A's own tables.</param>
    /// <param name="right">FOM B.</param>
    /// <param name="options">Depth and suppression knobs; defaults are used when null.</param>
    /// <exception cref="ArgumentNullException">Any of the first four arguments is null.</exception>
    public static AttributePairSheet Build(
        AttributeDataMap map,
        IReadOnlyList<AttributeMapRow> rows,
        FomDocument left,
        FomDocument right,
        AttributePairSheetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var o = options ?? new AttributePairSheetOptions();

        // Its own resolvers rather than any the caller may be holding. DataTypeResolver memoises
        // into shared state and is not written to be touched from two threads, and an export runs on
        // a worker while the datatype inspector can be opened from the UI thread against the very
        // same instance. Building a pair is cheap next to the walk they are used for.
        var leftTypes = new DataTypeResolver(left);
        var rightTypes = new DataTypeResolver(right);

        var built = new List<AttributePairRow>(rows.Count);
        var advisories = new List<string>(map.Advisories);

        foreach (var row in rows)
            AddAttribute(built, row, leftTypes, rightTypes, o);

        return new AttributePairSheet
        {
            ClassA = map.LeftClassName,
            ClassB = map.RightClassName,
            LeftLabel = map.LeftLabel,
            RightLabel = map.RightLabel,
            Rows = built,
            Advisories = advisories,
        };
    }

    /// <summary>
    /// Lays a built sheet out as a worksheet: FOM A's staircase, a gutter, then FOM B's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each side gets one column per level, and a row puts its name in the column matching its
    /// depth — the attribute in <b>Level 1</b>, its record's fields in <b>Level 2</b>, theirs in
    /// <b>Level 3</b> — which is the same staircase <see cref="ClassHierarchyExporter"/> writes for
    /// a class tree, so the two sheets are read the same way. The datatype and its resolved encoding
    /// follow each staircase, because the shape alone cannot tell a rename from a re-encode.
    /// </para>
    /// <para>
    /// The two sides share their rows: a row holds an A member beside whichever B member it was
    /// paired with, so the fold down the middle of the sheet is the comparison.
    /// </para>
    /// </remarks>
    public static XlsxSheet ToSheet(AttributePairSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        // Only as many level columns as the rows actually reach. A sheet of plain scalar attributes
        // has no level 2, and an empty column headed "Level 2" reads as something missing.
        var depth = 1;
        foreach (var row in sheet.Rows)
            if (row.Depth > depth) depth = row.Depth;

        var built = new XlsxSheet(SheetName) { FrozenRows = 2 };

        AddColumnWidths(built, depth);
        AddHeader(built, sheet, depth);

        var comparing = sheet.ClassA is not null && sheet.ClassB is not null;

        AddBody(built, sheet, depth, comparing);

        return built;
    }

    /// <summary>Writes the pair to an .xlsx workbook of one sheet.</summary>
    public static void WriteXlsx(AttributePairSheet sheet, string path)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        XlsxWriter.Write(path, new[] { ToSheet(sheet) });
    }

    private static void AddColumnWidths(XlsxSheet built, int depth)
    {
        for (var side = 0; side < 2; side++)
        {
            // The gutter, before B's block. Narrow enough to read as a gap rather than a column.
            if (side == 1)
                for (var i = 0; i < GapColumns; i++) built.ColumnWidths.Add(3);

            for (var level = 1; level <= depth; level++) built.ColumnWidths.Add(28);

            built.ColumnWidths.Add(30);
            built.ColumnWidths.Add(26);
        }

        built.ColumnWidths.Add(46);
    }

    /// <summary>
    /// Two header rows: which FOM and class each half describes, then the columns themselves.
    /// </summary>
    /// <remarks>
    /// The banner is what lets a saved file say where it came from. The two class names are the one
    /// fact the rows no longer carry — they were a column repeated against every attribute, which is
    /// the same string forty-five times — and without them a worksheet found later names neither the
    /// pair it compares nor the FOMs it was taken from.
    ///
    /// Each banner is <b>merged</b> across its own half, and it has to be. The bold padding cells
    /// that carry the header fill hold an empty string rather than nothing, and Excel counts a cell
    /// holding an empty string as occupied — so without the merge the banner would not spill across
    /// them but be clipped at column A's width, cutting "FOM A · RestaurantFOM ·
    /// ObjectRoot.Employee.Chef" off inside the FOM name. A merged range is laid out across its
    /// whole span, so the one line that says where the file came from stays readable.
    /// </remarks>
    private static void AddHeader(XlsxSheet built, AttributePairSheet sheet, int depth)
    {
        var banner = new List<XlsxCell>();
        var columns = new List<XlsxCell>();

        AddSide(banner, columns, "FOM A", sheet.LeftLabel, sheet.ClassA, gapFirst: false);
        AddSide(banner, columns, "FOM B", sheet.RightLabel, sheet.ClassB, gapFirst: true);

        // Excel counts from one, and the banner is the first row.
        built.Merges.Add(new XlsxMerge(1, 1, 1, depth + 2));
        built.Merges.Add(new XlsxMerge(1, depth + 2 + GapColumns + 1, 1, (depth + 2) * 2 + GapColumns));

        banner.Add(XlsxCell.Head(""));
        columns.Add(XlsxCell.Head("Note"));

        built.Rows.Add(banner);
        built.Rows.Add(columns);

        void AddSide(
            List<XlsxCell> bannerRow, List<XlsxCell> columnRow,
            string side, string label, string? className, bool gapFirst)
        {
            if (gapFirst)
            {
                for (var i = 0; i < GapColumns; i++)
                {
                    bannerRow.Add(XlsxCell.Open);
                    columnRow.Add(XlsxCell.Open);
                }
            }

            bannerRow.Add(XlsxCell.Head(Describe(side, label, className)));

            // Empty bold cells carry the header fill across the rest of this half.
            for (var i = 1; i < depth + 2; i++) bannerRow.Add(XlsxCell.Head(""));

            for (var level = 1; level <= depth; level++) columnRow.Add(XlsxCell.Head("Level " + level));

            columnRow.Add(XlsxCell.Head("DataType"));
            columnRow.Add(XlsxCell.Head("Encoding"));
        }
    }

    /// <summary>The banner over one half: which side, which FOM, and which class.</summary>
    private static string Describe(string side, string label, string? className)
    {
        var named = string.IsNullOrWhiteSpace(label) ? side : $"{side} · {label}";

        return className is null ? $"{named} · no class chosen" : $"{named} · {className}";
    }

    /// <summary>Body rows start under the two header rows, and Excel counts from one.</summary>
    private const int FirstBodyRow = 3;

    /// <summary>
    /// Writes the rows, and joins each name down the block of rows its own members occupy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The merging is what turns the staircase into the table people draw by hand, and it is the
    /// same thing <see cref="ClassHierarchyExporter"/> does to a class tree. Without it an
    /// attribute's name sits on its own row with a column of blanks running underneath it, and
    /// which fields belong to which attribute has to be worked out by eye from the indentation.
    /// Merged, the name is written once against the whole run of its members.
    /// </para>
    /// <para>
    /// That the range is a rectangle at all is a property of the walk: the expansion emits an
    /// attribute immediately before its own members and nothing else, so a block is exactly the
    /// rows that follow until one appears at its own depth or shallower.
    /// </para>
    /// <para>
    /// Which cells a merge covers is worked out per side rather than assumed from the column, and
    /// that is the one place this departs from the class sheet. There, every ancestor of a row has
    /// a name, so everything left of a name is necessarily under a block. Here a side can be blank
    /// at a level — the whole point of "Attribute not found" — and ruling a cell no block covers
    /// draws an empty box around nothing.
    /// </para>
    /// </remarks>
    private static void AddBody(XlsxSheet built, AttributePairSheet sheet, int depth, bool comparing)
    {
        var rows = sheet.Rows;

        var leftColumn = 1;
        var rightColumn = depth + 2 + GapColumns + 1;

        // Which staircase cells lie under a merged block, per side. Indexed [row, level].
        var coveredLeft = new bool[rows.Count, depth + 2];
        var coveredRight = new bool[rows.Count, depth + 2];

        for (var first = 0; first < rows.Count; first++)
        {
            var level = rows[first].Depth;
            if (level < 1 || level > depth) continue;

            var last = first;
            while (last + 1 < rows.Count && rows[last + 1].Depth > level) last++;

            // A leaf spans one row and is skipped: a one-cell merge changes nothing and Excel still
            // has to carry every one of them.
            if (last == first) continue;

            Join(StaircaseText(rows[first], left: true, comparing), coveredLeft, leftColumn);
            Join(StaircaseText(rows[first], left: false, comparing), coveredRight, rightColumn);

            void Join(string? anchor, bool[,] covered, int columnBase)
            {
                // Nothing to join when this side has no name here — the block belongs to the other.
                if (anchor is null) return;

                var column = columnBase + level - 1;
                built.Merges.Add(new XlsxMerge(FirstBodyRow + first, column, FirstBodyRow + last, column));

                for (var row = first + 1; row <= last; row++) covered[row, level] = true;
            }
        }

        for (var i = 0; i < rows.Count; i++)
            built.Rows.Add(BodyRow(rows[i], i, depth, comparing, coveredLeft, coveredRight));
    }

    /// <summary>
    /// What a side writes into its staircase on this row: the name, the phrase that says the
    /// attribute is absent, or nothing.
    /// </summary>
    /// <remarks>
    /// The phrase appears once, on the attribute's own row, and the merge then carries it down the
    /// block. Deeper rows never raise it themselves: the fields of an attribute that is not there
    /// are not separately missing.
    /// </remarks>
    private static string? StaircaseText(AttributePairRow row, bool left, bool comparing)
    {
        var name = left ? row.NameA : row.NameB;
        if (name is not null) return name;

        return comparing && row.Depth == 1 ? NotFoundText : null;
    }

    /// <summary>One row: the same member's place in each side's staircase, with the two datatypes.</summary>
    private static IReadOnlyList<XlsxCell> BodyRow(
        AttributePairRow row, int index, int depth, bool comparing,
        bool[,] coveredLeft, bool[,] coveredRight)
    {
        var cells = new List<XlsxCell>((depth + 2) * 2 + GapColumns + 1);

        AddSide(left: true, row.DataTypeA, row.EncodingA, coveredLeft, gapFirst: false);
        AddSide(left: false, row.DataTypeB, row.EncodingB, coveredRight, gapFirst: true);

        cells.Add(XlsxCell.Str(row.Note));

        return cells;

        void AddSide(bool left, string? dataType, string? encoding, bool[,] covered, bool gapFirst)
        {
            if (gapFirst)
                for (var i = 0; i < GapColumns; i++) cells.Add(XlsxCell.Open);

            var text = StaircaseText(row, left, comparing);

            for (var level = 1; level <= depth; level++)
            {
                if (level == row.Depth && text is not null)
                {
                    // Node rather than Str: this is the anchor of a merge whenever the attribute has
                    // members, and Excel aligns to the bottom of a cell unless told otherwise, so the
                    // name would print level with the last of its fields instead of beside itself.
                    cells.Add(XlsxCell.Node(text));
                    continue;
                }

                // Both blanks are painted white rather than left unfilled: an unfilled cell has no
                // colour of its own and takes whatever is behind it, which under Office's own dark
                // theme turns the empty half of a staircase into a dark field with the names punched
                // out of it. They part company over the border. A cell a merged block runs through
                // has to stay ruled, because Excel builds the block's outline out of the borders of
                // the cells it covers and dropping it opens the bottom of every block. A cell no
                // block covers is left unruled, or the sheet reads as a form to fill in.
                cells.Add(covered[index, level] ? XlsxCell.Paper : XlsxCell.Open);
            }

            cells.Add(XlsxCell.Str(dataType));
            cells.Add(XlsxCell.Str(encoding));
        }
    }

    // ---- expansion --------------------------------------------------------------------------

    /// <summary>Emits one attribute's depth-1 row and everything under it.</summary>
    private static void AddAttribute(
        List<AttributePairRow> rows,
        AttributeMapRow row,
        DataTypeResolver leftTypes,
        DataTypeResolver rightTypes,
        AttributePairSheetOptions o)
    {
        var unpaired = row.Status == AttributeMapStatus.Unpaired;

        rows.Add(new AttributePairRow
        {
            Depth = 1,
            Role = null,
            AttributeName = row.AttributeName,
            Match = row.Status,
            NameA = row.LeftDataType is null && row.LeftDeclaredIn is null ? null : row.AttributeName,

            // B's own spelling when the two sides matched on a folded name they write differently.
            NameB = row.RightDataType is null && row.RightDeclaredIn is null
                ? null
                : row.RightAttributeName ?? row.AttributeName,

            DataTypeA = row.LeftDataType,
            EncodingA = row.LeftEncoding,
            DataTypeB = row.RightDataType,
            EncodingB = row.RightEncoding,

            // The qualified name, because two independently chosen classes may sit in unrelated
            // trees that both carry a Platform and the local name could not say which.
            DeclaredInA = row.LeftDeclaredInQualified ?? row.LeftDeclaredIn,
            DeclaredInB = row.RightDeclaredInQualified ?? row.RightDeclaredIn,
            Note = row.Note,
        });

        if (o.MaxDepth < 2) return;

        var leftDetail = row.LeftDataType is null ? null : Explain(leftTypes, row.LeftDataType);
        var rightDetail = row.RightDataType is null ? null : Explain(rightTypes, row.RightDataType);

        if (leftDetail is null && rightDetail is null) return;

        var budget = new Budget(o.MaxRowsPerAttribute);
        Expand(rows, row.AttributeName, leftDetail, rightDetail, depth: 2, unpaired, o, budget);
    }

    /// <summary>
    /// Walks the members of a pair of datatypes together, emitting one row per pairing and
    /// recursing into both halves at once.
    /// </summary>
    private static void Expand(
        List<AttributePairRow> rows,
        string attributeName,
        DataTypeDetail? left,
        DataTypeDetail? right,
        int depth,
        bool unpaired,
        AttributePairSheetOptions o,
        Budget budget)
    {
        if (depth > o.MaxDepth) return;

        var leftMembers = Visible(left, o);
        var rightMembers = Visible(right, o);

        if (leftMembers.Count == 0 && rightMembers.Count == 0) return;

        foreach (var pair in DataTypeMemberPairing.Pair(leftMembers, rightMembers, o.Comparison))
        {
            if (!budget.Take())
            {
                // Said once per attribute, and only when something was actually dropped.
                if (budget.ReportOnce())
                {
                    rows.Add(new AttributePairRow
                    {
                        Depth = depth,
                        Role = pair.Left?.Role ?? pair.Right?.Role,
                        AttributeName = attributeName,
                        Note = $"Expansion stopped after {o.MaxRowsPerAttribute} rows for this attribute.",
                    });
                }

                return;
            }

            var a = pair.Left;
            var b = pair.Right;

            var notes = new List<string>(2);
            if (pair.PairedByPosition) notes.Add(DataTypeMemberPairing.PositionalNote);

            // The resolver's own wording, verbatim: a FOM whose datatype graph loops is a fact about
            // the FOM, and it must not be confused with this sheet choosing to stop.
            if (a?.Type?.Truncation is { } leftStop) notes.Add($"FOM A: {leftStop}");
            if (b?.Type?.Truncation is { } rightStop) notes.Add($"FOM B: {rightStop}");

            var deeper = depth + 1 <= o.MaxDepth;
            if (!deeper && (Visible(a?.Type, o).Count > 0 || Visible(b?.Type, o).Count > 0))
                notes.Add($"Not unfolded further; the sheet stops at level {o.MaxDepth}.");

            rows.Add(new AttributePairRow
            {
                Depth = depth,
                Role = a?.Role ?? b?.Role,
                AttributeName = attributeName,
                Match = NestedMatch(a, b, unpaired),
                NameA = a is null ? null : MemberName(a),
                DataTypeA = a?.Type?.Name,
                EncodingA = a?.Type?.Canonical,
                NameB = b is null ? null : MemberName(b),
                DataTypeB = b?.Type?.Name,
                EncodingB = b?.Type?.Canonical,
                Note = notes.Count == 0 ? null : string.Join(" ", notes),
            });

            if (deeper) Expand(rows, attributeName, a?.Type, b?.Type, depth + 1, unpaired, o, budget);
        }
    }

    /// <summary>
    /// The members of a datatype that earn a row, with the two suppressed roles filtered out.
    /// </summary>
    private static IReadOnlyList<DataTypeDetailMember> Visible(
        DataTypeDetail? detail, AttributePairSheetOptions o)
    {
        if (detail is null || detail.Members.Count == 0) return Array.Empty<DataTypeDetailMember>();

        var kept = new List<DataTypeDetailMember>(detail.Members.Count);

        foreach (var member in detail.Members)
        {
            if (member.Role == DataTypeMemberRole.Enumerator && !o.IncludeEnumerators) continue;
            if (member.Role == DataTypeMemberRole.Representation && !o.IncludeRepresentation) continue;

            kept.Add(member);
        }

        return kept;
    }

    /// <summary>
    /// How the two halves of a nested pairing compare.
    /// </summary>
    /// <remarks>
    /// Null where nothing can be established — an unresolved name on either side is evidence
    /// neither way, and writing "Changed" there would assert a difference the FOM does not support.
    /// That reasoning is <see cref="AttributeMapRow.EncodingDiffers"/>'s, applied one level down.
    /// </remarks>
    private static AttributeMapStatus? NestedMatch(
        DataTypeDetailMember? left, DataTypeDetailMember? right, bool unpaired)
    {
        if (unpaired) return AttributeMapStatus.Unpaired;

        if (left is null && right is null) return null;
        if (right is null) return AttributeMapStatus.OnlyInLeft;
        if (left is null) return AttributeMapStatus.OnlyInRight;

        var a = left.Type;
        var b = right.Type;

        // An enumerator carries no type of its own; the two are the same value or they are not.
        if (a is null || b is null)
            return a is null && b is null
                ? SameValue(left.Value, right.Value) ? AttributeMapStatus.Same : AttributeMapStatus.DataTypeChanged
                : null;

        if (!a.IsResolved || !b.IsResolved) return null;

        return string.Equals(a.Canonical, b.Canonical, StringComparison.Ordinal)
            ? AttributeMapStatus.Same
            : AttributeMapStatus.DataTypeChanged;
    }

    private static bool SameValue(string? left, string? right) =>
        string.Equals(left?.Trim() ?? "", right?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a member is called in the name column: its own name, or its selecting value when it has
    /// no name — an alternative and an enumerator are both identified by a value.
    /// </summary>
    private static string MemberName(DataTypeDetailMember member) =>
        string.IsNullOrWhiteSpace(member.Name) ? member.Value ?? "" : member.Name;

    /// <summary>
    /// Resolves a name, never throwing on content.
    /// </summary>
    /// <remarks>
    /// <see cref="DataTypeResolver.Explain"/> is documented not to throw on a malformed FOM, so
    /// anything caught here is a defect rather than bad input; the sheet loses one attribute's
    /// structure instead of the whole export.
    /// </remarks>
    private static DataTypeDetail? Explain(DataTypeResolver resolver, string name)
    {
        try
        {
            return resolver.Explain(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>How many rows one attribute has left before its expansion is cut short.</summary>
    private sealed class Budget
    {
        private int _left;
        private bool _reported;

        internal Budget(int limit) => _left = limit < 0 ? 0 : limit;

        /// <summary>Claims one row, or reports that there are none left.</summary>
        internal bool Take()
        {
            if (_left <= 0) return false;
            _left--;
            return true;
        }

        /// <summary>True the first time the cap is hit, so it is explained once and not per row.</summary>
        internal bool ReportOnce()
        {
            if (_reported) return false;
            _reported = true;
            return true;
        }
    }
}
