using System.Collections.Generic;
using System.Linq;

namespace HLAFomReader.Core.Comparison;

/// <summary>How a stored row lines up between the two FOMs.</summary>
public enum RowState
{
    /// <summary>Present on both sides with every column equal.</summary>
    Same = 0,
    /// <summary>Present on both sides, at least one column differs.</summary>
    Changed = 1,
    /// <summary>Present only in FOM B.</summary>
    Added = 2,
    /// <summary>Present only in FOM A.</summary>
    Removed = 3,
}

/// <summary>One column of one aligned row pair.</summary>
public sealed class CellPair
{
    public CellPair(string column, string? left, string? right, bool isDifferent)
    {
        Column = column;
        Left = left;
        Right = right;
        IsDifferent = isDifferent;
    }

    public string Column { get; }
    public string? Left { get; }
    public string? Right { get; }
    public bool IsDifferent { get; }
}

/// <summary>Two stored rows with the same key, lined up column by column.</summary>
public sealed class RowPair
{
    public RowPair(string key, RowState state, IReadOnlyList<CellPair> cells)
    {
        Key = key;
        State = state;
        Cells = cells;
    }

    public string Key { get; }
    public RowState State { get; }
    public IReadOnlyList<CellPair> Cells { get; }

    /// <summary>Comma-separated names of the columns that differ — the grid's "what changed" column.</summary>
    public string ChangedColumns =>
        string.Join(", ", Cells.Where(c => c.IsDifferent).Select(c => c.Column));

    public bool IsDifferent => State != RowState.Same;
}

/// <summary>The result of aligning one registry table across two FOMs.</summary>
public sealed class TableComparison
{
    public TableComparison(string tableName, IReadOnlyList<string> columns, IReadOnlyList<RowPair> rows)
    {
        TableName = tableName;
        Columns = columns;
        Rows = rows;
    }

    public string TableName { get; }

    /// <summary>Union of the display columns present on either side, in left-then-new order.</summary>
    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<RowPair> Rows { get; }

    public int SameCount => Rows.Count(r => r.State == RowState.Same);
    public int ChangedCount => Rows.Count(r => r.State == RowState.Changed);
    public int AddedCount => Rows.Count(r => r.State == RowState.Added);
    public int RemovedCount => Rows.Count(r => r.State == RowState.Removed);
    public int DifferenceCount => ChangedCount + AddedCount + RemovedCount;
    public bool IsIdentical => DifferenceCount == 0;

    public static TableComparison Empty(string tableName) =>
        new(tableName, new List<string>(), new List<RowPair>());
}
