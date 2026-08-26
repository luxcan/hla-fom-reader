using System;
using System.Collections.Generic;
using System.Globalization;
using HLAFomReader.Core.Registry;

namespace HLAFomReader.Core.Comparison;

/// <summary>
/// Aligns two <see cref="TableSnapshot"/> readings of the same registry table, row by row and column
/// by column, so the table browser can show one grid instead of two.
/// </summary>
/// <remarks>
/// Alignment is by <see cref="TableRow.Key"/> through a dictionary, so a FOM with several thousand
/// attributes still compares in linear time.
/// </remarks>
public static class TableComparer
{
    /// <summary>
    /// Lines up <paramref name="left"/> against <paramref name="right"/>.
    /// </summary>
    /// <param name="left">Rows read from FOM A.</param>
    /// <param name="right">Rows read from FOM B.</param>
    /// <param name="ignoreCase">
    /// When true both keys and cell values are matched ordinal-ignore-case, mirroring the
    /// case-insensitive option on the main comparison.
    /// </param>
    /// <returns>
    /// Every left row in its original order — matched or <see cref="RowState.Removed"/> — followed by
    /// the right-only rows in theirs, as <see cref="RowState.Added"/>.
    /// </returns>
    public static TableComparison Compare(TableSnapshot left, TableSnapshot right, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var columns = UnionColumns(left.Columns, right.Columns);

        // Column names come from SQL aliases we author ourselves, so they always match ordinally —
        // the ignore-case option is about the data, not the schema.
        var leftColumnIndex = BuildColumnIndex(left.Columns);
        var rightColumnIndex = BuildColumnIndex(right.Columns);

        var leftKeys = new string[left.Rows.Count];
        var rightKeys = new string[right.Rows.Count];
        AssignKeys(left.Rows, comparer, leftKeys, out _);
        AssignKeys(right.Rows, comparer, rightKeys, out var rightByKey);

        var matchedRight = new bool[right.Rows.Count];
        var pairs = new List<RowPair>(left.Rows.Count + right.Rows.Count);

        for (var i = 0; i < left.Rows.Count; i++)
        {
            var key = leftKeys[i];
            if (rightByKey.TryGetValue(key, out var rightRowIndex))
            {
                matchedRight[rightRowIndex] = true;
                pairs.Add(BuildPair(key, left.Rows[i], right.Rows[rightRowIndex],
                    columns, leftColumnIndex, rightColumnIndex, comparer));
            }
            else
            {
                pairs.Add(BuildPair(key, left.Rows[i], null,
                    columns, leftColumnIndex, rightColumnIndex, comparer));
            }
        }

        for (var i = 0; i < right.Rows.Count; i++)
        {
            if (matchedRight[i])
                continue;

            pairs.Add(BuildPair(rightKeys[i], null, right.Rows[i],
                columns, leftColumnIndex, rightColumnIndex, comparer));
        }

        var tableName = string.IsNullOrEmpty(left.TableName) ? right.TableName : left.TableName;
        return new TableComparison(tableName, columns, pairs);
    }

    /// <summary>Left columns in their own order, then any right column not already present.</summary>
    private static List<string> UnionColumns(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var columns = new List<string>(left.Count + right.Count);
        var seen = new HashSet<string>(left.Count + right.Count, StringComparer.Ordinal);

        foreach (var column in left)
        {
            if (seen.Add(column))
                columns.Add(column);
        }

        foreach (var column in right)
        {
            if (seen.Add(column))
                columns.Add(column);
        }

        return columns;
    }

    /// <summary>Maps a column name to its position in one side's value list; first spelling wins.</summary>
    private static Dictionary<string, int> BuildColumnIndex(IReadOnlyList<string> columns)
    {
        var index = new Dictionary<string, int>(columns.Count, StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
            index.TryAdd(columns[i], i);

        return index;
    }

    /// <summary>
    /// Gives every row a key that is unique within its side. Not every browsable table has a truly
    /// unique key, so repeats get a <c>" #2"</c>, <c>" #3"</c> … suffix in row order. Both sides run
    /// the same rule, so duplicates on one side still line up with duplicates on the other.
    /// </summary>
    /// <param name="rows">The rows to key, in their original order.</param>
    /// <param name="comparer">Key comparer — ordinal, or ordinal-ignore-case.</param>
    /// <param name="keys">Receives the resolved key of each row, by index.</param>
    /// <param name="byKey">Receives the reverse lookup from resolved key to row index.</param>
    private static void AssignKeys(
        IReadOnlyList<TableRow> rows,
        StringComparer comparer,
        string[] keys,
        out Dictionary<string, int> byKey)
    {
        byKey = new Dictionary<string, int>(rows.Count, comparer);

        // Remembering the next free suffix per base key keeps this linear even when every row in a
        // large table shares one key; without it each repeat would rescan the suffixes already taken.
        var nextOccurrence = new Dictionary<string, int>(comparer);

        for (var i = 0; i < rows.Count; i++)
        {
            var baseKey = rows[i].Key ?? "";
            if (!nextOccurrence.TryGetValue(baseKey, out var occurrence) || occurrence < 1)
                occurrence = 1;

            string key;
            while (true)
            {
                key = occurrence == 1
                    ? baseKey
                    : baseKey + " #" + occurrence.ToString(CultureInfo.InvariantCulture);
                occurrence++;

                // A suffixed key can collide with a row that literally carries that text; take the
                // next suffix rather than losing one of the two rows.
                if (byKey.TryAdd(key, i))
                    break;
            }

            nextOccurrence[baseKey] = occurrence;
            keys[i] = key;
        }
    }

    private static RowPair BuildPair(
        string key,
        TableRow? leftRow,
        TableRow? rightRow,
        IReadOnlyList<string> columns,
        Dictionary<string, int> leftColumnIndex,
        Dictionary<string, int> rightColumnIndex,
        StringComparer comparer)
    {
        var cells = new List<CellPair>(columns.Count);
        var anyDifferent = false;
        var oneSided = leftRow is null || rightRow is null;

        foreach (var column in columns)
        {
            var leftValue = ValueOf(leftRow, leftColumnIndex, column);
            var rightValue = ValueOf(rightRow, rightColumnIndex, column);

            // A one-sided row has nothing to compare against, so every value it does carry counts as
            // a difference — that is what makes ChangedColumns informative for Added and Removed.
            var isDifferent = oneSided
                ? !IsBlank(leftValue) || !IsBlank(rightValue)
                : !AreEqual(leftValue, rightValue, comparer);

            if (isDifferent)
                anyDifferent = true;

            cells.Add(new CellPair(column, leftValue, rightValue, isDifferent));
        }

        var state = leftRow is null
            ? RowState.Added
            : rightRow is null
                ? RowState.Removed
                : anyDifferent
                    ? RowState.Changed
                    : RowState.Same;

        return new RowPair(key, state, cells);
    }

    /// <summary>Value of one column on one side, or null when that side has no such column.</summary>
    private static string? ValueOf(TableRow? row, Dictionary<string, int> columnIndex, string column)
    {
        if (row is null || !columnIndex.TryGetValue(column, out var ordinal) || ordinal >= row.Values.Count)
            return null;

        return row.Values[ordinal];
    }

    /// <summary>
    /// Equality for two cells. A missing value and an empty one mean the same thing — SQLite stores
    /// an absent OMT attribute either way depending on the source file — so they compare as equal.
    /// </summary>
    private static bool AreEqual(string? left, string? right, StringComparer comparer)
    {
        if (IsBlank(left) || IsBlank(right))
            return IsBlank(left) && IsBlank(right);

        return comparer.Equals(left, right);
    }

    /// <summary>
    /// True for null or the empty string. Whitespace is deliberately significant: an attribute whose
    /// semantics differ only by indentation really has changed.
    /// </summary>
    private static bool IsBlank(string? value) => string.IsNullOrEmpty(value);
}
