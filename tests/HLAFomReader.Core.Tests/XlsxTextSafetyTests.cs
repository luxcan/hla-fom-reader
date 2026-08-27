using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Reporting;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// What the writer does with text it cannot put in a worksheet as it stands.
/// </summary>
/// <remarks>
/// <para>
/// A cell that Excel or the XML parser refuses does not spoil the cell — it spoils the whole
/// workbook, because a worksheet part that will not parse takes the file with it. So the writer has
/// to be the one place this is dealt with, and it has to deal with all of it: every text cell in
/// every sheet reaches the file through <c>Escape</c>.
/// </para>
/// <para>
/// This became reachable with the member sheets. Until then an export wrote class names, sharing
/// tokens and numbers — all of them parser output, drawn from a small vocabulary. The member sheets
/// write the <c>Semantics</c> field, which is prose an author typed into a FOM by hand, and can hold
/// anything their editor let them type.
/// </para>
/// </remarks>
public sealed class XlsxTextSafetyTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>
    /// The two non-characters XML forbids are dropped, rather than making the part unreadable.
    /// </summary>
    /// <remarks>
    /// Unlike a lone surrogate, which the UTF-8 encoder replaces on the way out, <c>U+FFFE</c> and
    /// <c>U+FFFF</c> are valid Unicode scalars that encode perfectly well and are then rejected by
    /// every XML reader — including the one Excel uses. The encoder cannot save us, so the writer
    /// must.
    /// </remarks>
    [Theory]
    [InlineData('\uFFFE')]
    [InlineData('\uFFFF')]
    public void TheNonCharactersXmlForbidsAreDropped(char forbidden)
    {
        var worksheet = WorksheetOf($"before{forbidden}after");

        Assert.Equal("beforeafter", FirstCellText(worksheet));
    }

    /// <summary>Control characters are dropped; tab and newline are kept.</summary>
    /// <remarks>
    /// A carriage return is written through as well, but comes back as a newline: XML normalises
    /// line endings on the way in, so no reader will ever hand one back. That is the parser rule
    /// rather than the writer's, and asserting a CR here would be asserting something no reader of
    /// the file can observe.
    /// </remarks>
    [Fact]
    public void ControlCharactersGoAndTheUsefulOnesStay()
    {
        var worksheet = WorksheetOf("a\u0001b\tc\nd\re");

        Assert.Equal("ab\tc\nd\ne", FirstCellText(worksheet));
    }

    /// <summary>The five XML entities are escaped, and come back as themselves.</summary>
    [Fact]
    public void TheXmlEntitiesSurviveTheRoundTrip()
    {
        var worksheet = WorksheetOf("a & b < c > d \" e ' f");

        Assert.Equal("a & b < c > d \" e ' f", FirstCellText(worksheet));
    }

    /// <summary>
    /// A cell longer than Excel's limit is trimmed to it, with an ellipsis so the loss is visible.
    /// </summary>
    /// <remarks>
    /// Excel refuses a workbook holding a cell over 32,767 characters — the whole file, not the
    /// cell. A semantics field that long is pathological rather than impossible, and losing the tail
    /// of one paragraph is a far better outcome than losing the export.
    /// </remarks>
    [Fact]
    public void AnOverlongCellIsTrimmedRatherThanRefused()
    {
        var text = FirstCellText(WorksheetOf(new string('x', 40_000)));

        Assert.Equal(32_767, text.Length);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    /// <summary>A cell exactly at the limit is left alone.</summary>
    [Fact]
    public void ACellAtTheLimitIsUntouched()
    {
        var text = FirstCellText(WorksheetOf(new string('x', 32_767)));

        Assert.Equal(new string('x', 32_767), text);
    }

    /// <summary>
    /// A semantics field carrying all of it at once still produces a workbook that parses.
    /// </summary>
    /// <remarks>
    /// The end-to-end case, exercised through the member exporter rather than a hand-built sheet,
    /// because that is the path this text actually travels.
    /// </remarks>
    [Fact]
    public void AHostileSemanticsFieldStillProducesAReadableWorkbook()
    {
        var document = new FomDocument();
        var root = new FomObjectClass { Name = "ObjectRoot", QualifiedName = "ObjectRoot" };

        root.Attributes.Add(new FomAttribute
        {
            Name = "Awkward",
            Semantics = "5 < 6 & \"quoted\" 'apostrophe' \uFFFF\u0007 tail",
        });

        document.ObjectClasses.Add(root);

        var stream = new MemoryStream();
        XlsxWriter.Write(
            stream,
            ClassHierarchyExporter.BuildSheets(document, new ClassExportSelection(new[] { "ObjectRoot" }, null)));

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        // Every part has to parse, not merely the one the awkward text landed in.
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal)))
        {
            using var part = entry.Open();
            XDocument.Load(part);
        }

        using var sheet3 = zip.GetEntry("xl/worksheets/sheet3.xml")!.Open();
        var cells = XDocument.Load(sheet3).Descendants(Main + "t").Select(t => t.Value).ToList();

        Assert.Contains("5 < 6 & \"quoted\" 'apostrophe'  tail", cells);
    }

    // ------------------------------------------------------------------- helpers

    /// <summary>Writes a one-cell sheet and reads the worksheet part back.</summary>
    private static XElement WorksheetOf(string text)
    {
        var sheet = new XlsxSheet("Sheet") { FrozenRows = 0 };
        sheet.Rows.Add(new[] { XlsxCell.Str(text) });

        var stream = new MemoryStream();
        XlsxWriter.Write(stream, new[] { sheet });
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var part = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();

        return XDocument.Load(part).Root!;
    }

    private static string FirstCellText(XElement worksheet) =>
        worksheet.Descendants(Main + "t").Single().Value;
}
