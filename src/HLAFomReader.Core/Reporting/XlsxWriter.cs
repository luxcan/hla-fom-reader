using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace HLAFomReader.Core.Reporting;

/// <summary>One cell of a worksheet: text, a number, or nothing at all.</summary>
/// <param name="Text">Inline string content. Null for a numeric or empty cell.</param>
/// <param name="Number">Numeric content, written so Excel can sort and total it. Null for text.</param>
/// <param name="Bold">True to apply the workbook's single bold style.</param>
/// <param name="TopAligned">
/// True to pin the content to the top of its cell. Only tells against a cell merged down a block
/// of rows, which is the case it exists for.
/// </param>
/// <param name="WhiteFill">
/// True to paint the cell white outright rather than leaving it unfilled. Not the same thing: an
/// unfilled cell has no colour of its own and takes whatever the reader's Excel paints behind it,
/// which is dark under Office's dark theme.
/// </param>
/// <param name="Unruled">
/// True to leave the cell without a border, so it reads as open page rather than an empty box.
/// </param>
public readonly record struct XlsxCell(
    string? Text, double? Number, bool Bold,
    bool TopAligned = false, bool WhiteFill = false, bool Unruled = false)
{
    /// <summary>True when the cell should be left out of the sheet entirely.</summary>
    public bool IsEmpty => Text is null && Number is null;

    /// <summary>A cell with nothing in it, left unfilled.</summary>
    public static XlsxCell Empty => default;

    /// <summary>
    /// A cell with nothing in it, painted white and still ruled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "No fill" and "white" look identical on a default worksheet and are not the same thing. An
    /// unfilled cell has no colour of its own, so it shows whatever is behind it — and under
    /// Office's own dark theme that is a dark grey, which turns the empty half of a staircase into
    /// a dark field with the occupied cells punched out of it. Painting them makes the page white
    /// wherever the reader has set their Excel.
    /// </para>
    /// <para>
    /// Keeps its border because this is the blank that lies <em>under</em> a merged block. Excel
    /// draws a merged range's outline out of the borders of the cells it covers, so dropping the
    /// border here would open the bottom of every block that spans more than its own row.
    /// </para>
    /// </remarks>
    public static XlsxCell Paper => new(null, null, false, WhiteFill: true);

    /// <summary>A cell with nothing in it, painted white and left unruled: open page.</summary>
    /// <remarks>
    /// For blanks that belong to no block at all, where a border would draw an empty box around
    /// nothing. Distinct from <see cref="Paper"/> only in that; the two are not interchangeable,
    /// and using this one under a merge breaks the block's outline.
    /// </remarks>
    public static XlsxCell Open => new(null, null, false, WhiteFill: true, Unruled: true);

    /// <summary>A text cell. Null or empty text collapses to <see cref="Empty"/>.</summary>
    public static XlsxCell Str(string? value) =>
        string.IsNullOrEmpty(value) ? default : new XlsxCell(value, null, false);

    /// <summary>A bold text cell, used for the header row.</summary>
    public static XlsxCell Head(string value) => new(value, null, true);

    /// <summary>
    /// A text cell that sits at the top of whatever space it is given — what the anchor of a
    /// merged block needs.
    /// </summary>
    /// <remarks>
    /// Excel aligns to the bottom of a cell unless told otherwise, so a class name merged down the
    /// twenty rows of its subtree would print twenty rows beneath the class it names, level with
    /// the last of its descendants. The facts about that class are on the block's first row, so the
    /// name belongs there too.
    /// </remarks>
    public static XlsxCell Node(string? value) =>
        string.IsNullOrEmpty(value) ? default : new XlsxCell(value, null, false, TopAligned: true);

    /// <summary>A numeric cell.</summary>
    public static XlsxCell Num(double value) => new(null, value, false);
}

/// <summary>
/// One block of cells joined into a single cell, in Excel's own 1-based coordinates — row 1 is the
/// first row of the sheet, column 1 is A.
/// </summary>
/// <remarks>
/// Only the top-left cell of a block carries content. Anything written into the rest is kept in the
/// file but never shown, so the writer simply leaves those cells out.
/// </remarks>
/// <param name="FirstRow">Topmost row of the block.</param>
/// <param name="FirstColumn">Leftmost column of the block.</param>
/// <param name="LastRow">Bottom row of the block.</param>
/// <param name="LastColumn">Rightmost column of the block.</param>
public readonly record struct XlsxMerge(int FirstRow, int FirstColumn, int LastRow, int LastColumn)
{
    /// <summary>The range written the way Excel refers to it, such as <c>A2:A17</c>.</summary>
    public string Reference =>
        XlsxWriter.ColumnName(FirstColumn) + FirstRow.ToString(CultureInfo.InvariantCulture)
        + ":" + XlsxWriter.ColumnName(LastColumn) + LastRow.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The colours a workbook is painted with, so an export can follow whatever theme the app is
/// wearing.
/// </summary>
/// <remarks>
/// <para>
/// Only the header band and the grid are themed. The body is left on Excel's own white with black
/// text, which is not an oversight: a sheet is read and printed as often as it is looked at on
/// screen, and a dark fill behind every cell costs ink and readability without telling anyone
/// anything. The header carries the theme; the rows carry the data.
/// </para>
/// <para>
/// Each colour is <c>AARRGGBB</c> hex, with or without a leading <c>#</c>. Anything unparseable
/// falls back rather than throwing — a mistyped colour should not cost somebody their export.
/// </para>
/// </remarks>
/// <param name="HeaderFill">Background of the header row.</param>
/// <param name="HeaderText">Text colour of the header row, which has to read against the fill.</param>
/// <param name="GridLine">Colour of the cell borders.</param>
public sealed record XlsxPalette(string HeaderFill, string HeaderText, string GridLine)
{
    /// <summary>
    /// What a caller that says nothing gets: the light theme's chrome, which reads on the white a
    /// spreadsheet starts as.
    /// </summary>
    public static XlsxPalette Default { get; } = new("FFEBEFF4", "FF1B2430", "FFC3CDD9");
}

/// <summary>One worksheet — the "tab" a reader sees along the bottom of the workbook.</summary>
public sealed class XlsxSheet
{
    /// <param name="name">Tab caption. Sanitised on write; see <see cref="XlsxWriter"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public XlsxSheet(string name) => Name = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>Tab caption as requested. Excel's own rules are applied when the file is written.</summary>
    public string Name { get; }

    /// <summary>
    /// Column widths in Excel's character units, left to right. Short lists are fine — any column
    /// past the end keeps the default width.
    /// </summary>
    public List<double> ColumnWidths { get; } = new();

    /// <summary>Rows, top to bottom. Rows may differ in length; trailing empties cost nothing.</summary>
    public List<IReadOnlyList<XlsxCell>> Rows { get; } = new();

    /// <summary>Rows held still when the sheet scrolls. 1 keeps the header visible; 0 freezes nothing.</summary>
    public int FrozenRows { get; set; } = 1;

    /// <summary>
    /// Blocks of cells to join, in the order they should be written. Overlapping blocks make a file
    /// Excel reports as damaged, so callers are responsible for keeping them apart.
    /// </summary>
    public List<XlsxMerge> Merges { get; } = new();
}

/// <summary>
/// Writes a small SpreadsheetML (<c>.xlsx</c>) workbook with no external dependency.
/// </summary>
/// <remarks>
/// <para>
/// An <c>.xlsx</c> file is a zip of XML parts, so the whole of what this app needs — several
/// sheets of strings and numbers, a bold header row, frozen panes and column widths — is a few
/// hundred lines of writing. That is a deliberate trade against taking a NuGet dependency on an
/// Office library: the app publishes as a single self-contained file, and every package added
/// there is weight shipped to every user for a feature most of them use occasionally.
/// </para>
/// <para>
/// Strings are written inline rather than through a shared-string table. That is fractionally
/// larger on disk and markedly simpler to get right, and these workbooks are measured in hundreds
/// of rows, not millions.
/// </para>
/// <para>
/// Output is deterministic: parts are written in a fixed order with a fixed timestamp, so the same
/// document exports to the same bytes twice.
/// </para>
/// </remarks>
public static class XlsxWriter
{
    /// <summary>Excel's hard limit on a sheet name.</summary>
    private const int MaxSheetNameLength = 31;

    /// <summary>Characters Excel forbids in a sheet name.</summary>
    private static readonly char[] ForbiddenInSheetName = { '[', ']', ':', '*', '?', '/', '\\' };

    /// <summary>
    /// Fixed entry timestamp. Zip cannot represent anything before 1980, and a real clock would
    /// make the output differ byte for byte between two otherwise identical exports.
    /// </summary>
    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    /// <summary>Writes <paramref name="sheets"/> to <paramref name="path"/>, replacing any existing file.</summary>
    /// <param name="path">Destination file.</param>
    /// <param name="sheets">Sheets to write, in tab order.</param>
    /// <param name="palette">Colours for the header and grid. Null takes <see cref="XlsxPalette.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="sheets"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sheets"/> is empty — a workbook needs a sheet.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void Write(string path, IReadOnlyList<XlsxSheet> sheets, XlsxPalette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sheets);

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(file, sheets, palette);
    }

    /// <summary>Writes <paramref name="sheets"/> into <paramref name="stream"/>, which is left open.</summary>
    /// <param name="stream">Destination stream, left open.</param>
    /// <param name="sheets">Sheets to write, in tab order.</param>
    /// <param name="palette">Colours for the header and grid. Null takes <see cref="XlsxPalette.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="sheets"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sheets"/> is empty.</exception>
    public static void Write(Stream stream, IReadOnlyList<XlsxSheet> sheets, XlsxPalette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(sheets);

        if (sheets.Count == 0)
            throw new ArgumentException("A workbook needs at least one sheet.", nameof(sheets));

        var names = ResolveSheetNames(sheets);

        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        AddEntry(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
        AddEntry(zip, "_rels/.rels", RootRelationships());
        AddEntry(zip, "xl/workbook.xml", Workbook(names));
        AddEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelationships(sheets.Count));
        AddEntry(zip, "xl/styles.xml", Styles(palette ?? XlsxPalette.Default));

        for (var i = 0; i < sheets.Count; i++)
            AddEntry(zip, "xl/worksheets/sheet" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml", Worksheet(sheets[i]));
    }

    // ------------------------------------------------------------------- package parts

    private static void AddEntry(ZipArchive zip, string name, string xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedTimestamp;

        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(xml);
    }

    private static string ContentTypes(int sheetCount)
    {
        const string OfficeDoc = "application/vnd.openxmlformats-officedocument.spreadsheetml";

        var builder = new StringBuilder();
        builder.Append(Declaration)
               .Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">")
               .Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>")
               .Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>")
               .Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"").Append(OfficeDoc).Append(".sheet.main+xml\"/>")
               .Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"").Append(OfficeDoc).Append(".styles+xml\"/>");

        for (var i = 1; i <= sheetCount; i++)
        {
            builder.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i.ToString(CultureInfo.InvariantCulture))
                   .Append(".xml\" ContentType=\"").Append(OfficeDoc).Append(".worksheet+xml\"/>");
        }

        return builder.Append("</Types>").ToString();
    }

    private static string RootRelationships() =>
        Declaration
        + "<Relationships xmlns=\"" + PkgRelNs + "\">"
        + "<Relationship Id=\"rId1\" Type=\"" + RelNs + "/officeDocument\" Target=\"xl/workbook.xml\"/>"
        + "</Relationships>";

    private static string Workbook(IReadOnlyList<string> names)
    {
        var builder = new StringBuilder();
        builder.Append(Declaration)
               .Append("<workbook xmlns=\"").Append(MainNs).Append("\" xmlns:r=\"").Append(RelNs).Append("\"><sheets>");

        for (var i = 0; i < names.Count; i++)
        {
            builder.Append("<sheet name=\"").Append(Escape(names[i]))
                   .Append("\" sheetId=\"").Append((i + 1).ToString(CultureInfo.InvariantCulture))
                   .Append("\" r:id=\"rId").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("\"/>");
        }

        return builder.Append("</sheets></workbook>").ToString();
    }

    private static string WorkbookRelationships(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.Append(Declaration).Append("<Relationships xmlns=\"").Append(PkgRelNs).Append("\">");

        for (var i = 1; i <= sheetCount; i++)
        {
            builder.Append("<Relationship Id=\"rId").Append(i.ToString(CultureInfo.InvariantCulture))
                   .Append("\" Type=\"").Append(RelNs).Append("/worksheet\" Target=\"worksheets/sheet")
                   .Append(i.ToString(CultureInfo.InvariantCulture)).Append(".xml\"/>");
        }

        // The styles part follows the sheets, so its id never collides with one of theirs.
        return builder.Append("<Relationship Id=\"rId").Append((sheetCount + 1).ToString(CultureInfo.InvariantCulture))
                      .Append("\" Type=\"").Append(RelNs).Append("/styles\" Target=\"styles.xml\"/>")
                      .Append("</Relationships>").ToString();
    }

    /// <summary>Cell format indices, which are positions in the <c>cellXfs</c> list below.</summary>
    private const string BodyStyle = "0";
    private const string HeaderStyle = "1";
    private const string NodeStyle = "2";
    private const string PaperStyle = "3";
    private const string OpenStyle = "4";

    /// <summary>
    /// The smallest style sheet Excel accepts that can still carry a themed header and a grid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three cell formats: 0 body, 1 header, 2 body pinned to the top of its cell. All three take
    /// the same thin border, because every cell in the used range is written — that is what draws
    /// the grid, and a cell left out would leave a hole in it.
    /// </para>
    /// <para>
    /// The first two fills must be <c>none</c> and <c>gray125</c> in that order however little use
    /// they are; Excel assumes the pair and misreads every later index without them. Borders are
    /// the same story, index 0 being the empty one. Colours are explicit RGB rather than theme
    /// references, because no theme part is written and a dangling theme reference makes Excel
    /// report the file as damaged.
    /// </para>
    /// </remarks>
    private static string Styles(XlsxPalette palette) =>
        Declaration
        + "<styleSheet xmlns=\"" + MainNs + "\">"
        + "<fonts count=\"2\">"
        + "<font><sz val=\"11\"/><color rgb=\"FF000000\"/><name val=\"Calibri\"/><family val=\"2\"/></font>"
        + "<font><b/><sz val=\"11\"/><color rgb=\"" + Argb(palette.HeaderText, XlsxPalette.Default.HeaderText)
        + "\"/><name val=\"Calibri\"/><family val=\"2\"/></font>"
        + "</fonts>"
        + "<fills count=\"4\">"
        + "<fill><patternFill patternType=\"none\"/></fill>"
        + "<fill><patternFill patternType=\"gray125\"/></fill>"
        + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\""
        + Argb(palette.HeaderFill, XlsxPalette.Default.HeaderFill)
        + "\"/><bgColor indexed=\"64\"/></patternFill></fill>"
        + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFFFFF\"/>"
        + "<bgColor indexed=\"64\"/></patternFill></fill>"
        + "</fills>"
        + "<borders count=\"2\">"
        + "<border><left/><right/><top/><bottom/><diagonal/></border>"
        + Thin(Argb(palette.GridLine, XlsxPalette.Default.GridLine))
        + "</borders>"
        + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
        + "<cellXfs count=\"5\">"
        + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\"/>"
        + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\""
        + " applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>"
        + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\""
        + " applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>"
        + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"3\" borderId=\"1\" xfId=\"0\""
        + " applyFill=\"1\" applyBorder=\"1\"/>"
        + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"3\" borderId=\"0\" xfId=\"0\""
        + " applyFill=\"1\" applyBorder=\"1\"/>"
        + "</cellXfs>"
        + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>"
        + "</styleSheet>";

    /// <summary>A thin border on all four sides in one colour. Child order is fixed by the schema.</summary>
    private static string Thin(string rgb)
    {
        var side = "<color rgb=\"" + rgb + "\"/>";

        return "<border>"
             + "<left style=\"thin\">" + side + "</left>"
             + "<right style=\"thin\">" + side + "</right>"
             + "<top style=\"thin\">" + side + "</top>"
             + "<bottom style=\"thin\">" + side + "</bottom>"
             + "<diagonal/></border>";
    }

    /// <summary>
    /// Normalises a colour to the eight hex digits Excel wants, falling back when it cannot.
    /// </summary>
    /// <remarks>
    /// Accepts <c>#AARRGGBB</c>, <c>AARRGGBB</c>, <c>#RRGGBB</c> and <c>RRGGBB</c>, taking a
    /// missing alpha as opaque. A colour that survives none of that is replaced rather than written
    /// through: one bad character in a palette would otherwise produce a workbook Excel refuses to
    /// open at all, which is a poor trade for a shade nobody would have noticed.
    /// </remarks>
    private static string Argb(string? value, string fallback)
    {
        var text = value?.TrimStart('#');

        if (text is null || (text.Length != 6 && text.Length != 8))
            return fallback;

        foreach (var c in text)
        {
            var hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex) return fallback;
        }

        return (text.Length == 6 ? "FF" + text : text).ToUpperInvariant();
    }

    private static string Worksheet(XlsxSheet sheet)
    {
        var builder = new StringBuilder();
        builder.Append(Declaration).Append("<worksheet xmlns=\"").Append(MainNs).Append("\">");

        // Schema order is fixed: sheetViews, then cols, then sheetData.
        builder.Append("<sheetViews><sheetView workbookViewId=\"0\">");
        if (sheet.FrozenRows > 0)
        {
            var top = (sheet.FrozenRows + 1).ToString(CultureInfo.InvariantCulture);
            builder.Append("<pane ySplit=\"").Append(sheet.FrozenRows.ToString(CultureInfo.InvariantCulture))
                   .Append("\" topLeftCell=\"A").Append(top)
                   .Append("\" activePane=\"bottomLeft\" state=\"frozen\"/>");
        }
        builder.Append("</sheetView></sheetViews>");

        if (sheet.ColumnWidths.Count > 0)
        {
            builder.Append("<cols>");
            for (var i = 0; i < sheet.ColumnWidths.Count; i++)
            {
                var index = (i + 1).ToString(CultureInfo.InvariantCulture);
                builder.Append("<col min=\"").Append(index).Append("\" max=\"").Append(index)
                       .Append("\" width=\"").Append(sheet.ColumnWidths[i].ToString("0.##", CultureInfo.InvariantCulture))
                       .Append("\" customWidth=\"1\"/>");
            }
            builder.Append("</cols>");
        }

        // Every row is written out to the width of the widest, so the grid is a rectangle. A row
        // that stopped at its last value would leave the border trailing off mid-sheet.
        var width = 0;
        foreach (var row in sheet.Rows)
            width = Math.Max(width, row.Count);

        builder.Append("<sheetData>");
        for (var r = 0; r < sheet.Rows.Count; r++)
            AppendRow(builder, sheet.Rows[r], r + 1, width);
        builder.Append("</sheetData>");

        // Schema order again: mergeCells follows sheetData, and Excel refuses the file outright if
        // it comes before.
        if (sheet.Merges.Count > 0)
        {
            builder.Append("<mergeCells count=\"")
                   .Append(sheet.Merges.Count.ToString(CultureInfo.InvariantCulture)).Append("\">");

            foreach (var merge in sheet.Merges)
                builder.Append("<mergeCell ref=\"").Append(merge.Reference).Append("\"/>");

            builder.Append("</mergeCells>");
        }

        return builder.Append("</worksheet>").ToString();
    }

    /// <summary>
    /// Writes one row out to <paramref name="width"/> columns, blanks included.
    /// </summary>
    /// <remarks>
    /// Empty cells used to be left out entirely, which made a wide staircase cheap — most of it is
    /// nothing at all. They are written now because a border belongs to a cell: skip the cell and
    /// the grid loses that square, leaving the blank half of every staircase row unruled. Writing
    /// them back costs an eight-byte element each and buys a grid with no holes in it, and these
    /// workbooks are hundreds of rows rather than millions.
    /// </remarks>
    private static void AppendRow(StringBuilder builder, IReadOnlyList<XlsxCell> cells, int rowNumber, int width)
    {
        var row = rowNumber.ToString(CultureInfo.InvariantCulture);
        builder.Append("<row r=\"").Append(row).Append("\">");

        for (var c = 0; c < width; c++)
        {
            var cell = c < cells.Count ? cells[c] : XlsxCell.Empty;
            var reference = ColumnName(c + 1) + row;

            if (cell.IsEmpty)
            {
                var blank = cell.Unruled ? OpenStyle : cell.WhiteFill ? PaperStyle : BodyStyle;
                builder.Append("<c r=\"").Append(reference).Append("\" s=\"").Append(blank).Append("\"/>");
                continue;
            }

            var style = cell.Bold ? HeaderStyle : cell.TopAligned ? NodeStyle : BodyStyle;

            if (cell.Number is { } number)
            {
                builder.Append("<c r=\"").Append(reference).Append("\" s=\"").Append(style).Append("\"><v>")
                       .Append(number.ToString("R", CultureInfo.InvariantCulture))
                       .Append("</v></c>");
                continue;
            }

            builder.Append("<c r=\"").Append(reference).Append("\" s=\"").Append(style)
                   .Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                   .Append(Escape(cell.Text!))
                   .Append("</t></is></c>");
        }

        builder.Append("</row>");
    }

    // ------------------------------------------------------------------- naming and escaping

    /// <summary>
    /// Applies Excel's sheet-name rules and makes the results unique, because a workbook with two
    /// identically named sheets will not open at all.
    /// </summary>
    private static List<string> ResolveSheetNames(IReadOnlyList<XlsxSheet> sheets)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(sheets.Count);

        for (var i = 0; i < sheets.Count; i++)
        {
            var name = SanitizeSheetName(sheets[i].Name, i);

            if (used.Add(name))
            {
                names.Add(name);
                continue;
            }

            // Append " (2)", " (3)" … trimming the stem so the result still fits.
            for (var suffix = 2; ; suffix++)
            {
                var tail = " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
                var stem = name.Length + tail.Length <= MaxSheetNameLength
                    ? name
                    : name[..(MaxSheetNameLength - tail.Length)].TrimEnd();

                var candidate = stem + tail;
                if (!used.Add(candidate)) continue;

                names.Add(candidate);
                break;
            }
        }

        return names;
    }

    private static string SanitizeSheetName(string name, int index)
    {
        var cleaned = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (Array.IndexOf(ForbiddenInSheetName, c) >= 0) continue;
            if (c < ' ') continue;
            cleaned.Append(c);
        }

        // Excel also refuses a name that starts or ends with an apostrophe.
        var result = cleaned.ToString().Trim().Trim('\'').Trim();

        if (result.Length > MaxSheetNameLength)
            result = result[..MaxSheetNameLength].TrimEnd();

        return result.Length == 0 ? "Sheet" + (index + 1).ToString(CultureInfo.InvariantCulture) : result;
    }

    /// <summary>Turns a 1-based column index into Excel's letters: 1 becomes A, 27 becomes AA.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is below 1.</exception>
    public static string ColumnName(int index)
    {
        if (index < 1) throw new ArgumentOutOfRangeException(nameof(index), index, "Columns are 1-based.");

        var name = new StringBuilder(3);
        while (index > 0)
        {
            var remainder = (index - 1) % 26;
            name.Insert(0, (char)('A' + remainder));
            index = (index - 1) / 26;
        }

        return name.ToString();
    }

    /// <summary>Excel refuses a workbook holding a cell longer than this.</summary>
    private const int MaxCellText = 32_767;

    /// <summary>
    /// Escapes text for XML content and drops what XML 1.0 cannot represent.
    /// FOM semantics fields are free text written by hand, so both halves of that matter.
    /// </summary>
    /// <remarks>
    /// Everything a caller writes reaches the file through here, which is the only reason one
    /// method can be trusted to keep the workbook openable.
    /// </remarks>
    private static string Escape(string value)
    {
        // Trimmed before escaping rather than after: a cap applied to the escaped text could cut an
        // entity in half, and Excel counts the characters a reader sees, not the ones on disk.
        if (value.Length > MaxCellText) value = value[..(MaxCellText - 1)] + "…";

        var builder = new StringBuilder(value.Length + 16);

        foreach (var c in value)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&apos;"); break;
                case '\t':
                case '\n':
                case '\r': builder.Append(c); break;

                // The two non-characters XML forbids outright. Unlike a lone surrogate — which the
                // UTF-8 encoder quietly replaces on the way out — these are valid Unicode scalars
                // that encode happily and then make the part unreadable, so the encoder will not
                // save us and they have to go here.
                case '\uFFFE':
                case '\uFFFF': break;

                default:
                    if (c >= ' ') builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}
