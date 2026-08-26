using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HLAFomReader.Core.Parsing;

/// <summary>
/// One node of a parenthesised FED expression.
/// </summary>
/// <remarks>
/// A list such as <c>(class Aircraft (attribute Fuel reliable timestamp))</c> becomes a node with
/// <see cref="Head"/> = <c>class</c>, <see cref="Atoms"/> = <c>[Aircraft]</c> and one entry in
/// <see cref="Children"/>. The reader deliberately splits the members of a list into "atoms"
/// (bare or quoted tokens) and "children" (nested lists); the relative order between the two
/// groups is not preserved, because no FED construct depends on it.
/// </remarks>
public sealed class SExpression
{
    private readonly List<string> _atoms = new();
    private readonly List<SExpression> _children = new();

    internal SExpression(int line) => Line = line;

    /// <summary>1-based line of the opening parenthesis, or of the token for a bare atom.</summary>
    public int Line { get; }

    /// <summary>
    /// The first token of the list, i.e. the element keyword. Null for a bare atom that sits
    /// outside any list, for the empty list <c>()</c>, and for a list that opens with a nested list.
    /// </summary>
    public string? Head { get; private set; }

    /// <summary>True when this node is a bare token that appeared outside any list.</summary>
    /// <remarks>Such a node carries its text as the single entry of <see cref="Atoms"/>.</remarks>
    public bool IsAtom { get; private set; }

    /// <summary>The non-list tokens that follow <see cref="Head"/>, in source order and unquoted.</summary>
    public IReadOnlyList<string> Atoms => _atoms;

    /// <summary>The nested lists of this node, in source order.</summary>
    public IReadOnlyList<SExpression> Children => _children;

    /// <summary>The child lists whose head matches <paramref name="head"/>, compared ordinal-ignore-case.</summary>
    public IEnumerable<SExpression> ChildrenNamed(string head)
    {
        if (string.IsNullOrEmpty(head))
            yield break;

        foreach (var child in _children)
        {
            if (child.Head is not null && string.Equals(child.Head, head, StringComparison.OrdinalIgnoreCase))
                yield return child;
        }
    }

    /// <summary>True when this node's head matches <paramref name="head"/>, compared ordinal-ignore-case.</summary>
    public bool HasHead(string head) =>
        Head is not null && string.Equals(Head, head, StringComparison.OrdinalIgnoreCase);

    /// <summary>The atom at <paramref name="index"/>, or null when the list is shorter than that.</summary>
    public string? Atom(int index) => index >= 0 && index < _atoms.Count ? _atoms[index] : null;

    /// <summary>Appends a token, promoting the very first one of a list to <see cref="Head"/>.</summary>
    internal void AddToken(string token)
    {
        if (Head is null && _atoms.Count == 0 && _children.Count == 0)
            Head = token;
        else
            _atoms.Add(token);
    }

    /// <summary>Turns this node into a bare atom carrying <paramref name="token"/>.</summary>
    internal void MarkAsAtom(string token)
    {
        IsAtom = true;
        _atoms.Add(token);
    }

    internal void AddChild(SExpression child) => _children.Add(child);

    public override string ToString()
    {
        if (IsAtom)
            return _atoms.Count > 0 ? _atoms[0] : "";
        return $"({Head ?? ""} atoms={_atoms.Count} children={_children.Count}) @{Line}";
    }
}

/// <summary>A problem found while tokenising, reported instead of thrown.</summary>
/// <param name="Line">1-based line the problem was noticed on.</param>
/// <param name="Message">Human-readable description.</param>
public readonly record struct SExpressionProblem(int Line, string Message)
{
    public override string ToString() => $"line {Line}: {Message}";
}

/// <summary>The outcome of reading one parenthesised document: what was understood, and what was not.</summary>
public sealed class SExpressionDocument
{
    internal SExpressionDocument(IReadOnlyList<SExpression> expressions, IReadOnlyList<SExpressionProblem> problems)
    {
        Expressions = expressions;
        Problems = problems;
    }

    /// <summary>Top-level expressions, in source order. A well-formed FED file has exactly one.</summary>
    public IReadOnlyList<SExpression> Expressions { get; }

    /// <summary>Tokenising problems (stray or missing parentheses, unterminated strings).</summary>
    public IReadOnlyList<SExpressionProblem> Problems { get; }
}

/// <summary>
/// A small, forgiving reader for the parenthesised syntax used by HLA 1.3 <c>.fed</c> files.
/// </summary>
/// <remarks>
/// <para>
/// Tokens are bare atoms delimited by whitespace, parentheses, <c>;</c> and <c>"</c>, or
/// double-quoted strings which may contain spaces and parentheses and are unquoted by the reader.
/// A <c>;</c> outside a quoted string starts a comment that runs to the end of the line —
/// real FED files use <c>;;</c>, but a single <c>;</c> is accepted too.
/// </para>
/// <para>
/// The reader never throws on malformed content: unbalanced or missing parentheses and
/// unterminated strings are reported through <see cref="SExpressionDocument.Problems"/> while
/// everything that could be understood is still returned.
/// </para>
/// </remarks>
public static class SExpressionReader
{
    /// <summary>Reads <paramref name="reader"/> to the end and tokenises it.</summary>
    /// <param name="reader">Source of the document. Only a null value is an exception.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    public static SExpressionDocument Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return ParseText(reader.ReadToEnd() ?? string.Empty);
    }

    /// <summary>Tokenises text that is already in memory.</summary>
    public static SExpressionDocument ParseText(string? text)
    {
        var source = text ?? string.Empty;
        var top = new List<SExpression>();
        var problems = new List<SExpressionProblem>();
        var open = new Stack<SExpression>();

        var line = 1;
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            // Line breaks: LF, CRLF and lone CR all advance the line counter exactly once.
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            if (c == '\r')
            {
                i++;
                if (i < source.Length && source[i] == '\n')
                    i++;
                line++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == ';')
            {
                while (i < source.Length && source[i] != '\n' && source[i] != '\r')
                    i++;
                continue;
            }

            if (c == '(')
            {
                open.Push(new SExpression(line));
                i++;
                continue;
            }

            if (c == ')')
            {
                i++;
                if (open.Count == 0)
                {
                    problems.Add(new SExpressionProblem(line, "Unmatched ')' with no open list; ignored."));
                    continue;
                }

                var closed = open.Pop();
                if (open.Count == 0)
                    top.Add(closed);
                else
                    open.Peek().AddChild(closed);
                continue;
            }

            if (c == '"')
            {
                var startLine = line;
                i++;
                var value = new StringBuilder();
                var terminated = false;

                while (i < source.Length)
                {
                    var q = source[i];
                    if (q == '"')
                    {
                        i++;
                        terminated = true;
                        break;
                    }

                    if (q == '\n')
                    {
                        line++;
                        value.Append('\n');
                        i++;
                        continue;
                    }

                    if (q == '\r')
                    {
                        i++;
                        if (i < source.Length && source[i] == '\n')
                            i++;
                        line++;
                        value.Append('\n');
                        continue;
                    }

                    value.Append(q);
                    i++;
                }

                if (!terminated)
                    problems.Add(new SExpressionProblem(startLine, "Unterminated quoted string; read to end of file."));

                Emit(open, top, value.ToString(), startLine);
                continue;
            }

            // Bare atom.
            var start = i;
            while (i < source.Length && !IsDelimiter(source[i]))
                i++;
            Emit(open, top, source[start..i], line);
        }

        // Premature end of file: close every list that is still open, keeping its content.
        while (open.Count > 0)
        {
            var unclosed = open.Pop();
            problems.Add(new SExpressionProblem(
                unclosed.Line,
                $"List '({unclosed.Head ?? ""}' opened here was never closed; closed at end of file."));

            if (open.Count == 0)
                top.Add(unclosed);
            else
                open.Peek().AddChild(unclosed);
        }

        return new SExpressionDocument(top, problems);
    }

    /// <summary>Adds a completed token to the innermost open list, or as a bare atom at top level.</summary>
    private static void Emit(Stack<SExpression> open, List<SExpression> top, string token, int line)
    {
        if (open.Count > 0)
        {
            open.Peek().AddToken(token);
            return;
        }

        var atom = new SExpression(line);
        atom.MarkAsAtom(token);
        top.Add(atom);
    }

    /// <summary>Characters that end a bare atom.</summary>
    private static bool IsDelimiter(char c) =>
        c is '(' or ')' or ';' or '"' || char.IsWhiteSpace(c);
}
