using System.Collections.Generic;

namespace HLAFomReader.Core.Registry;

/// <summary>
/// One browsable table in the registry database. The <see cref="Sql"/> is authored here rather than
/// composed at runtime — it joins away surrogate ids so the view shows readable values, and it must
/// return a <c>Key</c> column first, followed by the columns to display.
/// </summary>
public sealed class RegistryTable
{
    public RegistryTable(string name, string displayName, string sql, string description)
    {
        Name = name;
        DisplayName = displayName;
        Sql = sql;
        Description = description;
    }

    /// <summary>Underlying SQLite table name, used as the stable identifier.</summary>
    public string Name { get; }

    /// <summary>Label shown in the table list, e.g. "Attributes".</summary>
    public string DisplayName { get; }

    /// <summary>
    /// SELECT taking a single <c>@fomId</c> parameter. First column must be named <c>Key</c> and
    /// must identify the row within its FOM; the remaining columns are shown as-is.
    /// </summary>
    public string Sql { get; }

    /// <summary>One-line explanation of what the table holds.</summary>
    public string Description { get; }

    public override string ToString() => DisplayName;
}

/// <summary>A single row read out of the registry database.</summary>
public sealed class TableRow
{
    public TableRow(string key, IReadOnlyList<string?> values)
    {
        Key = key;
        Values = values;
    }

    /// <summary>Row identity within its FOM. Used to align the two sides.</summary>
    public string Key { get; }

    /// <summary>Values in the same order as <see cref="TableSnapshot.Columns"/>.</summary>
    public IReadOnlyList<string?> Values { get; }

    public override string ToString() => Key;
}

/// <summary>The rows of one table for one registered FOM.</summary>
public sealed class TableSnapshot
{
    public TableSnapshot(string tableName, IReadOnlyList<string> columns, IReadOnlyList<TableRow> rows)
    {
        TableName = tableName;
        Columns = columns;
        Rows = rows;
    }

    public string TableName { get; }

    /// <summary>Display columns, excluding the leading <c>Key</c> column.</summary>
    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<TableRow> Rows { get; }

    public int RowCount => Rows.Count;

    public static TableSnapshot Empty(string tableName) =>
        new(tableName, new List<string>(), new List<TableRow>());
}
