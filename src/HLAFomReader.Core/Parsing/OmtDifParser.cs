using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Parsing;

/// <summary>
/// Reads an HLA 1.3 OMT Data Interchange Format document — <c>.omt</c> as written by the DMSO OMT
/// tools, or <c>.omd</c> as written by OMDT — into a <see cref="FomDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// The grammar is the same parenthesised family as the 1.3 <c>.fed</c> file, so
/// <see cref="SExpressionReader"/> does the tokenising. The document is a header line
/// (<c>(DIF HLA-OMT v1.3 (TYPE Single))</c> or <c>(OMDT v1.3.5.17)</c>) followed by a single
/// <c>(ObjectModel …)</c> list holding the identification fields, the datatype tables and a
/// <em>flat</em> list of classes and interactions whose tree is expressed with integer
/// <c>SuperClass</c> / <c>SuperInteraction</c> pointers.
/// </para>
/// <para>
/// Unlike a FED, a 1.3 OMT document does carry datatypes, publish/subscribe capabilities and
/// semantics, so those parts of the normalised model are populated. Where 1.3 uses its own
/// vocabulary (<c>PS</c>, <c>TA</c>, <c>IR</c>) the value is expanded to the 1516 wording, so that
/// a 1.3 OMT document compares meaningfully against a 1516 FOM.
/// </para>
/// <para>
/// Real files in the wild are not always well formed — an unterminated quoted string in the
/// identification header is enough to destroy the parenthesis balance of everything that follows.
/// When the document as a whole cannot be trusted the parser falls back to recovery mode, which
/// finds each top-level block in the raw text and parses it on its own; see the remarks on
/// <see cref="Parse"/>.
/// </para>
/// <para>
/// Content problems are never thrown: they become <see cref="ParseDiagnostic"/>s and whatever
/// could be understood is still returned.
/// </para>
/// </remarks>
public sealed class OmtDifParser : IFomParser
{
    /// <summary>Guard against pathological nesting in a malformed file; real OMT trees are far shallower.</summary>
    private const int MaxClassDepth = 64;

    private const RegexOptions HeaderOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>Matches the OMT DIF header line, e.g. <c>(DIF HLA-OMT v1.3 (TYPE Single))</c>.</summary>
    private static readonly Regex DifHeaderRegex = new(@"^\s*\(\s*DIF\s+HLA-OMT\b", HeaderOptions);

    /// <summary>Matches the OMDT native header line, e.g. <c>(OMDT v1.3.5.17)</c>.</summary>
    private static readonly Regex OmdtHeaderRegex = new(@"^\s*\(\s*OMDT\b", HeaderOptions);

    /// <summary>The top-level blocks recovery mode looks for, in the order the tally reports them.</summary>
    private static readonly string[] BlockKeywords =
    {
        "Class", "Interaction", "ComplexDataType", "EnumeratedDataType", "Note",
    };

    /// <summary>The identification clauses of <c>(ObjectModel …)</c>, as recovery mode hunts for them.</summary>
    private static readonly string[] HeaderKeywords =
    {
        "Name", "VersionNumber", "Type", "Purpose", "ApplicationDomain", "SponsorOrgName",
        "POCHonorificName", "POCFirstName", "POCLastName", "POCOrgName", "POCPhone", "POCEmail",
        "ModificationDate", "MOMVersion", "FEDname",
    };

    /// <summary>Clauses a <c>(Class …)</c> may carry besides its attributes.</summary>
    private static readonly string[] ClassClauses =
    {
        "ID", "Name", "PSCapabilities", "Description", "SuperClass", "Attribute",
    };

    /// <summary>Clauses an <c>(Interaction …)</c> may carry besides its parameters.</summary>
    private static readonly string[] InteractionClauses =
    {
        "ID", "Name", "ISRType", "Description", "SuperInteraction",
        "DeliveryCategory", "MessageOrdering", "Parameter",
    };

    /// <summary>Clauses a <c>(ComplexDataType …)</c> may carry besides its components.</summary>
    private static readonly string[] ComplexDataTypeClauses = { "Name", "Description", "ComplexComponent" };

    /// <summary>
    /// Clauses an <c>(EnumeratedDataType …)</c> may carry besides its enumerations.
    /// <c>AutoSequence</c> and <c>StartValue</c> are OMDT additions to the DIF grammar.
    /// </summary>
    private static readonly string[] EnumeratedDataTypeClauses =
    {
        "Name", "Description", "AutoSequence", "StartValue", "Enumeration",
    };

    /// <summary>Characters that separate the numbers of a <c>[38, 39]</c> note reference.</summary>
    private static readonly char[] NoteReferenceSeparators = { '[', ']', ',', ' ' };

    /// <inheritdoc />
    public FomStandard Standard => FomStandard.Hla13;

    /// <summary>
    /// Reads the document. A well-formed file is parsed from the <see cref="SExpressionReader"/>
    /// tree; a file that either upsets the reader or yields no classes is re-read block by block
    /// from the raw text, because a single unterminated string can only be contained that way.
    /// Content problems are reported through <see cref="FomDocument.Diagnostics"/>, never thrown.
    /// </summary>
    /// <param name="reader">Source of the document.</param>
    /// <param name="sourcePath">Path recorded on the document, when it came from a file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    public FomDocument Parse(TextReader reader, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        // The raw text is kept whole: recovery mode needs to re-scan it independently of the reader.
        var text = reader.ReadToEnd() ?? string.Empty;

        var doc = new FomDocument
        {
            Standard = FomStandard.Hla13,
            SourcePath = sourcePath,
            SourceNamespace = null,
        };

        ReportHeaderKind(doc, text);

        var source = SExpressionReader.ParseText(text);
        var objectModel = source.Expressions.FirstOrDefault(e => !e.IsAtom && e.HasHead("ObjectModel"));
        var classCount = objectModel is null ? 0 : objectModel.ChildrenNamed("Class").Count();

        var reason = DescribeRecoveryReason(source, objectModel, classCount);
        if (reason is null)
        {
            ReadObjectModel(doc, objectModel!);
            return doc;
        }

        // The reader's own complaints are useful context for the recovery warning that follows.
        foreach (var problem in source.Problems)
            Add(doc, DiagnosticSeverity.Warning, problem.Message, problem.Line);

        Add(doc, DiagnosticSeverity.Warning,
            $"The document could not be read as a whole ({reason}); " +
            "each top-level block was recovered from the raw text instead.");

        Recover(doc, text);
        return doc;
    }

    /// <summary>
    /// Says why the whole-document parse cannot be trusted, or null when it can. A document is
    /// trusted only when the reader was happy, an <c>(ObjectModel …)</c> was found, and that element
    /// yielded at least one class — the combination a broken quote reliably destroys.
    /// </summary>
    private static string? DescribeRecoveryReason(SExpressionDocument source, SExpression? objectModel, int classCount)
    {
        if (source.Problems.Count > 0)
            return $"the reader reported {source.Problems.Count} tokenising problem(s), the first at {source.Problems[0]}";

        if (objectModel is null)
            return "no '(ObjectModel …)' element was found";

        if (classCount == 0)
            return "the '(ObjectModel …)' element yielded no '(Class …)' entries";

        return null;
    }

    /// <summary>Records which of the two known header lines opens the file.</summary>
    private static void ReportHeaderKind(FomDocument doc, string text)
    {
        // A byte-order mark survives decoding and is not whitespace to the regex engine.
        var body = text.TrimStart('\uFEFF');

        if (DifHeaderRegex.IsMatch(body))
        {
            Add(doc, DiagnosticSeverity.Info, "Read as an HLA 1.3 OMT document with a 'DIF HLA-OMT' header.", 1);
            return;
        }

        if (OmdtHeaderRegex.IsMatch(body))
        {
            Add(doc, DiagnosticSeverity.Info, "Read as an HLA 1.3 OMT document with an 'OMDT' header.", 1);
            return;
        }

        Add(doc, DiagnosticSeverity.Warning,
            "The file opens with neither a '(DIF HLA-OMT …)' nor an '(OMDT …)' header; " +
            "read as an HLA 1.3 OMT document anyway.", 1);
    }

    // ----------------------------------------------------------- whole-document path

    /// <summary>Reads a trustworthy <c>(ObjectModel …)</c> element in document order.</summary>
    private static void ReadObjectModel(FomDocument doc, SExpression objectModel)
    {
        var header = new HeaderFields();
        var classes = new List<PendingClass>();
        var interactions = new List<PendingInteraction>();

        foreach (var element in objectModel.Children)
            ReadElement(doc, element, lineOffset: 0, classes, interactions, header);

        header.ApplyTo(doc);
        AssembleObjectClasses(doc, classes);
        AssembleInteractionClasses(doc, interactions);
    }

    /// <summary>
    /// Reads one child of <c>(ObjectModel …)</c>. <paramref name="header"/> is null in recovery mode,
    /// where the identification clauses are picked up from the raw text instead.
    /// </summary>
    /// <param name="lineOffset">
    /// Added to every line number reported by <paramref name="element"/>. Zero for the whole-document
    /// path; in recovery mode it maps a block-relative line back onto the line of the file.
    /// </param>
    private static void ReadElement(
        FomDocument doc,
        SExpression element,
        int lineOffset,
        List<PendingClass> classes,
        List<PendingInteraction> interactions,
        HeaderFields? header)
    {
        if (element.HasHead("Class"))
        {
            classes.Add(ReadClass(doc, element, lineOffset));
        }
        else if (element.HasHead("Interaction"))
        {
            interactions.Add(ReadInteraction(doc, element, lineOffset));
        }
        else if (element.HasHead("ComplexDataType"))
        {
            ReadComplexDataType(doc, element, lineOffset);
        }
        else if (element.HasHead("EnumeratedDataType"))
        {
            ReadEnumeratedDataType(doc, element, lineOffset);
        }
        else if (element.HasHead("Note"))
        {
            ReadNote(doc, element, lineOffset);
        }
        else if (element.HasHead("Space") || element.HasHead("RoutingSpace"))
        {
            // Not emitted by any 1.3 OMT tool seen in practice, but the table exists in the standard.
            ReadSpace(doc, element, lineOffset);
        }
        else if (header is not null && element.Head is not null && header.TryTake(element))
        {
            // An identification clause; HeaderFields kept the value.
        }
        else
        {
            ReportUnrecognised(doc, element, lineOffset, "ObjectModel");
        }
    }

    // ------------------------------------------------------------------- recovery

    /// <summary>
    /// Re-reads <paramref name="text"/> one block at a time. Every <c>(Class …)</c>,
    /// <c>(Interaction …)</c>, <c>(ComplexDataType …)</c>, <c>(EnumeratedDataType …)</c> and
    /// <c>(Note …)</c> block is individually well formed even in a file whose header is broken,
    /// so parsing each in isolation salvages the whole body.
    /// </summary>
    private static void Recover(FomDocument doc, string text)
    {
        ReportUnterminatedString(doc, text);
        RecoverHeader(doc, text);

        var classes = new List<PendingClass>();
        var interactions = new List<PendingInteraction>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var recovered = 0;

        // Blocks are found in increasing order, so the line cursor only ever moves forwards.
        var cursorIndex = 0;
        var cursorLine = 1;
        var search = 0;

        while (TryFindNextBlock(text, search, out var keyword, out var start, out var end))
        {
            search = end;

            cursorLine += CountLineBreaks(text, cursorIndex, start);
            cursorIndex = start;

            var block = SExpressionReader.ParseText(text[start..end])
                .Expressions.FirstOrDefault(e => !e.IsAtom);
            if (block is null)
                continue;

            recovered++;
            counts[keyword] = counts.TryGetValue(keyword, out var seen) ? seen + 1 : 1;

            // The block's own lines are 1-based; the offset lifts them back onto the file.
            ReadElement(doc, block, cursorLine - 1, classes, interactions, header: null);
        }

        AssembleObjectClasses(doc, classes);
        AssembleInteractionClasses(doc, interactions);

        if (recovered == 0)
        {
            Add(doc, DiagnosticSeverity.Error,
                "No '(Class …)', '(Interaction …)', '(ComplexDataType …)', '(EnumeratedDataType …)' " +
                "or '(Note …)' block could be recovered from the file.");
            return;
        }

        var breakdown = string.Join(", ", BlockKeywords
            .Where(counts.ContainsKey)
            .Select(k => $"{counts[k]} {k}"));

        Add(doc, DiagnosticSeverity.Info, $"Recovered {recovered} block(s) from the raw text: {breakdown}.");
    }

    /// <summary>
    /// Picks the identification clauses out of the raw header region, tolerating a value whose
    /// closing quote is missing.
    /// </summary>
    private static void RecoverHeader(FomDocument doc, string text)
    {
        // The header is everything before the first block; searching further would find the
        // '(Name …)' of the first class instead of the model's own name.
        var limit = text.Length;
        if (TryFindNextBlock(text, 0, out _, out var firstBlock, out _))
            limit = firstBlock;

        var header = new HeaderFields();
        foreach (var keyword in HeaderKeywords)
        {
            if (TryExtractClauseValue(text, limit, keyword, out var value))
                header.Set(keyword, value);
        }

        header.ApplyTo(doc);
    }

    /// <summary>
    /// Reports the first quoted value that never closes, naming the clause it belongs to. That is
    /// the construct which breaks these files, so saying so is worth a warning of its own.
    /// </summary>
    private static void ReportUnterminatedString(FomDocument doc, string text)
    {
        var line = 1;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\r' || c == '\n')
            {
                i = SkipLineBreak(text, i);
                line++;
                continue;
            }

            if (c != '"')
            {
                i++;
                continue;
            }

            var openedAt = i;
            var openedOn = line;
            i++;

            // Walk the value. A value may legitimately span lines, but a line that starts a new
            // '(' clause means the closing quote was forgotten — that is the recovery assumption.
            var terminated = false;
            while (i < text.Length)
            {
                if (text[i] == '"')
                {
                    i++;
                    terminated = true;
                    break;
                }

                if (text[i] == '\r' || text[i] == '\n')
                {
                    var next = SkipLineBreak(text, i);
                    var peek = next;
                    while (peek < text.Length && (text[peek] == ' ' || text[peek] == '\t'))
                        peek++;

                    if (peek >= text.Length || text[peek] == '(')
                        break;

                    i = next;
                    line++;
                    continue;
                }

                i++;
            }

            if (terminated)
                continue;

            Add(doc, DiagnosticSeverity.Warning,
                $"The value of '({FindEnclosingClause(text, openedAt)} …)' is missing its closing quote; " +
                "the value was assumed to end at the line break.", openedOn);
            return;
        }
    }

    /// <summary>Name of the clause whose opening parenthesis most recently precedes <paramref name="index"/>.</summary>
    private static string FindEnclosingClause(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (text[i] != '(')
                continue;

            var word = ReadIdentifier(text, i + 1);
            if (word.Length > 0)
                return word;
        }

        return "?";
    }

    /// <summary>
    /// Finds the next balanced <c>(Keyword …)</c> block of interest at or after <paramref name="from"/>.
    /// </summary>
    /// <remarks>
    /// Quoted strings are skipped so that a description containing parentheses cannot upset the
    /// balance, and a string that reaches the end of its line without closing is treated as ending
    /// there. That single assumption is what keeps the damage of a missing closing quote to one line
    /// instead of to the whole file.
    /// </remarks>
    private static bool TryFindNextBlock(string text, int from, out string keyword, out int start, out int end)
    {
        keyword = "";
        start = -1;
        end = -1;

        var i = Math.Max(0, from);
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '"')
            {
                i = SkipQuoted(text, i);
                continue;
            }

            if (c != '(')
            {
                i++;
                continue;
            }

            var word = ReadIdentifier(text, i + 1);
            if (word.Length > 0 && BlockKeywords.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                var close = FindBlockEnd(text, i);
                if (close > i)
                {
                    keyword = BlockKeywords.First(k => string.Equals(k, word, StringComparison.OrdinalIgnoreCase));
                    start = i;
                    end = close;
                    return true;
                }
            }

            i++;
        }

        return false;
    }

    /// <summary>Index just past the parenthesis that closes the block opening at <paramref name="start"/>, or -1.</summary>
    private static int FindBlockEnd(string text, int start)
    {
        var depth = 0;
        var i = start;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '"')
            {
                i = SkipQuoted(text, i);
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                    return i + 1;
            }

            i++;
        }

        return -1;
    }

    /// <summary>
    /// Reads one identification clause out of the raw text, stopping before
    /// <paramref name="limit"/>. Returns false when the clause is absent or carries no value.
    /// </summary>
    private static bool TryExtractClauseValue(string text, int limit, string keyword, out string value)
    {
        value = "";

        var start = FindClause(text, limit, keyword);
        if (start < 0)
            return false;

        var i = start;
        while (i < limit && char.IsWhiteSpace(text[i]))
            i++;

        if (i >= limit)
            return false;

        if (text[i] != '"')
        {
            // An unquoted atom such as '(Type FOM)' or '(ModificationDate 9/28/2015)'.
            var begin = i;
            while (i < limit && !IsAtomDelimiter(text[i]))
                i++;
            value = text[begin..i];
            return value.Length != 0;
        }

        i++;
        var builder = new StringBuilder();
        var terminated = false;

        while (i < limit)
        {
            var c = text[i];

            if (c == '"')
            {
                terminated = true;
                break;
            }

            if (c == '\r' || c == '\n')
            {
                var next = SkipLineBreak(text, i);
                var peek = next;
                while (peek < limit && (text[peek] == ' ' || text[peek] == '\t'))
                    peek++;

                // A continuation line carries more of the value; a line that opens a new clause
                // means this value's closing quote was forgotten.
                if (peek >= limit || text[peek] == '(')
                    break;

                builder.Append('\n');
                i = next;
                continue;
            }

            builder.Append(c);
            i++;
        }

        value = builder.ToString().TrimEnd();

        // Without its closing quote the clause's own ')' was swallowed into the value; give it back.
        if (!terminated && value.EndsWith(')'))
            value = value[..^1].TrimEnd();

        return value.Length != 0;
    }

    /// <summary>Index just past <c>(keyword</c>, searching only outside quoted strings.</summary>
    private static int FindClause(string text, int limit, string keyword)
    {
        var i = 0;
        while (i < limit)
        {
            var c = text[i];

            if (c == '"')
            {
                i = SkipQuoted(text, i);
                continue;
            }

            if (c != '(')
            {
                i++;
                continue;
            }

            var word = ReadIdentifier(text, i + 1);
            if (string.Equals(word, keyword, StringComparison.OrdinalIgnoreCase))
            {
                var after = i + 1;
                while (after < text.Length && char.IsWhiteSpace(text[after]))
                    after++;
                return after + word.Length;
            }

            i++;
        }

        return -1;
    }

    /// <summary>Reads the identifier at <paramref name="start"/>, skipping any leading whitespace.</summary>
    private static string ReadIdentifier(string text, int start)
    {
        var i = start;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        var begin = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '-'))
            i++;

        return text[begin..i];
    }

    /// <summary>Index just past a quoted value that ends at its closing quote or at the line break.</summary>
    private static int SkipQuoted(string text, int quoteIndex)
    {
        var i = quoteIndex + 1;
        while (i < text.Length && text[i] != '"' && text[i] != '\r' && text[i] != '\n')
            i++;

        return i < text.Length && text[i] == '"' ? i + 1 : i;
    }

    /// <summary>Index just past the line break at <paramref name="index"/>, treating CRLF as one break.</summary>
    private static int SkipLineBreak(string text, int index)
    {
        if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            return index + 2;
        return index + 1;
    }

    /// <summary>Number of line breaks in <c>text[from..to)</c>, counting CRLF once.</summary>
    private static int CountLineBreaks(string text, int from, int to)
    {
        var breaks = 0;
        var i = Math.Max(0, from);
        var stop = Math.Min(to, text.Length);

        while (i < stop)
        {
            if (text[i] == '\r' || text[i] == '\n')
            {
                i = SkipLineBreak(text, i);
                breaks++;
                continue;
            }

            i++;
        }

        return breaks;
    }

    private static bool IsAtomDelimiter(char c) => c is '(' or ')' or '"' || char.IsWhiteSpace(c);

    // ------------------------------------------------------------------ datatypes

    /// <summary>Maps <c>(ComplexDataType …)</c> onto a fixed record, its components onto record fields.</summary>
    private static void ReadComplexDataType(FomDocument doc, SExpression element, int lineOffset)
    {
        var name = ChildValue(element, "Name");
        if (name is null)
        {
            Add(doc, DiagnosticSeverity.Warning, "'(ComplexDataType …)' declares no name; skipped with its components.",
                element.Line + lineOffset, "ObjectModel/ComplexDataType");
            return;
        }

        var record = new FixedRecordDataType
        {
            Name = name,
            QualifiedName = name,
            Semantics = ChildValue(element, "Description"),
            Notes = CollectNoteReferences(element),
        };

        foreach (var child in element.Children)
        {
            if (!child.HasHead("ComplexComponent"))
            {
                if (!IsKnown(child, ComplexDataTypeClauses))
                    ReportUnrecognised(doc, child, lineOffset, $"ObjectModel/ComplexDataType[{name}]");
                continue;
            }

            var fieldName = ChildValue(child, "FieldName");
            if (fieldName is null)
            {
                Add(doc, DiagnosticSeverity.Warning,
                    $"A '(ComplexComponent …)' of '{name}' declares no field name; skipped.",
                    child.Line + lineOffset, $"ObjectModel/ComplexDataType[{name}]");
                continue;
            }

            record.Fields.Add(new RecordField
            {
                Name = fieldName,
                QualifiedName = $"{name}.{fieldName}",
                DataType = ChildValue(child, "DataType"),
                // RecordField has no home for the 1.3 measurement columns, so they are summarised.
                Semantics = ComposeSemantics(
                    ChildValue(child, "Description"),
                    SummariseClauses(child, "Cardinality", "Units", "Resolution", "Accuracy", "AccuracyCondition")),
                Notes = CollectNoteReferences(child),
            });
        }

        doc.DataTypes.FixedRecordDataTypes.Add(record);
    }

    /// <summary>Maps <c>(EnumeratedDataType …)</c> and its <c>(Enumeration …)</c> children.</summary>
    private static void ReadEnumeratedDataType(FomDocument doc, SExpression element, int lineOffset)
    {
        var name = ChildValue(element, "Name");
        if (name is null)
        {
            Add(doc, DiagnosticSeverity.Warning, "'(EnumeratedDataType …)' declares no name; skipped with its enumerators.",
                element.Line + lineOffset, "ObjectModel/EnumeratedDataType");
            return;
        }

        var enumerated = new EnumeratedDataType
        {
            Name = name,
            QualifiedName = name,
            // HLA 1.3 has no representation column; the width lives in the type's name (…Enum16).
            Representation = null,
            Semantics = ComposeSemantics(
                ChildValue(element, "Description"),
                SummariseClauses(element, "AutoSequence", "StartValue")),
            Notes = CollectNoteReferences(element),
        };

        foreach (var child in element.Children)
        {
            if (!child.HasHead("Enumeration"))
            {
                if (!IsKnown(child, EnumeratedDataTypeClauses))
                    ReportUnrecognised(doc, child, lineOffset, $"ObjectModel/EnumeratedDataType[{name}]");
                continue;
            }

            var enumerator = ChildValue(child, "Enumerator");
            if (enumerator is null)
            {
                Add(doc, DiagnosticSeverity.Warning,
                    $"An '(Enumeration …)' of '{name}' declares no enumerator; skipped.",
                    child.Line + lineOffset, $"ObjectModel/EnumeratedDataType[{name}]");
                continue;
            }

            enumerated.Enumerators.Add(new EnumeratorValue
            {
                Name = enumerator,
                QualifiedName = $"{name}.{enumerator}",
                Values = ChildValue(child, "Representation"),
                Notes = CollectNoteReferences(child),
            });
        }

        doc.DataTypes.EnumeratedDataTypes.Add(enumerated);
    }

    // -------------------------------------------------------------------- classes

    /// <summary>Reads one <c>(Class …)</c> block; the tree is assembled afterwards from the IDs.</summary>
    private static PendingClass ReadClass(FomDocument doc, SExpression element, int lineOffset)
    {
        var line = element.Line + lineOffset;
        var name = ChildValue(element, "Name");
        var id = ChildInteger(element, "ID");

        if (name is null)
        {
            name = id is { } number ? $"Class{number}" : "Class";
            Add(doc, DiagnosticSeverity.Warning,
                $"'(Class …)' declares no name; it is reported as '{name}'.", line, "ObjectModel/Class");
        }

        var objectClass = new FomObjectClass
        {
            Name = name,
            QualifiedName = name,
            Sharing = ExpandPublishSubscribe(ChildValue(element, "PSCapabilities")),
            Semantics = ChildValue(element, "Description"),
            Notes = CollectNoteReferences(element),
        };

        var path = $"ObjectModel/Class[{name}]";

        foreach (var child in element.Children)
        {
            if (child.HasHead("Attribute"))
            {
                var attribute = ReadAttribute(doc, child, name, path, lineOffset);
                if (attribute is not null)
                    objectClass.Attributes.Add(attribute);
            }
            else if (!IsKnown(child, ClassClauses))
            {
                ReportUnrecognised(doc, child, lineOffset, path);
            }
        }

        return new PendingClass(objectClass, id, ChildInteger(element, "SuperClass"), line);
    }

    /// <summary>Reads one <c>(Attribute …)</c> clause of a class.</summary>
    private static FomAttribute? ReadAttribute(FomDocument doc, SExpression element, string className, string path, int lineOffset)
    {
        var name = ChildValue(element, "Name");
        if (name is null)
        {
            Add(doc, DiagnosticSeverity.Warning,
                $"An '(Attribute …)' of class '{className}' declares no name; skipped.",
                element.Line + lineOffset, path);
            return null;
        }

        return new FomAttribute
        {
            Name = name,
            // QualifiedName is filled in once the class tree is assembled and the owner's path is known.
            QualifiedName = name,
            DataType = ChildValue(element, "DataType"),
            Cardinality = ChildValue(element, "Cardinality"),
            Units = ChildValue(element, "Units"),
            Resolution = ChildValue(element, "Resolution"),
            Accuracy = ChildValue(element, "Accuracy"),
            AccuracyCondition = ChildValue(element, "AccuracyCondition"),
            UpdateType = ChildValue(element, "UpdateType"),
            UpdateCondition = ChildValue(element, "UpdateCondition"),
            Ownership = ExpandTransferAccept(ChildValue(element, "TransferAccept")),
            // UpdateReflect is the 1.3 spelling of an attribute's publish/subscribe capability.
            Sharing = ExpandUpdateReflect(ChildValue(element, "UpdateReflect")),
            Transportation = ChildValue(element, "DeliveryCategory"),
            Order = ChildValue(element, "MessageOrdering"),
            Semantics = ChildValue(element, "Description"),
            Notes = CollectNoteReferences(element),
        };
    }

    // --------------------------------------------------------------- interactions

    /// <summary>Reads one <c>(Interaction …)</c> block; the tree is assembled afterwards from the IDs.</summary>
    private static PendingInteraction ReadInteraction(FomDocument doc, SExpression element, int lineOffset)
    {
        var line = element.Line + lineOffset;
        var name = ChildValue(element, "Name");
        var id = ChildInteger(element, "ID");

        if (name is null)
        {
            name = id is { } number ? $"Interaction{number}" : "Interaction";
            Add(doc, DiagnosticSeverity.Warning,
                $"'(Interaction …)' declares no name; it is reported as '{name}'.", line, "ObjectModel/Interaction");
        }

        var interaction = new FomInteractionClass
        {
            Name = name,
            QualifiedName = name,
            Sharing = ExpandInitiateSense(ChildValue(element, "ISRType")),
            Transportation = ChildValue(element, "DeliveryCategory"),
            Order = ChildValue(element, "MessageOrdering"),
            Semantics = ChildValue(element, "Description"),
            Notes = CollectNoteReferences(element),
        };

        var path = $"ObjectModel/Interaction[{name}]";

        foreach (var child in element.Children)
        {
            if (child.HasHead("Parameter"))
            {
                var parameter = ReadParameter(doc, child, name, path, lineOffset);
                if (parameter is not null)
                    interaction.Parameters.Add(parameter);
            }
            else if (!IsKnown(child, InteractionClauses))
            {
                ReportUnrecognised(doc, child, lineOffset, path);
            }
        }

        return new PendingInteraction(interaction, id, ChildInteger(element, "SuperInteraction"), line);
    }

    /// <summary>Reads one <c>(Parameter …)</c> clause of an interaction.</summary>
    private static FomParameter? ReadParameter(FomDocument doc, SExpression element, string interactionName, string path, int lineOffset)
    {
        var name = ChildValue(element, "Name");
        if (name is null)
        {
            Add(doc, DiagnosticSeverity.Warning,
                $"A '(Parameter …)' of interaction '{interactionName}' declares no name; skipped.",
                element.Line + lineOffset, path);
            return null;
        }

        return new FomParameter
        {
            Name = name,
            // QualifiedName is filled in once the interaction tree is assembled.
            QualifiedName = name,
            DataType = ChildValue(element, "DataType"),
            Cardinality = ChildValue(element, "Cardinality"),
            Units = ChildValue(element, "Units"),
            Resolution = ChildValue(element, "Resolution"),
            Accuracy = ChildValue(element, "Accuracy"),
            AccuracyCondition = ChildValue(element, "AccuracyCondition"),
            Semantics = ChildValue(element, "Description"),
            Notes = CollectNoteReferences(element),
        };
    }

    // ---------------------------------------------------------------------- notes

    /// <summary>Maps <c>(Note (NoteNumber n) (NoteText "…"))</c> onto a document note.</summary>
    private static void ReadNote(FomDocument doc, SExpression element, int lineOffset)
    {
        var number = ChildValue(element, "NoteNumber");
        // NoteText carries "\r\n" as two literal characters; it is kept exactly as written so that
        // the text round-trips against the source document.
        var text = ChildValue(element, "NoteText");

        if (number is null && text is null)
        {
            Add(doc, DiagnosticSeverity.Warning, "'(Note …)' carries neither a number nor any text; skipped.",
                element.Line + lineOffset, "ObjectModel/Note");
            return;
        }

        var label = number ?? "";
        doc.Notes.Add(new FomNote
        {
            Name = label,
            QualifiedName = label,
            Label = number,
            Text = text,
        });
    }

    // --------------------------------------------------------------- routing spaces

    /// <summary>
    /// Reads a routing space, should a document carry one. No 1.3 OMT file seen in practice does,
    /// so this exists only so that one would not be lost.
    /// </summary>
    private static void ReadSpace(FomDocument doc, SExpression element, int lineOffset)
    {
        var name = ChildValue(element, "Name") ?? element.Atom(0);
        if (string.IsNullOrEmpty(name))
        {
            Add(doc, DiagnosticSeverity.Warning, "'(Space …)' declares no name; skipped.",
                element.Line + lineOffset, "ObjectModel/Space");
            return;
        }

        var space = new FomRoutingSpace { Name = name, QualifiedName = name };

        foreach (var child in element.ChildrenNamed("Dimension"))
        {
            var dimension = ChildValue(child, "Name") ?? child.Atom(0);
            if (!string.IsNullOrEmpty(dimension))
                space.Dimensions.Add(dimension);
        }

        doc.RoutingSpaces.Add(space);
    }

    // ------------------------------------------------------------- tree assembly

    /// <summary>Turns the flat class list into a tree and stamps the dotted qualified names on it.</summary>
    private static void AssembleObjectClasses(FomDocument doc, List<PendingClass> pending)
    {
        if (pending.Count == 0)
            return;

        var parents = ResolveParents(
            doc,
            pending.Select(p => p.Id).ToList(),
            pending.Select(p => p.SuperId).ToList(),
            pending.Select(p => p.Node.Name).ToList(),
            pending.Select(p => p.Line).ToList(),
            "Class",
            "SuperClass",
            "ObjectModel/Class");

        for (var i = 0; i < pending.Count; i++)
        {
            var node = pending[i].Node;
            var parent = parents[i];

            if (parent < 0)
            {
                doc.ObjectClasses.Add(node);
                continue;
            }

            node.Parent = pending[parent].Node;
            pending[parent].Node.Children.Add(node);
        }

        foreach (var root in doc.ObjectClasses)
            NameObjectClass(doc, root, parentQualifiedName: null, depth: 1);
    }

    /// <summary>Turns the flat interaction list into a tree and stamps the dotted qualified names on it.</summary>
    private static void AssembleInteractionClasses(FomDocument doc, List<PendingInteraction> pending)
    {
        if (pending.Count == 0)
            return;

        var parents = ResolveParents(
            doc,
            pending.Select(p => p.Id).ToList(),
            pending.Select(p => p.SuperId).ToList(),
            pending.Select(p => p.Node.Name).ToList(),
            pending.Select(p => p.Line).ToList(),
            "Interaction",
            "SuperInteraction",
            "ObjectModel/Interaction");

        for (var i = 0; i < pending.Count; i++)
        {
            var node = pending[i].Node;
            var parent = parents[i];

            if (parent < 0)
            {
                doc.InteractionClasses.Add(node);
                continue;
            }

            node.Parent = pending[parent].Node;
            pending[parent].Node.Children.Add(node);
        }

        foreach (var root in doc.InteractionClasses)
            NameInteractionClass(doc, root, parentQualifiedName: null, depth: 1);
    }

    /// <summary>
    /// Resolves the <c>SuperClass</c> / <c>SuperInteraction</c> pointers into parent indices,
    /// where -1 means "root". Anything that points at itself, at an ID that does not exist, or
    /// around a cycle is rooted instead, with a warning.
    /// </summary>
    private static int[] ResolveParents(
        FomDocument doc,
        IReadOnlyList<int?> ids,
        IReadOnlyList<int?> superIds,
        IReadOnlyList<string> names,
        IReadOnlyList<int> lines,
        string kind,
        string superClause,
        string path)
    {
        var count = ids.Count;
        var parents = new int[count];
        var byId = new Dictionary<int, int>();

        for (var i = 0; i < count; i++)
        {
            if (ids[i] is not { } id)
            {
                Add(doc, DiagnosticSeverity.Warning,
                    $"{kind} '{names[i]}' declares no '(ID …)'; nothing can inherit from it.", lines[i], path);
                continue;
            }

            if (byId.TryGetValue(id, out var first))
            {
                Add(doc, DiagnosticSeverity.Warning,
                    $"{kind} '{names[i]}' repeats ID {id}, already used by '{names[first]}'; " +
                    $"'({superClause} {id})' resolves to the first of the two.", lines[i], path);
                continue;
            }

            byId[id] = i;
        }

        for (var i = 0; i < count; i++)
        {
            parents[i] = -1;

            if (superIds[i] is not { } superId)
                continue;   // no pointer at all: a root of the tree.

            if (ids[i] is { } own && own == superId)
            {
                Add(doc, DiagnosticSeverity.Warning,
                    $"{kind} '{names[i]}' names itself as its own '({superClause} {superId})'; treated as a root.",
                    lines[i], path);
                continue;
            }

            if (!byId.TryGetValue(superId, out var parent))
            {
                Add(doc, DiagnosticSeverity.Warning,
                    $"{kind} '{names[i]}' points at '({superClause} {superId})', which no {kind.ToLowerInvariant()} declares; " +
                    "treated as a root.", lines[i], path);
                continue;
            }

            parents[i] = parent;
        }

        // Break cycles only after every pointer is known, so a loop of any length is visible.
        for (var i = 0; i < count; i++)
        {
            var steps = 0;
            var walker = parents[i];

            while (walker >= 0)
            {
                if (walker == i || ++steps > count)
                {
                    Add(doc, DiagnosticSeverity.Warning,
                        $"{kind} '{names[i]}' takes part in a '{superClause}' cycle; treated as a root.",
                        lines[i], path);
                    parents[i] = -1;
                    break;
                }

                walker = parents[walker];
            }
        }

        return parents;
    }

    /// <summary>Stamps the dotted qualified name on a class, its attributes and its subtree.</summary>
    private static void NameObjectClass(FomDocument doc, FomObjectClass node, string? parentQualifiedName, int depth)
    {
        node.QualifiedName = parentQualifiedName is null ? node.Name : $"{parentQualifiedName}.{node.Name}";

        foreach (var attribute in node.Attributes)
            attribute.QualifiedName = $"{node.QualifiedName}.{attribute.Name}";

        if (depth >= MaxClassDepth)
        {
            Add(doc, DiagnosticSeverity.Warning,
                $"Object class '{node.QualifiedName}' is nested deeper than {MaxClassDepth} levels; " +
                "its children were not named.", null, "ObjectModel/Class");
            return;
        }

        foreach (var child in node.Children)
            NameObjectClass(doc, child, node.QualifiedName, depth + 1);
    }

    /// <summary>Stamps the dotted qualified name on an interaction, its parameters and its subtree.</summary>
    private static void NameInteractionClass(FomDocument doc, FomInteractionClass node, string? parentQualifiedName, int depth)
    {
        node.QualifiedName = parentQualifiedName is null ? node.Name : $"{parentQualifiedName}.{node.Name}";

        foreach (var parameter in node.Parameters)
            parameter.QualifiedName = $"{node.QualifiedName}.{parameter.Name}";

        if (depth >= MaxClassDepth)
        {
            Add(doc, DiagnosticSeverity.Warning,
                $"Interaction class '{node.QualifiedName}' is nested deeper than {MaxClassDepth} levels; " +
                "its children were not named.", null, "ObjectModel/Interaction");
            return;
        }

        foreach (var child in node.Children)
            NameInteractionClass(doc, child, node.QualifiedName, depth + 1);
    }

    // ------------------------------------------------------------------ vocabulary

    /// <summary>Expands the 1.3 <c>PSCapabilities</c> token into the 1516 sharing vocabulary.</summary>
    private static string? ExpandPublishSubscribe(string? value) => value switch
    {
        null => null,
        "S" => "Subscribe",
        "P" => "Publish",
        "PS" => "PublishSubscribe",
        "N" => "Neither",
        _ => value,
    };

    /// <summary>
    /// Expands the 1.3 <c>ISRType</c> token. <c>IR</c> — initiate and react — has no 1516
    /// equivalent, so it passes through as written, as does anything unrecognised.
    /// </summary>
    private static string? ExpandInitiateSense(string? value) => value switch
    {
        null => null,
        "I" => "Publish",
        "S" => "Subscribe",
        "IS" => "PublishSubscribe",
        "N" => "Neither",
        _ => value,
    };

    /// <summary>Expands the 1.3 <c>TransferAccept</c> token into the 1516 ownership vocabulary.</summary>
    private static string? ExpandTransferAccept(string? value) => value switch
    {
        null => null,
        "N" => "NoTransfer",
        "T" => "Divest",
        "A" => "Acquire",
        "TA" => "DivestAcquire",
        _ => value,
    };

    /// <summary>Expands the 1.3 <c>UpdateReflect</c> token into the 1516 sharing vocabulary.</summary>
    private static string? ExpandUpdateReflect(string? value) => value switch
    {
        null => null,
        "U" => "Publish",
        "R" => "Subscribe",
        "UR" => "PublishSubscribe",
        "N" => "Neither",
        _ => value,
    };

    // ---------------------------------------------------------------------- shared

    /// <summary>The first child list with the given head, or null.</summary>
    private static SExpression? Child(SExpression element, string head) =>
        element.ChildrenNamed(head).FirstOrDefault();

    /// <summary>
    /// The value of a child clause. Only the first atom is the value: a trailing <c>[10]</c> or
    /// <c>[38, 39]</c> is a note reference, and tokenises as one or more atoms of its own.
    /// </summary>
    private static string? ChildValue(SExpression element, string head)
    {
        var clause = Child(element, head);
        return clause is null ? null : NullIfEmpty(clause.Atom(0));
    }

    /// <summary>The value of a child clause read as an integer, or null when absent or not a number.</summary>
    private static int? ChildInteger(SExpression element, string head)
    {
        var value = ChildValue(element, head);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    /// <summary>
    /// The note numbers referenced by an element's own clauses, e.g. <c>"38, 39"</c> for
    /// <c>(UpdateCondition "AccelerationChange" [38, 39])</c>. Nested elements such as
    /// <c>(Attribute …)</c> are skipped, so a class never inherits its attributes' references.
    /// </summary>
    private static string? CollectNoteReferences(SExpression element)
    {
        List<string>? numbers = null;

        foreach (var clause in element.Children)
        {
            if (!IsLeafClause(clause))
                continue;

            for (var i = 1; i < clause.Atoms.Count; i++)
            {
                foreach (var piece in clause.Atoms[i].Split(NoteReferenceSeparators, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!piece.All(char.IsAsciiDigit))
                        continue;

                    numbers ??= new List<string>();
                    if (!numbers.Contains(piece))
                        numbers.Add(piece);
                }
            }
        }

        return numbers is null ? null : string.Join(", ", numbers);
    }

    /// <summary>True when a child is a plain <c>(Key value)</c> clause rather than a nested element.</summary>
    private static bool IsLeafClause(SExpression element) => element.Children.Count == 0;

    /// <summary>True when <paramref name="clause"/> is one of the clauses its parent is allowed to carry.</summary>
    private static bool IsKnown(SExpression clause, string[] known) =>
        clause.Head is not null && known.Contains(clause.Head, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Joins a free-text description with a bracketed <c>key=value</c> summary, for the nodes whose
    /// only free-text slot is <see cref="FomNode.Semantics"/>.
    /// </summary>
    private static string? ComposeSemantics(string? description, string? summary)
    {
        if (description is null)
            return summary;

        return summary is null ? description : $"{description} ({summary})";
    }

    /// <summary>
    /// Joins the named clauses of an element into a compact <c>key=value; key=value</c> summary,
    /// for the columns the normalised model has no field of its own for.
    /// </summary>
    private static string? SummariseClauses(SExpression element, params string[] heads)
    {
        List<string>? parts = null;

        foreach (var head in heads)
        {
            var value = ChildValue(element, head);
            if (value is null)
                continue;

            parts ??= new List<string>();
            parts.Add($"{head}={value}");
        }

        return parts is null ? null : string.Join("; ", parts);
    }

    /// <summary>Reports an element the OMT grammar does not define, without stopping the parse.</summary>
    private static void ReportUnrecognised(FomDocument doc, SExpression element, int lineOffset, string? path)
    {
        var head = element.Head
                   ?? (element.IsAtom ? element.Atom(0) : null)
                   ?? "";

        Add(doc, DiagnosticSeverity.Info, $"Unrecognised OMT element '{head}'", element.Line + lineOffset, path);
    }

    private static void Add(FomDocument doc, DiagnosticSeverity severity, string message, int? line = null, string? path = null) =>
        doc.Diagnostics.Add(new ParseDiagnostic(severity, message, line, path));

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // -------------------------------------------------------------- nested types

    /// <summary>A class read from the flat OMT list, before its <c>SuperClass</c> pointer is resolved.</summary>
    private sealed class PendingClass
    {
        public PendingClass(FomObjectClass node, int? id, int? superId, int line)
        {
            Node = node;
            Id = id;
            SuperId = superId;
            Line = line;
        }

        public FomObjectClass Node { get; }
        public int? Id { get; }
        public int? SuperId { get; }
        public int Line { get; }
    }

    /// <summary>An interaction read from the flat OMT list, before its <c>SuperInteraction</c> pointer is resolved.</summary>
    private sealed class PendingInteraction
    {
        public PendingInteraction(FomInteractionClass node, int? id, int? superId, int line)
        {
            Node = node;
            Id = id;
            SuperId = superId;
            Line = line;
        }

        public FomInteractionClass Node { get; }
        public int? Id { get; }
        public int? SuperId { get; }
        public int Line { get; }
    }

    /// <summary>
    /// The identification clauses of <c>(ObjectModel …)</c>, gathered before they are flattened onto
    /// <see cref="ModelIdentification"/> — the points of contact become a single line, so they have
    /// to be collected first.
    /// </summary>
    private sealed class HeaderFields
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Takes the value of <paramref name="clause"/> when it is an identification clause.</summary>
        /// <returns>True when the clause was recognised and consumed.</returns>
        public bool TryTake(SExpression clause)
        {
            var head = clause.Head;
            if (head is null || !HeaderKeywords.Contains(head, StringComparer.OrdinalIgnoreCase))
                return false;

            var value = NullIfEmpty(clause.Atom(0));
            if (value is not null)
                _values[head] = value;

            return true;
        }

        /// <summary>Records a value found by the raw-text scan of recovery mode.</summary>
        public void Set(string keyword, string value)
        {
            if (!string.IsNullOrEmpty(value))
                _values[keyword] = value;
        }

        /// <summary>Writes the gathered values onto the document's identification block.</summary>
        public void ApplyTo(FomDocument doc)
        {
            var identification = doc.Identification;

            identification.Name = Get("Name") ?? identification.Name;
            identification.Version = Get("VersionNumber") ?? identification.Version;
            identification.Type = Get("Type") ?? identification.Type;
            identification.Purpose = Get("Purpose") ?? identification.Purpose;
            identification.ApplicationDomain = Get("ApplicationDomain") ?? identification.ApplicationDomain;
            identification.ModificationDate = Get("ModificationDate") ?? identification.ModificationDate;
            identification.Other = Get("SponsorOrgName") ?? identification.Other;

            // HLA 1.3 has no description field of its own, so the purpose doubles as one.
            identification.Description ??= identification.Purpose;

            var contact = FlattenPointOfContact();
            if (contact is not null && !identification.PointsOfContact.Contains(contact))
                identification.PointsOfContact.Add(contact);

            // FEDname names the FED the model was generated for; the normalised model has no slot
            // for it, so it is reported rather than silently dropped.
            var fedName = Get("FEDname");
            if (fedName is not null)
                Add(doc, DiagnosticSeverity.Info, $"The object model names its federation execution data file as '{fedName}'.");
        }

        /// <summary>Builds the single "First Last, Org, Phone, Email" line, omitting blank parts.</summary>
        private string? FlattenPointOfContact()
        {
            var name = string.Join(' ', new[] { Get("POCFirstName"), Get("POCLastName") }
                .Where(p => !string.IsNullOrWhiteSpace(p)));

            var parts = new[] { name, Get("POCOrgName"), Get("POCPhone"), Get("POCEmail") }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!.Trim())
                .ToArray();

            return parts.Length == 0 ? null : string.Join(", ", parts);
        }

        private string? Get(string keyword) =>
            _values.TryGetValue(keyword, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }
}
