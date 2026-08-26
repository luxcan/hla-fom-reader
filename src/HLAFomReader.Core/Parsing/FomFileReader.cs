using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Parsing;

/// <summary>
/// One kind of input file the reader accepts, as offered in the Open dialog.
/// </summary>
/// <param name="Standard">The HLA standard files of this format are parsed as.</param>
/// <param name="DisplayName">Human label, e.g. <c>IEEE 1516-2010 Evolved FOM module (XML)</c>.</param>
/// <param name="Extensions">Lower-case extensions including the leading dot, e.g. <c>.xml</c>.</param>
public sealed record FomFileFormat(FomStandard Standard, string DisplayName, string[] Extensions)
{
    /// <summary>Extension mask for a file dialog group, e.g. <c>*.xml;*.fdd</c>.</summary>
    public string FilterMask => string.Join(";", Extensions.Select(e => "*" + e));

    /// <summary>True when <paramref name="extension"/> — with or without the leading dot — belongs to this format.</summary>
    public bool HasExtension(string? extension)
    {
        var normalized = FomFileReader.NormalizeExtension(extension);
        return normalized.Length != 0
            && Extensions.Any(e => string.Equals(e, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public override string ToString() => DisplayName;
}

/// <summary>
/// Entry point for reading FOM/FED files: sniffs the head of a file to work out which
/// HLA standard it follows, then hands it to the matching <see cref="IFomParser"/>.
/// Nothing here throws for bad input — a file that cannot be opened or understood comes
/// back as a <see cref="FomDocument"/> carrying <see cref="ParseDiagnostic"/>s.
/// </summary>
public static class FomFileReader
{
    /// <summary>How much of a file is inspected when detecting the standard.</summary>
    private const int MaxHeadBytes = 8 * 1024;

    /// <summary>UTF-8 without a preamble that substitutes U+FFFD instead of throwing on bad bytes.</summary>
    private static readonly UTF8Encoding PermissiveUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly RegexOptions SniffOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>Matches an <c>&lt;objectModel&gt;</c> start tag carrying any namespace prefix.</summary>
    private static readonly Regex ObjectModelTagRegex =
        new(@"<\s*(?:[A-Za-z_][\w.\-]*\s*:\s*)?objectModel\b", SniffOptions);

    /// <summary>Matches the opening <c>(FED</c> token of an HLA 1.3 FED file.</summary>
    private static readonly Regex FedOpeningRegex = new(@"^\s*\(\s*FED\b", SniffOptions);

    /// <summary>Matches the <c>(FEDversion ...)</c> clause, which only ever appears in FED files.</summary>
    private static readonly Regex FedVersionRegex = new(@"\(\s*FEDversion\b", SniffOptions);

    /// <summary>Matches the OMT DIF header, e.g. <c>(DIF HLA-OMT v1.3 (TYPE Single))</c>.</summary>
    private static readonly Regex OmtDifHeaderRegex = new(@"^\s*\(\s*DIF\s+HLA-OMT\b", SniffOptions);

    /// <summary>Matches the OMDT native header of a <c>.omd</c> file, e.g. <c>(OMDT v1.3.5.17)</c>.</summary>
    private static readonly Regex OmdtHeaderRegex = new(@"^\s*\(\s*OMDT\b", SniffOptions);

    /// <summary>Matches the <c>(ObjectModel ...)</c> body element shared by both OMT headers.</summary>
    private static readonly Regex ObjectModelClauseRegex = new(@"\(\s*ObjectModel\b", SniffOptions);

    /// <summary>Matches clauses that only ever appear in an OMT object model, never in a FED.</summary>
    private static readonly Regex OmtBodyRegex = new(@"\(\s*(?:PSCapabilities|ComplexDataType)\b", SniffOptions);

    /// <summary>
    /// Namespace / schema-location fragments that identify a 1516 revision, most specific first.
    /// The first fragment found in the head of the document wins.
    /// </summary>
    private static readonly (string Token, FomStandard Standard)[] NamespaceTokens =
    {
        ("IEEE1516-DIF-2010", FomStandard.Ieee1516_2010),
        ("IEEE1516-2010", FomStandard.Ieee1516_2010),
        ("IEEE1516-DIF-2025", FomStandard.Ieee1516_2025),
        ("IEEE1516-2025", FomStandard.Ieee1516_2025),
        ("IEEE1516-DIF-2000", FomStandard.Ieee1516_2000),
        ("IEEE1516-2000", FomStandard.Ieee1516_2000),
    };

    private static readonly FomFileFormat[] Formats =
    {
        new(FomStandard.Hla13, "HLA 1.3 FED file", new[] { ".fed" }),
        new(FomStandard.Hla13, "HLA 1.3 OMT object model (.omt, .omd)", new[] { ".omt", ".omd" }),
        new(FomStandard.Ieee1516_2000, "IEEE 1516-2000 FOM (XML)", new[] { ".xml", ".fdd" }),
        new(FomStandard.Ieee1516_2010, "IEEE 1516-2010 Evolved FOM module (XML)", new[] { ".xml", ".fdd" }),
        new(FomStandard.Ieee1516_2025, "IEEE 1516-2025 FOM (XML)", new[] { ".xml", ".fdd" }),
    };

    /// <summary>Every input format the reader understands, in dialog order.</summary>
    public static IReadOnlyList<FomFileFormat> SupportedFormats => Formats;

    /// <summary>
    /// Works out which standard <paramref name="filePath"/> follows by reading at most the first
    /// 8 KB of it. A missing, locked or unreadable file yields <see cref="FomStandard.Unknown"/>
    /// rather than an exception.
    /// </summary>
    public static FomStandard DetectStandard(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return FomStandard.Unknown;

        var extension = GetExtension(filePath);

        return TryReadHead(filePath, out var head, out _)
            ? DetectStandardFromContent(head, extension)
            : FomStandard.Unknown;
    }

    /// <summary>
    /// Works out which standard a document follows from the first few kilobytes of its text.
    /// XML evidence (namespace URI or schema location) always beats the file extension;
    /// <paramref name="fileExtension"/> is only consulted when the content is ambiguous.
    /// </summary>
    /// <param name="headText">Head of the document; may be truncated mid-element.</param>
    /// <param name="fileExtension">Extension with or without the leading dot, if known.</param>
    public static FomStandard DetectStandardFromContent(string headText, string? fileExtension = null)
    {
        var head = headText ?? "";
        var extension = NormalizeExtension(fileExtension);

        if (LooksLikeXml(head))
        {
            foreach (var (token, standard) in NamespaceTokens)
            {
                if (head.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return standard;
            }

            // An OMT DIF document with no usable namespace: 1516-2010 is by far the most
            // common flavour behind a bare .xml/.fdd file, so assume that and let the parser
            // report anything that does not fit.
            return extension is ".xml" or ".fdd" ? FomStandard.Ieee1516_2010 : FomStandard.Unknown;
        }

        // Both HLA 1.3 dialects report the same standard; DetectHla13Dialect tells them apart.
        if (LooksLikeOmt(head) || LooksLikeFed(head) || extension is ".fed" or ".omt" or ".omd")
            return FomStandard.Hla13;

        return FomStandard.Unknown;
    }

    /// <summary>
    /// Detects the standard of <paramref name="filePath"/> and parses it with the matching parser.
    /// IO failures and undetectable formats are reported through
    /// <see cref="FomDocument.Diagnostics"/>; this method does not throw.
    /// </summary>
    public static FomDocument ParseFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return CreateFailureDocument(FomStandard.Unknown, null, "No file path was supplied.");

        var fullPath = ToAbsolutePath(filePath);
        var standard = DetectStandard(fullPath);

        if (!TryReadAllText(fullPath, out var text, out var ioError))
            return CreateFailureDocument(standard, fullPath, ioError);

        // FED and OMT are both HLA 1.3, so the standard alone can no longer pick the parser.
        var dialect = DetectHla13Dialect(Head(text), GetExtension(fullPath));

        var document = ParseText(text, standard, fullPath, dialect);
        document.SourcePath = fullPath;
        return document;
    }

    /// <summary>
    /// Parses an already-opened document as <paramref name="standard"/>. Passing
    /// <see cref="FomStandard.Unknown"/> runs the best-effort detection described on
    /// <see cref="ParseFile"/>: XML first, then the HLA 1.3 dialect the text looks like.
    /// </summary>
    /// <remarks>
    /// A caller who asks for <see cref="FomStandard.Hla13"/> explicitly gets the FED parser. The
    /// reader has to be handed to that parser unread, so there is nothing to sniff the 1.3 dialect
    /// from; use <see cref="ParseFile"/>, which has the path and the text, to have an OMT object
    /// model recognised automatically.
    /// </remarks>
    public static FomDocument Parse(TextReader reader, FomStandard standard, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var path = sourcePath is null ? null : ToAbsolutePath(sourcePath);

        if (standard == FomStandard.Unknown)
        {
            string text;
            try
            {
                text = reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OutOfMemoryException)
            {
                return CreateFailureDocument(FomStandard.Unknown, path, $"Could not read the document: {ex.Message}");
            }

            // The whole text is in hand here, so the 1.3 dialect can be sniffed after all.
            var sniffed = DetectHla13Dialect(Head(text), path is null ? "" : GetExtension(path));
            return ParseText(text, FomStandard.Unknown, path, sniffed);
        }

        var document = RunParser(CreateParser(standard, Hla13Dialect.Fed), reader, path);
        document.SourcePath ??= path;
        return document;
    }

    /// <summary>
    /// Returns a WPF <c>OpenFileDialog.Filter</c> string: a combined group first, one group per
    /// supported format, then an all-files escape hatch.
    /// </summary>
    public static string BuildOpenFileDialogFilter()
    {
        var allExtensions = Formats
            .SelectMany(f => f.Extensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allMask = string.Join(";", allExtensions.Select(e => "*" + e));

        var builder = new StringBuilder();
        builder.Append("All FOM files (").Append(allMask).Append(")|").Append(allMask);

        foreach (var format in Formats)
            builder.Append('|').Append(format.DisplayName).Append(" (").Append(format.FilterMask).Append(")|").Append(format.FilterMask);

        builder.Append("|All files (*.*)|*.*");
        return builder.ToString();
    }

    /// <summary>Lower-cases an extension and gives it a leading dot; returns "" when there is none.</summary>
    internal static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return "";

        var trimmed = extension.Trim();
        if (trimmed.StartsWith("*", StringComparison.Ordinal))
            trimmed = trimmed[1..];
        if (trimmed.Length == 0)
            return "";
        if (!trimmed.StartsWith(".", StringComparison.Ordinal))
            trimmed = "." + trimmed;

        return trimmed.ToLowerInvariant();
    }

    // ---------------------------------------------------------------- dispatch

    /// <summary>Which of the two parenthesised HLA 1.3 grammars a document is written in.</summary>
    /// <remarks>
    /// Both report <see cref="FomStandard.Hla13"/>, so detection has to carry this alongside the
    /// standard to reach the right parser.
    /// </remarks>
    private enum Hla13Dialect
    {
        /// <summary>Federation Execution Data, <c>(FED …)</c> in a <c>.fed</c> file.</summary>
        Fed = 0,

        /// <summary>An OMT object model, <c>(ObjectModel …)</c> in a <c>.omt</c> or <c>.omd</c> file.</summary>
        Omt,
    }

    /// <summary>Parses text that is already in memory, handling the Unknown fallback path.</summary>
    private static FomDocument ParseText(string text, FomStandard standard, string? sourcePath, Hla13Dialect dialect)
    {
        if (standard != FomStandard.Unknown)
        {
            var known = RunParser(CreateParser(standard, dialect), new StringReader(text), sourcePath);
            known.SourcePath ??= sourcePath;
            return known;
        }

        // Best effort: XML is the stricter grammar of the two, so a FED file reliably fails it.
        var attempted = FomStandard.Ieee1516_2010;
        var document = RunParser(CreateParser(attempted, dialect), new StringReader(text), sourcePath);

        if (!HasContent(document))
        {
            var hla13 = RunParser(CreateParser(FomStandard.Hla13, dialect), new StringReader(text), sourcePath);
            if (HasContent(hla13))
            {
                attempted = FomStandard.Hla13;
                document = hla13;
            }
        }

        document.SourcePath ??= sourcePath;

        var label = document.Standard == FomStandard.Unknown
            ? DescribeStandard(attempted)
            : document.StandardDisplayName;
        document.Diagnostics.Insert(0, new ParseDiagnostic(
            DiagnosticSeverity.Warning,
            $"Could not determine the HLA standard from the file; parsed as {label}."));

        return document;
    }

    private static IFomParser CreateParser(FomStandard standard, Hla13Dialect dialect) => standard switch
    {
        FomStandard.Hla13 when dialect == Hla13Dialect.Omt => new OmtDifParser(),
        FomStandard.Hla13 => new FedParser(),
        FomStandard.Ieee1516_2000 => new Ieee1516XmlParser(FomStandard.Ieee1516_2000),
        FomStandard.Ieee1516_2025 => new Ieee1516XmlParser(FomStandard.Ieee1516_2025),
        _ => new Ieee1516XmlParser(FomStandard.Ieee1516_2010),
    };

    /// <summary>
    /// Runs a parser, turning a parser that misbehaves badly enough to throw into an
    /// error document so one broken file can never take the application down.
    /// </summary>
    private static FomDocument RunParser(IFomParser parser, TextReader reader, string? sourcePath)
    {
        try
        {
            return parser.Parse(reader, sourcePath)
                   ?? CreateFailureDocument(parser.Standard, sourcePath, "The parser returned no document.");
        }
        catch (Exception ex)
        {
            return CreateFailureDocument(parser.Standard, sourcePath, $"Unhandled error while parsing: {ex.Message}");
        }
    }

    /// <summary>True when a parse produced something worth keeping, as opposed to an empty shell.</summary>
    private static bool HasContent(FomDocument document) =>
        document.ObjectClasses.Count > 0
        || document.InteractionClasses.Count > 0
        || document.DataTypes.TotalCount > 0
        || document.Dimensions.Count > 0
        || document.RoutingSpaces.Count > 0
        || document.Transportations.Count > 0
        || document.Synchronizations.Count > 0
        || document.UpdateRates.Count > 0
        || document.Switches.Count > 0
        || document.Tags.Count > 0
        || document.Notes.Count > 0
        || !string.IsNullOrWhiteSpace(document.Identification.Name);

    private static FomDocument CreateFailureDocument(FomStandard standard, string? sourcePath, string message)
    {
        var document = new FomDocument { Standard = standard, SourcePath = sourcePath };
        document.Diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, message));
        return document;
    }

    /// <summary>Mirrors <see cref="FomDocument.StandardDisplayName"/> for a standard with no document.</summary>
    private static string DescribeStandard(FomStandard standard) => standard switch
    {
        FomStandard.Hla13 => "HLA 1.3",
        FomStandard.Ieee1516_2000 => "IEEE 1516-2000",
        FomStandard.Ieee1516_2010 => "IEEE 1516-2010 (Evolved)",
        FomStandard.Ieee1516_2025 => "IEEE 1516-2025",
        _ => "Unknown",
    };

    // ---------------------------------------------------------------- sniffing

    /// <summary>
    /// True when the head opens as XML: an XML declaration, or an <c>&lt;objectModel&gt;</c>
    /// root reached past any leading whitespace, comments, processing instructions or DOCTYPE.
    /// </summary>
    private static bool LooksLikeXml(string head)
    {
        if (head.Length == 0)
            return false;

        var i = 0;
        // A BOM survives decoding when the text was handed to us without BOM stripping.
        if (head[0] == '\uFEFF')
            i = 1;

        while (i < head.Length)
        {
            while (i < head.Length && char.IsWhiteSpace(head[i]))
                i++;
            if (i >= head.Length || head[i] != '<')
                break;

            if (Matches(head, i, "<?xml") && (i + 5 >= head.Length || !char.IsLetterOrDigit(head[i + 5])))
                return true;

            if (Matches(head, i, "<?"))
            {
                i = SkipTo(head, i + 2, "?>");
                continue;
            }

            if (Matches(head, i, "<!--"))
            {
                i = SkipTo(head, i + 4, "-->");
                continue;
            }

            if (Matches(head, i, "<![CDATA["))
            {
                i = SkipTo(head, i + 9, "]]>");
                continue;
            }

            if (Matches(head, i, "<!"))
            {
                i = SkipDeclaration(head, i + 2);
                continue;
            }

            // First real element: accept it only when its local name is objectModel.
            return string.Equals(ReadLocalName(head, i + 1), "objectModel", StringComparison.OrdinalIgnoreCase);
        }

        // Truncated or oddly framed head: fall back on a plain search for the root tag.
        return ObjectModelTagRegex.IsMatch(head);
    }

    /// <summary>True when the head, with FED <c>;</c> comments removed, opens as an HLA 1.3 FED file.</summary>
    private static bool LooksLikeFed(string head)
    {
        var stripped = StripFedComments(head);
        return FedOpeningRegex.IsMatch(stripped) || FedVersionRegex.IsMatch(stripped);
    }

    /// <summary>
    /// True when the head is an HLA 1.3 OMT object model: either of the two header lines, or an
    /// <c>(ObjectModel …)</c> body carrying a clause no FED can express.
    /// </summary>
    private static bool LooksLikeOmt(string head)
    {
        // A byte-order mark is not whitespace to the regex engine, so it would defeat the anchors.
        var stripped = StripFedComments(head).TrimStart('\uFEFF');

        if (OmtDifHeaderRegex.IsMatch(stripped) || OmdtHeaderRegex.IsMatch(stripped))
            return true;

        return ObjectModelClauseRegex.IsMatch(stripped) && OmtBodyRegex.IsMatch(stripped);
    }

    /// <summary>
    /// Decides which parenthesised HLA 1.3 grammar a document uses. OMT is tested first because
    /// both grammars are parenthesised and only the FED markers are unambiguous the other way;
    /// a file that shows neither falls back on its extension, and then on FED.
    /// </summary>
    private static Hla13Dialect DetectHla13Dialect(string head, string? fileExtension)
    {
        if (LooksLikeOmt(head))
            return Hla13Dialect.Omt;

        if (LooksLikeFed(head))
            return Hla13Dialect.Fed;

        return NormalizeExtension(fileExtension) is ".omt" or ".omd" ? Hla13Dialect.Omt : Hla13Dialect.Fed;
    }

    /// <summary>The leading portion of a document that detection inspects.</summary>
    private static string Head(string text) =>
        text.Length <= MaxHeadBytes ? text : text[..MaxHeadBytes];

    /// <summary>The extension of a path, or "" when the path has none or cannot be parsed.</summary>
    private static string GetExtension(string path)
    {
        try
        {
            return Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    /// <summary>Removes <c>;</c>-to-end-of-line comments, leaving quoted strings intact.</summary>
    private static string StripFedComments(string text)
    {
        if (text.IndexOf(';') < 0)
            return text;

        var builder = new StringBuilder(text.Length);
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
                builder.Append(c);
                continue;
            }

            if (c == ';' && !inQuotes)
            {
                while (i < text.Length && text[i] != '\n' && text[i] != '\r')
                    i++;
                if (i < text.Length)
                    builder.Append(text[i]);   // keep the line break so line structure survives
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Reads the element name at <paramref name="start"/>, dropping any namespace prefix.</summary>
    private static string ReadLocalName(string text, int start)
    {
        var i = start;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        var begin = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-' or '.' or ':'))
            i++;

        var name = text[begin..i];
        var colon = name.LastIndexOf(':');
        return colon >= 0 ? name[(colon + 1)..] : name;
    }

    /// <summary>Skips a <c>&lt;!...&gt;</c> declaration, including a DOCTYPE internal subset.</summary>
    private static int SkipDeclaration(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close < 0)
                    return text.Length;
                i = close;
                continue;
            }

            if (text[i] == '>')
                return i + 1;
        }

        return text.Length;
    }

    /// <summary>Returns the index just past <paramref name="terminator"/>, or the end of the text.</summary>
    private static int SkipTo(string text, int start, string terminator)
    {
        var index = text.IndexOf(terminator, Math.Min(start, text.Length), StringComparison.Ordinal);
        return index < 0 ? text.Length : index + terminator.Length;
    }

    /// <summary>True when <paramref name="token"/> appears in full at <paramref name="index"/>.</summary>
    private static bool Matches(string text, int index, string token) =>
        index >= 0
        && index + token.Length <= text.Length
        && string.Compare(text, index, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;

    // ---------------------------------------------------------------- file access

    /// <summary>Reads up to <see cref="MaxHeadBytes"/> bytes and decodes them permissively.</summary>
    private static bool TryReadHead(string path, out string headText, out string error)
    {
        headText = "";
        error = "";

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);

            var buffer = new byte[MaxHeadBytes];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
                total += read;

            headText = Decode(buffer, total);
            return true;
        }
        catch (Exception ex) when (IsFileAccessFailure(ex))
        {
            error = $"Could not read '{path}': {ex.Message}";
            return false;
        }
    }

    /// <summary>Reads a whole file, honouring any BOM and never failing on invalid byte sequences.</summary>
    private static bool TryReadAllText(string path, out string text, out string error)
    {
        text = "";
        error = "";

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, PermissiveUtf8, detectEncodingFromByteOrderMarks: true);

            text = reader.ReadToEnd();
            return true;
        }
        catch (Exception ex) when (IsFileAccessFailure(ex))
        {
            error = $"Could not read '{path}': {ex.Message}";
            return false;
        }
    }

    private static bool IsFileAccessFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException
            or OutOfMemoryException;

    /// <summary>
    /// Decodes a raw head buffer: honours a UTF-8/UTF-16/UTF-32 BOM, guesses UTF-16 from an
    /// interleaved-NUL pattern, and otherwise assumes UTF-8 with replacement for bad bytes.
    /// </summary>
    private static string Decode(byte[] buffer, int count)
    {
        if (count <= 0)
            return "";

        if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            return PermissiveUtf8.GetString(buffer, 3, count - 3);

        if (count >= 4 && buffer[0] == 0xFF && buffer[1] == 0xFE && buffer[2] == 0x00 && buffer[3] == 0x00)
            return new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: false).GetString(buffer, 4, count - 4);

        if (count >= 4 && buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0xFE && buffer[3] == 0xFF)
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: false).GetString(buffer, 4, count - 4);

        if (count >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false).GetString(buffer, 2, count - 2);

        if (count >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false).GetString(buffer, 2, count - 2);

        // No BOM: ASCII text saved as UTF-16 leaves every second byte zero.
        if (count >= 4 && buffer[0] != 0x00 && buffer[1] == 0x00 && buffer[2] != 0x00 && buffer[3] == 0x00)
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false).GetString(buffer, 0, count);

        if (count >= 4 && buffer[0] == 0x00 && buffer[1] != 0x00 && buffer[2] == 0x00 && buffer[3] != 0x00)
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false).GetString(buffer, 0, count);

        return PermissiveUtf8.GetString(buffer, 0, count);
    }

    /// <summary>Best-effort absolute path; an unusable path is handed back unchanged.</summary>
    private static string ToAbsolutePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return path;
        }
    }
}
