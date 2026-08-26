using System;
using System.Collections.Generic;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Registry;

/// <summary>Persistent store of parsed FOMs. Backed by SQLite.</summary>
public interface IFomRepository : IDisposable
{
    /// <summary>Absolute path of the SQLite database file.</summary>
    string DatabasePath { get; }

    /// <summary>Every registered FOM, newest registration first.</summary>
    IReadOnlyList<FomRegistryEntry> ListEntries();

    FomRegistryEntry? GetEntry(long id);

    /// <summary>Finds an entry by absolute source path, ordinal-ignore-case.</summary>
    FomRegistryEntry? FindByPath(string filePath);

    /// <summary>
    /// Writes a parsed document and all of its OMT tables into the database.
    /// Replaces any existing entry with the same <paramref name="filePath"/>.
    /// </summary>
    /// <param name="companionPath">
    /// The second source file when the document was merged from a pair — an HLA 1.3 FED and its OMT.
    /// Recorded so the entry can be re-parsed from both later. Null for a single-file entry.
    /// </param>
    /// <param name="composedFrom">
    /// The modules this document was compiled from, in compile order, or null for a FOM registered
    /// from a single file. File names rather than paths: the compiled model is already stored here
    /// in full, so this is a record of where it came from and not a route back to it.
    /// </param>
    FomRegistryEntry Register(FomDocument document, string displayName, string filePath,
        string? companionPath = null, IReadOnlyList<string>? composedFrom = null);

    /// <summary>Rebuilds the full <see cref="FomDocument"/> from the stored rows.</summary>
    FomDocument LoadDocument(long id);

    void Rename(long id, string displayName);

    void Delete(long id);

    /// <summary>Recomputes IsStale / IsMissing for every entry against the files on disk.</summary>
    void RefreshFileState(IEnumerable<FomRegistryEntry> entries);

    /// <summary>Records that a comparison was run, for the history list.</summary>
    long SaveComparison(ComparisonResult result, long leftId, long rightId);

    IReadOnlyList<ComparisonHistoryEntry> ListComparisons(int limit = 50);

    void DeleteComparison(long id);

    /// <summary>Every browsable table in the registry database, in presentation order.</summary>
    IReadOnlyList<RegistryTable> ListTables();

    /// <summary>Reads one table's rows for one registered FOM, straight out of SQLite.</summary>
    TableSnapshot ReadTable(long fomId, string tableName);

    /// <summary>Number of rows the given table holds for the given FOM.</summary>
    int CountRows(long fomId, string tableName);
}

/// <summary>A previously executed comparison, for the history strip on the Compare screen.</summary>
public sealed class ComparisonHistoryEntry
{
    public long Id { get; set; }
    public long LeftFomId { get; set; }
    public long RightFomId { get; set; }
    public string LeftName { get; set; } = "";
    public string RightName { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int ModifiedCount { get; set; }
    public int UnchangedCount { get; set; }
    public int TotalDifferences => AddedCount + RemovedCount + ModifiedCount;
    public string OptionsJson { get; set; } = "{}";
}
