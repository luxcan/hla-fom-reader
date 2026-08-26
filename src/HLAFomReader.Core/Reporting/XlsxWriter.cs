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
public readonly record struct XlsxCell(string? Text, double? Number, bool Bold)
{
    /// <summary>True when the cell should be left out of the sheet entirely.</summary>
    public bool IsEmpty => Text is null && Number is null;

    /// <summary>A cell that is not written at all — the blanks in a hierarchy staircase.</summary>
    public static XlsxCell Empty => default;

    /// <summary>A text cell. Null or empty text collapses to <see cref="Empty"/>.</summary>
    public static XlsxCell Str(string? value) =>
        string.IsNullOrEmpty(value) ? default : new XlsxCell(value, null, false);

    /// <summary>A bold text cell, used for the header row.</summary>
    public static XlsxCell Head(string value) => new(value, null, true);

    /// <summary>A numeric cell.</summary>
    public static XlsxCell Num(double value) => new(null, value, false);
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
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sheets"/> is empty — a workbook needs a sheet.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void Write(string path, IReadOnlyList<XlsxSheet> sheets)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sheets);

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(file, sheets);
    }

    /// <summary>Writes <paramref name="sheets"/> into <paramref name="stream"/>, which is left open.</summary>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sheets"/> is empty.</exception>
    public static void Write(Stream stream, IReadOnlyList<XlsxSheet> sheets)
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
        AddEntry(zip, "xl/styles.xml", Styles());

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

    /// <summary>
    /// The smallest style sheet Excel accepts, carrying two cell formats: 0 plain and 1 bold.
    /// Colours are given as explicit RGB rather than theme references, because no theme part is
    /// written and a dangling theme reference makes Excel report the file as damaged.
    /// </summary>
    private static string Styles() =>
        Declaration
        + "<styleSheet xmlns=\"" + MainNs + "\">"
        + "<fonts count=\"2\">"
        + "<font><sz val=\"11\"/><color rgb=\"FF000000\"/><name val=\"Calibri\"/><family val=\"2\"/></font>"
        + "<font><b/><sz val=\"11\"/><color rgb=\"FF000000\"/><name val=\"Calibri\"/><family val=\"2\"/></font>"
        + "</fonts>"
        + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill>"
        + "<fill><patternFill patternType=\"gray125\"/></fill></fills>"
        + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
        + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
        + "<cellXfs count=\"2\">"
        + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
        + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"
        + "</cellXfs>"
        + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>"
        + "</styleSheet>";

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

        builder.Append("<sheetData>");
        for (var r = 0; r < sheet.Rows.Count; r++)
            AppendRow(builder, sheet.Rows[r], r + 1);
        builder.Append("</sheetData>");

        return builder.Append("</worksheet>").ToString();
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<XlsxCell> cells, int rowNumber)
    {
        var row = rowNumber.ToString(CultureInfo.InvariantCulture);
        builder.Append("<row r=\"").Append(row).Append("\">");

        for (var c = 0; c < cells.Count; c++)
        {
            var cell = cells[c];

            // Empty cells are left out rather than written blank. That is what makes the hierarchy
            // staircase cheap: most of a wide sheet is nothing at all.
            if (cell.IsEmpty) continue;

            var reference = ColumnName(c + 1) + row;
            var style = cell.Bold ? " s=\"1\"" : "";

            if (cell.Number is { } number)
            {
                builder.Append("<c r=\"").Append(reference).Append('"').Append(style).Append("><v>")
                       .Append(number.ToString("R", CultureInfo.InvariantCulture))
                       .Append("</v></c>");
                continue;
            }

            builder.Append("<c r=\"").Append(reference).Append('"').Append(style)
                   .Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
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

    /// <summary>
    /// Escapes text for XML content and drops the control characters XML 1.0 cannot represent.
    /// FOM semantics fields are free text written by hand, so both halves of that matter.
    /// </summary>
    private static string Escape(string value)
    {
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
                default:
                    if (c >= ' ') builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}
