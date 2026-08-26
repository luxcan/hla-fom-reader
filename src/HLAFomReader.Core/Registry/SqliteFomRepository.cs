using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;
using Microsoft.Data.Sqlite;

namespace HLAFomReader.Core.Registry;

/// <summary>
/// SQLite-backed <see cref="IFomRepository"/>. Documents are stored relationally — one table per
/// OMT table — so the registry can list, count and query FOMs without re-parsing the source files.
/// The connection is opened once and held for the lifetime of the instance.
/// </summary>
/// <remarks>
/// Threading contract: all public members are serialised on a single gate; the repository is safe
/// to share across threads but performs one operation at a time. A <see cref="SqliteConnection"/>
/// is not thread-safe, so every public entry point takes <c>_gate</c> for the whole operation —
/// including draining a reader — and delegates to a private <c>…Core</c> method that assumes the
/// gate is already held. The gate is not reentrant: inside the class, call the <c>…Core</c> method,
/// never the public wrapper.
/// </remarks>
public sealed class SqliteFomRepository : IFomRepository
{
    /// <summary>Kind discriminators for <c>FomIdentificationValues</c>.</summary>
    private const string KindKeyword = "Keyword";
    private const string KindPoc = "Poc";
    private const string KindUseHistory = "UseHistory";

    /// <summary>Kind discriminators for <c>DataTypes</c>.</summary>
    private const string KindBasic = "Basic";
    private const string KindSimple = "Simple";
    private const string KindEnumerated = "Enumerated";
    private const string KindArray = "Array";
    private const string KindFixedRecord = "FixedRecord";
    private const string KindVariantRecord = "VariantRecord";

    /// <summary>Kind discriminators for <c>DataTypeMembers</c>.</summary>
    private const string KindEnumerator = "Enumerator";
    private const string KindField = "Field";
    private const string KindAlternative = "Alternative";

    /// <summary>Guards against a malformed class tree recursing forever.</summary>
    private const int MaxClassDepth = 512;

    /// <summary>Read-only properties on <see cref="ComparisonOptions"/> (the comparers) are not persisted.</summary>
    private static readonly JsonSerializerOptions OptionsJsonSettings = new()
    {
        IgnoreReadOnlyProperties = true,
        WriteIndented = false,
    };

    /// <summary>
    /// Serialises every public operation. Held for the whole of an operation, so no reader is ever
    /// left open — and no lazily-evaluated sequence ever returned — outside the lock.
    /// </summary>
    private readonly object _gate = new();

    private readonly SqliteConnection _connection;
    private bool _disposed;

    /// <summary>
    /// Opens (creating if necessary) the store and brings its schema up to date.
    /// </summary>
    /// <param name="databasePath">
    /// Path to the database file, or null for <see cref="FomDatabase.DefaultDatabasePath"/>.
    /// </param>
    /// <param name="password">
    /// SQLCipher key unlocking the file. Null or empty means a plaintext store, which is the
    /// default and what every pre-encryption call site gets.
    /// </param>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">
    /// The store is encrypted and <paramref name="password"/> is wrong or missing.
    /// </exception>
    public SqliteFomRepository(string? databasePath = null, string? password = null)
    {
        DatabasePath = string.IsNullOrWhiteSpace(databasePath)
            ? FomDatabase.DefaultDatabasePath
            : databasePath;

        _connection = FomDatabase.Open(DatabasePath, password);
        try
        {
            FomDatabase.EnsureSchema(_connection);
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string DatabasePath { get; }

    // ---------------------------------------------------------------------------------------
    // Hashing
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Lowercase hex SHA-256 of the file's bytes, or null when the file cannot be read.
    /// Used to detect that a registered FOM has changed on disk.
    /// </summary>
    /// <param name="filePath">Path of the file to hash.</param>
    public static string? ComputeFileHash(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        try
        {
            // FileShare.ReadWrite so a FOM still open in an editor can still be hashed.
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException
                                      or System.Security.SecurityException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Registry queries
    // ---------------------------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyList<FomRegistryEntry> ListEntries()
    {
        lock (_gate)
            return ListEntriesCore();
    }

    /// <summary>Body of <see cref="ListEntries"/>; the caller must already hold <c>_gate</c>.</summary>
    private IReadOnlyList<FomRegistryEntry> ListEntriesCore()
    {
        ThrowIfDisposed();

        var entries = new List<FomRegistryEntry>();
        Query(
            EntrySelectSql + " ORDER BY RegisteredUtc DESC, Id DESC;",
            reader => entries.Add(ReadEntry(reader)));

        // A second pass rather than a join: an entry has few dependencies and most have none, so
        // this is a handful of indexed lookups against a list already in hand.

        return entries;
    }

    /// <inheritdoc />
    public FomRegistryEntry? GetEntry(long id)
    {
        lock (_gate)
            return GetEntryCore(id);
    }

    /// <summary>Body of <see cref="GetEntry"/>; the caller must already hold <c>_gate</c>.</summary>
    private FomRegistryEntry? GetEntryCore(long id)
    {
        ThrowIfDisposed();

        FomRegistryEntry? entry = null;
        Query(
            EntrySelectSql + " WHERE Id = @id;",
            reader => entry ??= ReadEntry(reader),
            ("@id", id));

        return entry;
    }

    /// <inheritdoc />
    public FomRegistryEntry? FindByPath(string filePath)
    {
        lock (_gate)
            return FindByPathCore(filePath);
    }

    /// <summary>Body of <see cref="FindByPath"/>; the caller must already hold <c>_gate</c>.</summary>
    private FomRegistryEntry? FindByPathCore(string filePath)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        const string sql = " WHERE FilePath = @path COLLATE NOCASE LIMIT 1;";
        var fullPath = NormalizePath(filePath);

        FomRegistryEntry? entry = null;
        Query(EntrySelectSql + sql, reader => entry ??= ReadEntry(reader), ("@path", fullPath));

        if (entry is null && !string.Equals(fullPath, filePath, StringComparison.Ordinal))
        {
            // Fall back to the caller's spelling: the row may have been written from a path
            // that does not normalise to the same string (a mapped drive or UNC alias).
            Query(EntrySelectSql + sql, reader => entry ??= ReadEntry(reader), ("@path", filePath));
        }

        return entry;
    }

    /// <inheritdoc />
    public void Rename(long id, string displayName)
    {
        lock (_gate)
            RenameCore(id, displayName);
    }

    /// <summary>Body of <see cref="Rename"/>; the caller must already hold <c>_gate</c>.</summary>
    private void RenameCore(long id, string displayName)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A display name is required.", nameof(displayName));

        Execute("UPDATE Foms SET DisplayName = @name WHERE Id = @id;",
            ("@name", displayName.Trim()),
            ("@id", id));
    }

    /// <inheritdoc />
    public void Delete(long id)
    {
        lock (_gate)
            DeleteCore(id);
    }

    /// <summary>Body of <see cref="Delete"/>; the caller must already hold <c>_gate</c>.</summary>
    private void DeleteCore(long id)
    {
        ThrowIfDisposed();
        Execute("DELETE FROM Foms WHERE Id = @id;", ("@id", id));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Both source files count. An HLA 1.3 entry was assembled from a <c>.fed</c> and its <c>.omt</c>
    /// together and cannot be rebuilt from either alone, so a vanished companion is reported as
    /// <see cref="FomRegistryEntry.IsMissing"/> and a changed companion as
    /// <see cref="FomRegistryEntry.IsStale"/>, exactly as for the primary file. A single-file entry
    /// has no companion and is judged on its own file only.
    /// <para>
    /// The only public member that stays outside <c>_gate</c>: it inspects the file system and the
    /// entries handed to it, and never touches the connection. Holding the gate here would block a
    /// query behind a slow disk for no benefit. Keep it that way — if this ever needs to read the
    /// database, it has to take the gate like everything else.
    /// </para>
    /// </remarks>
    public void RefreshFileState(IEnumerable<FomRegistryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var entry in entries)
        {
            if (entry is null)
                continue;

            var primaryExists = !string.IsNullOrWhiteSpace(entry.FilePath) && File.Exists(entry.FilePath);
            var companionExists = !entry.IsPair || File.Exists(entry.CompanionPath!);
            entry.IsMissing = !primaryExists || !companionExists;

            if (entry.IsMissing)
            {
                // Half a pair says nothing useful about whether the contents have drifted; the
                // entry has to be re-registered from both files regardless.
                entry.IsStale = false;
                continue;
            }

            entry.IsStale = HasChanged(entry.FilePath, entry.FileHash)
                || (entry.IsPair && HasChanged(entry.CompanionPath!, entry.CompanionHash));
        }
    }

    /// <summary>
    /// True when the file at <paramref name="path"/> no longer hashes to
    /// <paramref name="recordedHash"/>.
    /// </summary>
    /// <remarks>
    /// With no recorded hash, or no readable current one, there is nothing to compare against —
    /// report "unchanged" rather than raising a false alarm.
    /// </remarks>
    private static bool HasChanged(string path, string? recordedHash)
    {
        if (string.IsNullOrEmpty(recordedHash))
            return false;

        var hash = ComputeFileHash(path);
        return hash is not null && !string.Equals(hash, recordedHash, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------
    // Registration
    // ---------------------------------------------------------------------------------------

    /// <inheritdoc />
    public FomRegistryEntry Register(FomDocument document, string displayName, string filePath,
        string? companionPath = null, IReadOnlyList<string>? composedFrom = null)
    {
        lock (_gate)
            return RegisterCore(document, displayName, filePath, companionPath, composedFrom);
    }

    /// <summary>Body of <see cref="Register"/>; the caller must already hold <c>_gate</c>.</summary>
    private FomRegistryEntry RegisterCore(FomDocument document, string displayName, string filePath,
        string? companionPath, IReadOnlyList<string>? composedFrom)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A source file path is required.", nameof(filePath));

        var fullPath = NormalizePath(filePath);
        var fileName = SafeFileName(fullPath);
        var identification = document.Identification ?? new ModelIdentification();

        // Stored exactly as handed over — the caller picked the pair, and normalising a path that
        // may be a mapped-drive or UNC alias would only make it harder to match back to their file.
        // A blank companion is the same thing as none at all, so it collapses to null.
        var companion = string.IsNullOrWhiteSpace(companionPath) ? null : companionPath;

        var entry = new FomRegistryEntry
        {
            Key = Guid.NewGuid(),
            DisplayName = ResolveDisplayName(displayName, identification, fullPath, fileName),
            FilePath = fullPath,
            FileName = fileName,
            Standard = document.Standard,
            SourceNamespace = document.SourceNamespace,
            FileHash = ComputeFileHash(fullPath),
            CompanionPath = companion,

            // Null when there is no companion, and also when there is one that cannot be read right
            // now: an unhashable file is not evidence of a change, and RefreshFileState treats a
            // missing recorded hash as "nothing to compare against" rather than as staleness.
            CompanionHash = companion is null ? null : ComputeFileHash(companion),

            // Joined on a newline: see FomRegistryEntry.ModuleSeparator for why that separator.
            ComposedFrom = composedFrom is { Count: > 0 }
                ? string.Join(FomRegistryEntry.ModuleSeparator, composedFrom)
                : null,

            RegisteredUtc = DateTime.UtcNow,
            LastParsedUtc = DateTime.UtcNow,
            IdentificationName = identification.Name,
            IdentificationType = identification.Type,
            Version = identification.Version,
            Purpose = identification.Purpose,
            ApplicationDomain = identification.ApplicationDomain,
            Description = identification.Description,
            ModificationDate = identification.ModificationDate,
            SecurityClassification = identification.SecurityClassification,
            ObjectClassCount = document.ObjectClassCount,
            AttributeCount = document.AttributeCount,
            InteractionClassCount = document.InteractionClassCount,
            ParameterCount = document.ParameterCount,
            DataTypeCount = document.DataTypeCount,
            DimensionCount = document.DimensionCount,
        };

        ReadFileState(fullPath, entry);
        CountDiagnostics(document, entry);

        using var transaction = _connection.BeginTransaction();

        // Re-registering the same file — which is what Re-parse does — replaces the row, and a
        // replacement must not claim the FOM was first seen today. Keep the original date: it is
        // what the Registered column means, and what the list is ordered by, so resetting it moves
        // the entry to the top as though it were new.
        if (ReadFirstRegisteredUtc(transaction, fullPath) is { } original)
            entry.RegisteredUtc = original;

        // The unique index on FilePath makes replacement the only sane semantic; the cascade
        // sweeps out every child row belonging to the previous registration.
        Execute(transaction, "DELETE FROM Foms WHERE FilePath = @path COLLATE NOCASE;", ("@path", fullPath));

        entry.Id = InsertWithId(transaction, "Foms",
            ("Key", entry.Key.ToString("D", CultureInfo.InvariantCulture)),
            ("DisplayName", entry.DisplayName),
            ("FilePath", entry.FilePath),
            ("FileName", entry.FileName),
            ("Standard", (int)entry.Standard),
            ("SourceNamespace", entry.SourceNamespace),
            ("FileHash", entry.FileHash),
            ("CompanionPath", entry.CompanionPath),
            ("CompanionHash", entry.CompanionHash),
            ("FileSizeBytes", entry.FileSizeBytes),
            ("FileModifiedUtc", entry.FileModifiedUtc),
            ("RegisteredUtc", entry.RegisteredUtc),
            ("LastParsedUtc", entry.LastParsedUtc),
            ("IdentName", identification.Name),
            ("IdentType", identification.Type),
            ("IdentVersion", identification.Version),
            ("IdentModificationDate", identification.ModificationDate),
            ("IdentSecurityClassification", identification.SecurityClassification),
            ("IdentReleaseRestriction", identification.ReleaseRestriction),
            ("IdentPurpose", identification.Purpose),
            ("IdentApplicationDomain", identification.ApplicationDomain),
            ("IdentDescription", identification.Description),
            ("IdentUseLimitation", identification.UseLimitation),
            ("IdentReference", identification.Reference),
            ("IdentOther", identification.Other),
            ("IdentGlyph", identification.Glyph),
            ("ObjectClassCount", entry.ObjectClassCount),
            ("AttributeCount", entry.AttributeCount),
            ("InteractionClassCount", entry.InteractionClassCount),
            ("ParameterCount", entry.ParameterCount),
            ("DataTypeCount", entry.DataTypeCount),
            ("DimensionCount", entry.DimensionCount),
            ("ErrorCount", entry.ErrorCount),
            ("WarningCount", entry.WarningCount),
            ("ComposedFrom", entry.ComposedFrom));

        WriteIdentificationValues(transaction, entry.Id, identification);
        WriteObjectClasses(transaction, entry.Id, document.ObjectClasses, parentId: null, depth: 0,
            new HashSet<FomObjectClass>(ReferenceEqualityComparer.Instance));
        WriteInteractionClasses(transaction, entry.Id, document.InteractionClasses, parentId: null, depth: 0,
            new HashSet<FomInteractionClass>(ReferenceEqualityComparer.Instance));
        WriteDataTypes(transaction, entry.Id, document.DataTypes);
        WriteDimensions(transaction, entry.Id, document.Dimensions);
        WriteRoutingSpaces(transaction, entry.Id, document.RoutingSpaces);
        WriteTransportations(transaction, entry.Id, document.Transportations);
        WriteSynchronizations(transaction, entry.Id, document.Synchronizations);
        WriteUpdateRates(transaction, entry.Id, document.UpdateRates);
        WriteSwitches(transaction, entry.Id, document.Switches);
        WriteTags(transaction, entry.Id, document.Tags);
        WriteNotes(transaction, entry.Id, document.Notes);
        WriteTime(transaction, entry.Id, document.Time);
        WriteDiagnostics(transaction, entry.Id, document.Diagnostics);

        transaction.Commit();
        return entry;
    }

    /// <summary>Picks the first non-blank of: caller label, modelIdentification name, file stem, file name.</summary>
    private static string ResolveDisplayName(string? displayName, ModelIdentification identification, string fullPath, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
            return displayName.Trim();

        if (!string.IsNullOrWhiteSpace(identification.Name))
            return identification.Name!.Trim();

        string? stem = null;
        try
        {
            stem = Path.GetFileNameWithoutExtension(fullPath);
        }
        catch (ArgumentException)
        {
            // Unusable path characters — fall through to the file name.
        }

        if (!string.IsNullOrWhiteSpace(stem))
            return stem!;

        return string.IsNullOrWhiteSpace(fileName) ? "(unnamed FOM)" : fileName;
    }

    /// <summary>Records size and last-write time, tolerating a file that is gone or unreadable.</summary>
    private static void ReadFileState(string fullPath, FomRegistryEntry entry)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Exists)
            {
                entry.FileSizeBytes = info.Length;
                entry.FileModifiedUtc = info.LastWriteTimeUtc;
            }
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException
                                      or System.Security.SecurityException)
        {
            entry.FileSizeBytes = 0;
            entry.FileModifiedUtc = null;
        }
    }

    /// <summary>Tallies error and warning diagnostics onto the entry.</summary>
    private static void CountDiagnostics(FomDocument document, FomRegistryEntry entry)
    {
        foreach (var diagnostic in document.Diagnostics)
        {
            if (diagnostic is null)
                continue;

            if (diagnostic.Severity == DiagnosticSeverity.Error)
                entry.ErrorCount++;
            else if (diagnostic.Severity == DiagnosticSeverity.Warning)
                entry.WarningCount++;
        }
    }

    private void WriteIdentificationValues(SqliteTransaction transaction, long fomId, ModelIdentification identification)
    {
        WriteValueList(transaction, fomId, KindKeyword, identification.Keywords);
        WriteValueList(transaction, fomId, KindPoc, identification.PointsOfContact);
        WriteValueList(transaction, fomId, KindUseHistory, identification.UseHistory);
    }

    private void WriteValueList(SqliteTransaction transaction, long fomId, string kind, List<string>? values)
    {
        if (values is null)
            return;

        var ordinal = 0;
        foreach (var value in values)
        {
            Execute(transaction,
                "INSERT INTO FomIdentificationValues (FomId, Kind, Ordinal, Value) VALUES (@fomId, @kind, @ordinal, @value);",
                ("@fomId", fomId), ("@kind", kind), ("@ordinal", ordinal++), ("@value", value));
        }
    }

    private void WriteObjectClasses(
        SqliteTransaction transaction,
        long fomId,
        IEnumerable<FomObjectClass>? classes,
        long? parentId,
        int depth,
        HashSet<FomObjectClass> visited)
    {
        if (classes is null || depth > MaxClassDepth)
            return;

        var ordinal = 0;
        foreach (var objectClass in classes)
        {
            // A parser that accidentally wires a class as its own ancestor must not hang the write.
            if (objectClass is null || !visited.Add(objectClass))
                continue;

            var classId = InsertWithId(transaction, "ObjectClasses",
                ("FomId", fomId),
                ("ParentId", parentId),
                ("Name", objectClass.Name),
                ("QualifiedName", objectClass.QualifiedName),
                ("Sharing", objectClass.Sharing),
                ("Semantics", objectClass.Semantics),
                ("NoteRefs", objectClass.Notes),
                ("Ordinal", ordinal++));

            WriteAttributes(transaction, fomId, classId, objectClass.Attributes);
            WriteObjectClasses(transaction, fomId, objectClass.Children, classId, depth + 1, visited);
        }
    }

    private void WriteAttributes(SqliteTransaction transaction, long fomId, long classId, IEnumerable<FomAttribute>? attributes)
    {
        if (attributes is null)
            return;

        var ordinal = 0;
        foreach (var attribute in attributes)
        {
            if (attribute is null)
                continue;

            var attributeId = InsertWithId(transaction, "ObjectAttributes",
                ("FomId", fomId),
                ("ObjectClassId", classId),
                ("Name", attribute.Name),
                ("QualifiedName", attribute.QualifiedName),
                ("DataType", attribute.DataType),
                ("Cardinality", attribute.Cardinality),
                ("Units", attribute.Units),
                ("Resolution", attribute.Resolution),
                ("Accuracy", attribute.Accuracy),
                ("AccuracyCondition", attribute.AccuracyCondition),
                ("UpdateType", attribute.UpdateType),
                ("UpdateCondition", attribute.UpdateCondition),
                ("Ownership", attribute.Ownership),
                ("Sharing", attribute.Sharing),
                ("Transportation", attribute.Transportation),
                ("\"Order\"", attribute.Order),
                ("RoutingSpace", attribute.RoutingSpace),
                ("Semantics", attribute.Semantics),
                ("NoteRefs", attribute.Notes),
                ("Ordinal", ordinal++));

            WriteNameList(transaction,
                "INSERT INTO AttributeDimensions (AttributeId, Ordinal, DimensionName) VALUES (@ownerId, @ordinal, @name);",
                attributeId, attribute.Dimensions);
        }
    }

    private void WriteInteractionClasses(
        SqliteTransaction transaction,
        long fomId,
        IEnumerable<FomInteractionClass>? classes,
        long? parentId,
        int depth,
        HashSet<FomInteractionClass> visited)
    {
        if (classes is null || depth > MaxClassDepth)
            return;

        var ordinal = 0;
        foreach (var interactionClass in classes)
        {
            if (interactionClass is null || !visited.Add(interactionClass))
                continue;

            var classId = InsertWithId(transaction, "InteractionClasses",
                ("FomId", fomId),
                ("ParentId", parentId),
                ("Name", interactionClass.Name),
                ("QualifiedName", interactionClass.QualifiedName),
                ("Sharing", interactionClass.Sharing),
                ("Transportation", interactionClass.Transportation),
                ("\"Order\"", interactionClass.Order),
                ("RoutingSpace", interactionClass.RoutingSpace),
                ("Semantics", interactionClass.Semantics),
                ("NoteRefs", interactionClass.Notes),
                ("Ordinal", ordinal++));

            WriteNameList(transaction,
                "INSERT INTO InteractionDimensions (InteractionClassId, Ordinal, DimensionName) VALUES (@ownerId, @ordinal, @name);",
                classId, interactionClass.Dimensions);

            WriteParameters(transaction, fomId, classId, interactionClass.Parameters);
            WriteInteractionClasses(transaction, fomId, interactionClass.Children, classId, depth + 1, visited);
        }
    }

    private void WriteParameters(SqliteTransaction transaction, long fomId, long classId, IEnumerable<FomParameter>? parameters)
    {
        if (parameters is null)
            return;

        var ordinal = 0;
        foreach (var parameter in parameters)
        {
            if (parameter is null)
                continue;

            Execute(transaction,
                """
                INSERT INTO InteractionParameters
                    (FomId, InteractionClassId, Name, QualifiedName, DataType, Cardinality, Units,
                     Resolution, Accuracy, AccuracyCondition, Semantics, NoteRefs, Ordinal)
                VALUES (@fomId, @classId, @name, @qualifiedName, @dataType, @cardinality, @units,
                        @resolution, @accuracy, @accuracyCondition, @semantics, @noteRefs, @ordinal);
                """,
                ("@fomId", fomId), ("@classId", classId), ("@name", parameter.Name),
                ("@qualifiedName", parameter.QualifiedName), ("@dataType", parameter.DataType),
                ("@cardinality", parameter.Cardinality), ("@units", parameter.Units),
                ("@resolution", parameter.Resolution), ("@accuracy", parameter.Accuracy),
                ("@accuracyCondition", parameter.AccuracyCondition),
                ("@semantics", parameter.Semantics), ("@noteRefs", parameter.Notes), ("@ordinal", ordinal++));
        }
    }

    private void WriteDataTypes(SqliteTransaction transaction, long fomId, FomDataTypeTables? tables)
    {
        if (tables is null)
            return;

        var ordinal = 0;
        foreach (var basic in tables.BasicDataRepresentations)
        {
            if (basic is null) continue;
            InsertDataType(transaction, fomId, KindBasic, basic, ordinal++,
                size: basic.Size, interpretation: basic.Interpretation, endian: basic.Endian, encoding: basic.Encoding);
        }

        ordinal = 0;
        foreach (var simple in tables.SimpleDataTypes)
        {
            if (simple is null) continue;
            InsertDataType(transaction, fomId, KindSimple, simple, ordinal++,
                representation: simple.Representation, units: simple.Units,
                resolution: simple.Resolution, accuracy: simple.Accuracy);
        }

        ordinal = 0;
        foreach (var enumerated in tables.EnumeratedDataTypes)
        {
            if (enumerated is null) continue;
            var id = InsertDataType(transaction, fomId, KindEnumerated, enumerated, ordinal++,
                representation: enumerated.Representation);

            var memberOrdinal = 0;
            foreach (var enumerator in enumerated.Enumerators)
            {
                if (enumerator is null) continue;
                InsertDataTypeMember(transaction, id, KindEnumerator, enumerator, memberOrdinal++,
                    memberValues: enumerator.Values);
            }
        }

        ordinal = 0;
        foreach (var array in tables.ArrayDataTypes)
        {
            if (array is null) continue;
            InsertDataType(transaction, fomId, KindArray, array, ordinal++,
                encoding: array.Encoding, elementDataType: array.DataType, cardinality: array.Cardinality);
        }

        ordinal = 0;
        foreach (var fixedRecord in tables.FixedRecordDataTypes)
        {
            if (fixedRecord is null) continue;
            var id = InsertDataType(transaction, fomId, KindFixedRecord, fixedRecord, ordinal++,
                encoding: fixedRecord.Encoding, includeRef: fixedRecord.Include);

            var memberOrdinal = 0;
            foreach (var field in fixedRecord.Fields)
            {
                if (field is null) continue;
                InsertDataTypeMember(transaction, id, KindField, field, memberOrdinal++, dataType: field.DataType);
            }
        }

        ordinal = 0;
        foreach (var variant in tables.VariantRecordDataTypes)
        {
            if (variant is null) continue;
            var id = InsertDataType(transaction, fomId, KindVariantRecord, variant, ordinal++,
                encoding: variant.Encoding, discriminant: variant.Discriminant, discriminantDataType: variant.DataType);

            var memberOrdinal = 0;
            foreach (var alternative in variant.Alternatives)
            {
                if (alternative is null) continue;
                InsertDataTypeMember(transaction, id, KindAlternative, alternative, memberOrdinal++,
                    dataType: alternative.DataType, enumerator: alternative.Enumerator);
            }
        }
    }

    /// <summary>Writes one row of the shared DataTypes table; unused columns stay null for that kind.</summary>
    private long InsertDataType(
        SqliteTransaction transaction,
        long fomId,
        string kind,
        FomNode node,
        int ordinal,
        string? size = null,
        string? interpretation = null,
        string? endian = null,
        string? encoding = null,
        string? representation = null,
        string? units = null,
        string? resolution = null,
        string? accuracy = null,
        string? elementDataType = null,
        string? cardinality = null,
        string? discriminant = null,
        string? discriminantDataType = null,
        string? includeRef = null)
        => InsertWithId(transaction, "DataTypes",
            ("FomId", fomId),
            ("Kind", kind),
            ("Name", node.Name),
            ("QualifiedName", node.QualifiedName),
            ("Size", size),
            ("Interpretation", interpretation),
            ("Endian", endian),
            ("Encoding", encoding),
            ("Representation", representation),
            ("Units", units),
            ("Resolution", resolution),
            ("Accuracy", accuracy),
            ("ElementDataType", elementDataType),
            ("Cardinality", cardinality),
            ("Discriminant", discriminant),
            ("DiscriminantDataType", discriminantDataType),
            ("IncludeRef", includeRef),
            ("Semantics", node.Semantics),
            ("NoteRefs", node.Notes),
            ("Ordinal", ordinal));

    private void InsertDataTypeMember(
        SqliteTransaction transaction,
        long dataTypeId,
        string kind,
        FomNode node,
        int ordinal,
        string? memberValues = null,
        string? dataType = null,
        string? enumerator = null)
        => Execute(transaction,
            """
            INSERT INTO DataTypeMembers
                (DataTypeId, Kind, Name, QualifiedName, MemberValues, DataType, Enumerator, Semantics, NoteRefs, Ordinal)
            VALUES (@dataTypeId, @kind, @name, @qualifiedName, @memberValues, @dataType, @enumerator, @semantics, @noteRefs, @ordinal);
            """,
            ("@dataTypeId", dataTypeId), ("@kind", kind), ("@name", node.Name),
            ("@qualifiedName", node.QualifiedName), ("@memberValues", memberValues), ("@dataType", dataType),
            ("@enumerator", enumerator), ("@semantics", node.Semantics), ("@noteRefs", node.Notes),
            ("@ordinal", ordinal));

    private void WriteDimensions(SqliteTransaction transaction, long fomId, IEnumerable<FomDimension>? dimensions)
    {
        if (dimensions is null)
            return;

        var ordinal = 0;
        foreach (var dimension in dimensions)
        {
            if (dimension is null) continue;
            Execute(transaction,
                """
                INSERT INTO Dimensions
                    (FomId, Name, QualifiedName, DataType, UpperBound, Normalization, Value, Semantics, NoteRefs, Ordinal)
                VALUES (@fomId, @name, @qualifiedName, @dataType, @upperBound, @normalization, @value, @semantics, @noteRefs, @ordinal);
                """,
                ("@fomId", fomId), ("@name", dimension.Name), ("@qualifiedName", dimension.QualifiedName),
                ("@dataType", dimension.DataType), ("@upperBound", dimension.UpperBound),
                ("@normalization", dimension.Normalization), ("@value", dimension.Value),
                ("@semantics", dimension.Semantics), ("@noteRefs", dimension.Notes), ("@ordinal", ordinal++));
        }
    }

    private void WriteRoutingSpaces(SqliteTransaction transaction, long fomId, IEnumerable<FomRoutingSpace>? spaces)
    {
        if (spaces is null)
            return;

        var ordinal = 0;
        foreach (var space in spaces)
        {
            if (space is null) continue;

            var spaceId = InsertWithId(transaction, "RoutingSpaces",
                ("FomId", fomId),
                ("Name", space.Name),
                ("QualifiedName", space.QualifiedName),
                ("Semantics", space.Semantics),
                ("NoteRefs", space.Notes),
                ("Ordinal", ordinal++));

            WriteNameList(transaction,
                "INSERT INTO RoutingSpaceDimensions (RoutingSpaceId, Ordinal, Name) VALUES (@ownerId, @ordinal, @name);",
                spaceId, space.Dimensions);
        }
    }

    private void WriteTransportations(SqliteTransaction transaction, long fomId, IEnumerable<FomTransportation>? transportations)
    {
        if (transportations is null)
            return;

        var ordinal = 0;
        foreach (var transportation in transportations)
        {
            if (transportation is null) continue;
            Execute(transaction,
                """
                INSERT INTO Transportations (FomId, Name, QualifiedName, Reliable, Semantics, NoteRefs, Ordinal)
                VALUES (@fomId, @name, @qualifiedName, @reliable, @semantics, @noteRefs, @ordinal);
                """,
                ("@fomId", fomId), ("@name", transportation.Name), ("@qualifiedName", transportation.QualifiedName),
                ("@reliable", transportation.Reliable), ("@semantics", transportation.Semantics),
                ("@noteRefs", transportation.Notes), ("@ordinal", ordinal++));
        }
    }

    private void WriteSynchronizations(SqliteTransaction transaction, long fomId, IEnumerable<FomSynchronization>? synchronizations)
    {
        if (synchronizations is null)
            return;

        var ordinal = 0;
        foreach (var synchronization in synchronizations)
        {
            if (synchronization is null) continue;
            Execute(transaction,
                """
                INSERT INTO Synchronizations (FomId, Name, QualifiedName, Capability, DataType, Semantics, NoteRefs, Ordinal)
                VALUES (@fomId, @name, @qualifiedName, @capability, @dataType, @semantics, @noteRefs, @ordinal);
                """,
                ("@fomId", fomId), ("@name", synchronization.Name), ("@qualifiedName", synchronization.QualifiedName),
                ("@capability", synchronization.Capability), ("@dataType", synchronization.DataType),
                ("@semantics", synchronization.Semantics), ("@noteRefs", synchronization.Notes), ("@ordinal", ordinal++));
        }
    }

    private void WriteUpdateRates(SqliteTransaction transaction, long fomId, IEnumerable<FomUpdateRate>? updateRates)
    {
        if (updateRates is null)
            return;

        var ordinal = 0;
        foreach (var updateRate in updateRates)
        {
            if (updateRate is null) continue;
            Execute(transaction,
                """
                INSERT INTO UpdateRates (FomId, Name, QualifiedName, Rate, Semantics, NoteRefs, Ordinal)
                VALUES (@fomId, @name, @qualifiedName, @rate, @semantics, @noteRefs, @ordinal);
                """,
                ("@fomId", fomId), ("@name", updateRate.Name), ("@qualifiedName", updateRate.QualifiedName),
                ("@rate", updateRate.Rate), ("@semantics", updateRate.Semantics),
                ("@noteRefs", updateRate.Notes), ("@ordinal", ordinal++));
        }
    }

    private void WriteSwitches(SqliteTransaction transaction, long fomId, IEnumerable<FomSwitch>? switches)
    {
        if (switches is null)
            return;

        var ordinal = 0;
        foreach (var item in switches)
        {
            if (item is null) continue;
            Execute(transaction,
                """
                INSERT INTO Switches (FomId, Name, QualifiedName, IsEnabled, ResignSwitch, Semantics, NoteRefs, Ordinal)
                VALUES (@fomId, @name, @qualifiedName, @isEnabled, @resignSwitch, @semantics, @noteRefs, @ordinal);
                """,
                ("@fomId", fomId), ("@name", item.Name), ("@qualifiedName", item.QualifiedName),
                ("@isEnabled", item.IsEnabled), ("@resignSwitch", item.ResignSwitch),
                ("@semantics", item.Semantics), ("@noteRefs", item.Notes), ("@ordinal", ordinal++));
        }
    }

    private void WriteTags(SqliteTransaction transaction, long fomId, IEnumerable<FomTag>? tags)
    {
        if (tags is null)
            return;

        var ordinal = 0;
        foreach (var tag in tags)
        {
            if (tag is null) continue;
            Execute(transaction,
                """
                INSERT INTO Tags (FomId, Name, QualifiedName, DataType, Semantics, NoteRefs, Ordinal)
                VALUES (@fomId, @name, @qualifiedName, @dataType, @semantics, @noteRefs, @ordinal);
                """,
                ("@fomId", fomId), ("@name", tag.Name), ("@qualifiedName", tag.QualifiedName),
                ("@dataType", tag.DataType), ("@semantics", tag.Semantics),
                ("@noteRefs", tag.Notes), ("@ordinal", ordinal++));
        }
    }

    private void WriteNotes(SqliteTransaction transaction, long fomId, IEnumerable<FomNote>? notes)
    {
        if (notes is null)
            return;

        var ordinal = 0;
        foreach (var note in notes)
        {
            if (note is null) continue;
            Execute(transaction,
                """
                INSERT INTO FomNotes (FomId, Name, QualifiedName, Label, Text, Semantics, Ordinal)
                VALUES (@fomId, @name, @qualifiedName, @label, @text, @semantics, @ordinal);
                """,
                ("@fomId", fomId), ("@name", note.Name), ("@qualifiedName", note.QualifiedName),
                ("@label", note.Label), ("@text", note.Text), ("@semantics", note.Semantics), ("@ordinal", ordinal++));
        }
    }

    private void WriteTime(SqliteTransaction transaction, long fomId, FomTime? time)
    {
        if (time is null || time.IsEmpty)
            return;

        Execute(transaction,
            """
            INSERT INTO TimeRepresentation
                (FomId, TimeStampDataType, TimeStampSemantics, LookaheadDataType, LookaheadSemantics)
            VALUES (@fomId, @timeStampDataType, @timeStampSemantics, @lookaheadDataType, @lookaheadSemantics);
            """,
            ("@fomId", fomId), ("@timeStampDataType", time.TimeStampDataType),
            ("@timeStampSemantics", time.TimeStampSemantics), ("@lookaheadDataType", time.LookaheadDataType),
            ("@lookaheadSemantics", time.LookaheadSemantics));
    }

    private void WriteDiagnostics(SqliteTransaction transaction, long fomId, IEnumerable<ParseDiagnostic>? diagnostics)
    {
        if (diagnostics is null)
            return;

        var ordinal = 0;
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic is null) continue;
            Execute(transaction,
                """
                INSERT INTO Diagnostics (FomId, Severity, Message, Line, Path, Ordinal)
                VALUES (@fomId, @severity, @message, @line, @path, @ordinal);
                """,
                ("@fomId", fomId), ("@severity", (int)diagnostic.Severity),
                ("@message", diagnostic.Message ?? ""), ("@line", diagnostic.Line),
                ("@path", diagnostic.Path), ("@ordinal", ordinal++));
        }
    }

    /// <summary>Writes a simple ordered list of names into a two-column child table.</summary>
    private void WriteNameList(SqliteTransaction transaction, string sql, long ownerId, List<string>? names)
    {
        if (names is null)
            return;

        var ordinal = 0;
        foreach (var name in names)
        {
            Execute(transaction, sql, ("@ownerId", ownerId), ("@ordinal", ordinal++), ("@name", name));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Loading
    // ---------------------------------------------------------------------------------------

    /// <inheritdoc />
    public FomDocument LoadDocument(long id)
    {
        lock (_gate)
            return LoadDocumentCore(id);
    }

    /// <summary>Body of <see cref="LoadDocument"/>; the caller must already hold <c>_gate</c>.</summary>
    private FomDocument LoadDocumentCore(long id)
    {
        ThrowIfDisposed();

        var document = new FomDocument();
        var found = false;

        Query(
            """
            SELECT Standard, FilePath, SourceNamespace, IdentName, IdentType, IdentVersion,
                   IdentModificationDate, IdentSecurityClassification, IdentReleaseRestriction,
                   IdentPurpose, IdentApplicationDomain, IdentDescription, IdentUseLimitation,
                   IdentReference, IdentOther, IdentGlyph
            FROM Foms WHERE Id = @id;
            """,
            reader =>
            {
                if (found)
                    return;

                found = true;
                document.Standard = ReadStandard(reader, "Standard");
                document.SourcePath = ReadString(reader, "FilePath");
                document.SourceNamespace = ReadString(reader, "SourceNamespace");
                document.Identification = new ModelIdentification
                {
                    Name = ReadString(reader, "IdentName"),
                    Type = ReadString(reader, "IdentType"),
                    Version = ReadString(reader, "IdentVersion"),
                    ModificationDate = ReadString(reader, "IdentModificationDate"),
                    SecurityClassification = ReadString(reader, "IdentSecurityClassification"),
                    ReleaseRestriction = ReadString(reader, "IdentReleaseRestriction"),
                    Purpose = ReadString(reader, "IdentPurpose"),
                    ApplicationDomain = ReadString(reader, "IdentApplicationDomain"),
                    Description = ReadString(reader, "IdentDescription"),
                    UseLimitation = ReadString(reader, "IdentUseLimitation"),
                    Reference = ReadString(reader, "IdentReference"),
                    Other = ReadString(reader, "IdentOther"),
                    Glyph = ReadString(reader, "IdentGlyph"),
                };
            },
            ("@id", id));

        if (!found)
            throw new KeyNotFoundException($"No FOM is registered with id {id}.");

        LoadIdentificationValues(id, document.Identification);
        LoadObjectClasses(id, document);
        LoadInteractionClasses(id, document);
        LoadDataTypes(id, document);
        LoadDimensions(id, document);
        LoadRoutingSpaces(id, document);
        LoadTransportations(id, document);
        LoadSynchronizations(id, document);
        LoadUpdateRates(id, document);
        LoadSwitches(id, document);
        LoadTags(id, document);
        LoadNotes(id, document);
        LoadTime(id, document);
        LoadDiagnostics(id, document);

        return document;
    }

    private void LoadIdentificationValues(long fomId, ModelIdentification identification)
    {
        Query(
            "SELECT Kind, Value FROM FomIdentificationValues WHERE FomId = @fomId ORDER BY Kind, Ordinal, Id;",
            reader =>
            {
                var value = ReadString(reader, "Value");
                if (value is null)
                    return;

                switch (ReadString(reader, "Kind"))
                {
                    case KindKeyword: identification.Keywords.Add(value); break;
                    case KindPoc: identification.PointsOfContact.Add(value); break;
                    case KindUseHistory: identification.UseHistory.Add(value); break;
                }
            },
            ("@fomId", fomId));
    }

    private void LoadObjectClasses(long fomId, FomDocument document)
    {
        var byId = new Dictionary<long, FomObjectClass>();
        var parents = new List<(long Id, long? ParentId)>();

        Query(
            """
            SELECT Id, ParentId, Name, QualifiedName, Sharing, Semantics, NoteRefs
            FROM ObjectClasses WHERE FomId = @fomId ORDER BY Ordinal, Id;
            """,
            reader =>
            {
                var rowId = ReadInt64(reader, "Id");
                var objectClass = ReadNode(reader, new FomObjectClass());
                objectClass.Sharing = ReadString(reader, "Sharing");
                byId[rowId] = objectClass;
                parents.Add((rowId, ReadNullableInt64(reader, "ParentId")));
            },
            ("@fomId", fomId));

        // Second pass: siblings were read in ordinal order, so appending here preserves it.
        foreach (var (rowId, parentId) in parents)
        {
            var objectClass = byId[rowId];
            if (parentId is not null && byId.TryGetValue(parentId.Value, out var parent) && !ReferenceEquals(parent, objectClass))
            {
                objectClass.Parent = parent;
                parent.Children.Add(objectClass);
            }
            else
            {
                document.ObjectClasses.Add(objectClass);
            }
        }

        LoadObjectAttributes(fomId, byId);
    }

    private void LoadObjectAttributes(long fomId, Dictionary<long, FomObjectClass> classes)
    {
        var byId = new Dictionary<long, FomAttribute>();

        Query(
            """
            SELECT Id, ObjectClassId, Name, QualifiedName, DataType, Cardinality, Units, Resolution,
                   Accuracy, AccuracyCondition, UpdateType, UpdateCondition,
                   Ownership, Sharing, Transportation, "Order" AS OrderToken, RoutingSpace,
                   Semantics, NoteRefs
            FROM ObjectAttributes WHERE FomId = @fomId ORDER BY ObjectClassId, Ordinal, Id;
            """,
            reader =>
            {
                if (!classes.TryGetValue(ReadInt64(reader, "ObjectClassId"), out var owner))
                    return;

                var attribute = ReadNode(reader, new FomAttribute());
                attribute.DataType = ReadString(reader, "DataType");
                attribute.Cardinality = ReadString(reader, "Cardinality");
                attribute.Units = ReadString(reader, "Units");
                attribute.Resolution = ReadString(reader, "Resolution");
                attribute.Accuracy = ReadString(reader, "Accuracy");
                attribute.AccuracyCondition = ReadString(reader, "AccuracyCondition");
                attribute.UpdateType = ReadString(reader, "UpdateType");
                attribute.UpdateCondition = ReadString(reader, "UpdateCondition");
                attribute.Ownership = ReadString(reader, "Ownership");
                attribute.Sharing = ReadString(reader, "Sharing");
                attribute.Transportation = ReadString(reader, "Transportation");
                attribute.Order = ReadString(reader, "OrderToken");
                attribute.RoutingSpace = ReadString(reader, "RoutingSpace");

                owner.Attributes.Add(attribute);
                byId[ReadInt64(reader, "Id")] = attribute;
            },
            ("@fomId", fomId));

        Query(
            """
            SELECT d.AttributeId AS AttributeId, d.DimensionName AS DimensionName
            FROM AttributeDimensions d
            JOIN ObjectAttributes a ON a.Id = d.AttributeId
            WHERE a.FomId = @fomId
            ORDER BY d.AttributeId, d.Ordinal, d.Id;
            """,
            reader =>
            {
                var name = ReadString(reader, "DimensionName");
                if (name is not null && byId.TryGetValue(ReadInt64(reader, "AttributeId"), out var attribute))
                    attribute.Dimensions.Add(name);
            },
            ("@fomId", fomId));
    }

    private void LoadInteractionClasses(long fomId, FomDocument document)
    {
        var byId = new Dictionary<long, FomInteractionClass>();
        var parents = new List<(long Id, long? ParentId)>();

        Query(
            """
            SELECT Id, ParentId, Name, QualifiedName, Sharing, Transportation,
                   "Order" AS OrderToken, RoutingSpace, Semantics, NoteRefs
            FROM InteractionClasses WHERE FomId = @fomId ORDER BY Ordinal, Id;
            """,
            reader =>
            {
                var rowId = ReadInt64(reader, "Id");
                var interactionClass = ReadNode(reader, new FomInteractionClass());
                interactionClass.Sharing = ReadString(reader, "Sharing");
                interactionClass.Transportation = ReadString(reader, "Transportation");
                interactionClass.Order = ReadString(reader, "OrderToken");
                interactionClass.RoutingSpace = ReadString(reader, "RoutingSpace");
                byId[rowId] = interactionClass;
                parents.Add((rowId, ReadNullableInt64(reader, "ParentId")));
            },
            ("@fomId", fomId));

        foreach (var (rowId, parentId) in parents)
        {
            var interactionClass = byId[rowId];
            if (parentId is not null && byId.TryGetValue(parentId.Value, out var parent) && !ReferenceEquals(parent, interactionClass))
            {
                interactionClass.Parent = parent;
                parent.Children.Add(interactionClass);
            }
            else
            {
                document.InteractionClasses.Add(interactionClass);
            }
        }

        Query(
            """
            SELECT d.InteractionClassId AS InteractionClassId, d.DimensionName AS DimensionName
            FROM InteractionDimensions d
            JOIN InteractionClasses c ON c.Id = d.InteractionClassId
            WHERE c.FomId = @fomId
            ORDER BY d.InteractionClassId, d.Ordinal, d.Id;
            """,
            reader =>
            {
                var name = ReadString(reader, "DimensionName");
                if (name is not null && byId.TryGetValue(ReadInt64(reader, "InteractionClassId"), out var owner))
                    owner.Dimensions.Add(name);
            },
            ("@fomId", fomId));

        Query(
            """
            SELECT InteractionClassId, Name, QualifiedName, DataType, Cardinality, Units, Resolution,
                   Accuracy, AccuracyCondition, Semantics, NoteRefs
            FROM InteractionParameters WHERE FomId = @fomId ORDER BY InteractionClassId, Ordinal, Id;
            """,
            reader =>
            {
                if (!byId.TryGetValue(ReadInt64(reader, "InteractionClassId"), out var owner))
                    return;

                var parameter = ReadNode(reader, new FomParameter());
                parameter.DataType = ReadString(reader, "DataType");
                parameter.Cardinality = ReadString(reader, "Cardinality");
                parameter.Units = ReadString(reader, "Units");
                parameter.Resolution = ReadString(reader, "Resolution");
                parameter.Accuracy = ReadString(reader, "Accuracy");
                parameter.AccuracyCondition = ReadString(reader, "AccuracyCondition");
                owner.Parameters.Add(parameter);
            },
            ("@fomId", fomId));
    }

    private void LoadDataTypes(long fomId, FomDocument document)
    {
        var enumerated = new Dictionary<long, EnumeratedDataType>();
        var fixedRecords = new Dictionary<long, FixedRecordDataType>();
        var variantRecords = new Dictionary<long, VariantRecordDataType>();

        Query(
            """
            SELECT Id, Kind, Name, QualifiedName, Size, Interpretation, Endian, Encoding, Representation,
                   Units, Resolution, Accuracy, ElementDataType, Cardinality, Discriminant,
                   DiscriminantDataType, IncludeRef, Semantics, NoteRefs
            FROM DataTypes WHERE FomId = @fomId ORDER BY Kind, Ordinal, Id;
            """,
            reader =>
            {
                var rowId = ReadInt64(reader, "Id");
                switch (ReadString(reader, "Kind"))
                {
                    case KindBasic:
                        var basic = ReadNode(reader, new BasicDataType());
                        basic.Size = ReadString(reader, "Size");
                        basic.Interpretation = ReadString(reader, "Interpretation");
                        basic.Endian = ReadString(reader, "Endian");
                        basic.Encoding = ReadString(reader, "Encoding");
                        document.DataTypes.BasicDataRepresentations.Add(basic);
                        break;

                    case KindSimple:
                        var simple = ReadNode(reader, new SimpleDataType());
                        simple.Representation = ReadString(reader, "Representation");
                        simple.Units = ReadString(reader, "Units");
                        simple.Resolution = ReadString(reader, "Resolution");
                        simple.Accuracy = ReadString(reader, "Accuracy");
                        document.DataTypes.SimpleDataTypes.Add(simple);
                        break;

                    case KindEnumerated:
                        var enumeration = ReadNode(reader, new EnumeratedDataType());
                        enumeration.Representation = ReadString(reader, "Representation");
                        document.DataTypes.EnumeratedDataTypes.Add(enumeration);
                        enumerated[rowId] = enumeration;
                        break;

                    case KindArray:
                        var array = ReadNode(reader, new ArrayDataType());
                        array.DataType = ReadString(reader, "ElementDataType");
                        array.Cardinality = ReadString(reader, "Cardinality");
                        array.Encoding = ReadString(reader, "Encoding");
                        document.DataTypes.ArrayDataTypes.Add(array);
                        break;

                    case KindFixedRecord:
                        var fixedRecord = ReadNode(reader, new FixedRecordDataType());
                        fixedRecord.Encoding = ReadString(reader, "Encoding");
                        fixedRecord.Include = ReadString(reader, "IncludeRef");
                        document.DataTypes.FixedRecordDataTypes.Add(fixedRecord);
                        fixedRecords[rowId] = fixedRecord;
                        break;

                    case KindVariantRecord:
                        var variant = ReadNode(reader, new VariantRecordDataType());
                        variant.Discriminant = ReadString(reader, "Discriminant");
                        variant.DataType = ReadString(reader, "DiscriminantDataType");
                        variant.Encoding = ReadString(reader, "Encoding");
                        document.DataTypes.VariantRecordDataTypes.Add(variant);
                        variantRecords[rowId] = variant;
                        break;
                }
            },
            ("@fomId", fomId));

        if (enumerated.Count == 0 && fixedRecords.Count == 0 && variantRecords.Count == 0)
            return;

        Query(
            """
            SELECT m.DataTypeId AS DataTypeId, m.Kind AS Kind, m.Name AS Name,
                   m.QualifiedName AS QualifiedName, m.MemberValues AS MemberValues,
                   m.DataType AS DataType, m.Enumerator AS Enumerator,
                   m.Semantics AS Semantics, m.NoteRefs AS NoteRefs
            FROM DataTypeMembers m
            JOIN DataTypes t ON t.Id = m.DataTypeId
            WHERE t.FomId = @fomId
            ORDER BY m.DataTypeId, m.Ordinal, m.Id;
            """,
            reader =>
            {
                var ownerId = ReadInt64(reader, "DataTypeId");
                switch (ReadString(reader, "Kind"))
                {
                    case KindEnumerator when enumerated.TryGetValue(ownerId, out var owner):
                        var enumerator = ReadNode(reader, new EnumeratorValue());
                        enumerator.Values = ReadString(reader, "MemberValues");
                        owner.Enumerators.Add(enumerator);
                        break;

                    case KindField when fixedRecords.TryGetValue(ownerId, out var owningRecord):
                        var field = ReadNode(reader, new RecordField());
                        field.DataType = ReadString(reader, "DataType");
                        owningRecord.Fields.Add(field);
                        break;

                    case KindAlternative when variantRecords.TryGetValue(ownerId, out var variant):
                        var alternative = ReadNode(reader, new VariantAlternative());
                        alternative.Enumerator = ReadString(reader, "Enumerator");
                        alternative.DataType = ReadString(reader, "DataType");
                        variant.Alternatives.Add(alternative);
                        break;
                }
            },
            ("@fomId", fomId));
    }

    private void LoadDimensions(long fomId, FomDocument document)
    {
        Query(
            """
            SELECT Name, QualifiedName, DataType, UpperBound, Normalization, Value, Semantics, NoteRefs
            FROM Dimensions WHERE FomId = @fomId ORDER BY Ordinal, Id;
            """,
            reader =>
            {
                var dimension = ReadNode(reader, new FomDimension());
                dimension.DataType = ReadString(reader, "DataType");
                dimension.UpperBound = ReadString(reader, "UpperBound");
                dimension.Normalization = ReadString(reader, "Normalization");
                dimension.Value = ReadString(reader, "Value");
                document.Dimensions.Add(dimension);
            },
            ("@fomId", fomId));
    }

    private void LoadRoutingSpaces(long fomId, FomDocument document)
    {
        var byId = new Dictionary<long, FomRoutingSpace>();

        Query(
            "SELECT Id, Name, QualifiedName, Semantics, NoteRefs FROM RoutingSpaces WHERE FomId = @fomId ORDER BY Ordinal, Id;",
            reader =>
            {
                var space = ReadNode(reader, new FomRoutingSpace());
                document.RoutingSpaces.Add(space);
                byId[ReadInt64(reader, "Id")] = space;
            },
            ("@fomId", fomId));

        if (byId.Count == 0)
            return;

        Query(
            """
            SELECT d.RoutingSpaceId AS RoutingSpaceId, d.Name AS Name
            FROM RoutingSpaceDimensions d
            JOIN RoutingSpaces s ON s.Id = d.RoutingSpaceId
            WHERE s.FomId = @fomId
            ORDER BY d.RoutingSpaceId, d.Ordinal, d.Id;
            """,
            reader =>
            {
                var name = ReadString(reader, "Name");
                if (name is not null && byId.TryGetValue(ReadInt64(reader, "RoutingSpaceId"), out var space))
                    space.Dimensions.Add(name);
            },
            ("@fomId", fomId));
    }

    private void LoadTransportations(long fomId, FomDocument document)
    {
        Query(
            "SELECT Name, QualifiedName, Reliable, Semantics, NoteRefs FROM Transportations WHERE FomId = @fomId ORDER BY Ordinal, Id;",
            reader =>
            {
                var transportation = ReadNode(reader, new FomTransportation());
                transportation.Reliable = ReadString(reader, "Reliable");
                document.Transportations.Add(transportation);
            },
            ("@fomId", fomId));
    }

    private void LoadSynchronizations(long fomId, FomDocument document)
    {
        Query(
            """
            SELECT Name, QualifiedName, Capability, DataType, Semantics, NoteRefs
            FROM Synchronizations WHERE FomId = @fomId ORDER BY Ordinal, Id;
            """,
            reader =>
            {
                var synchronization = ReadNode(reader, new FomSynchronization());
                synchronization.Capability = ReadString(reader, "Capability");
                synchronization.DataType = ReadString(reader, "DataType");
                document.Synchronizations.Add(synchronization);
            },
            ("@fomId", fomId));
    }

    private void LoadUpdateRates(long fomId, FomDocument document)
    {
        Query(
            "SELECT Name, QualifiedName, Rate, Semantics, NoteRefs FROM UpdateRates WHERE FomId = @fomId ORDER BY Ordinal, Id;",
            reader =>
            {
                var updateRate = ReadNode(reader, new FomUpdateRate());
                updateRate.Rate = ReadString(reader, "Rate");
                document.UpdateRates.Add(updateRate);
            },
            ("@fomId", fomId));
    }

    private void LoadSwitches(long fomId, FomDocument document)
    {
        Query(
            """
            SELECT Name, QualifiedName, IsEnabled, ResignSwitch, Semantics, NoteRefs
            FROM Switches WHERE FomId = @fomId ORDER BY Ordinal, Id;
            """,
            reader =>
            {
                var item = ReadNode(reader, new FomSwitch());
                item.IsEnabled = ReadString(reader, "IsEnabled");
                item.ResignSwitch = ReadString(reader, "ResignSwitch");
                document.Switches.Add(item);
            },
            ("@fomId", fomId));
    }

    private void LoadTags(long fomId, FomDocument document)
    {
        Query(
            "SELECT Name, QualifiedName, DataType, Semantics, NoteRefs FROM Tags WHERE FomId = @fomId ORDER BY Ordinal, Id;",
            reader =>
            {
                var tag = ReadNode(reader, new FomTag());
                tag.DataType = ReadString(reader, "DataType");
                document.Tags.Add(tag);
            },
            ("@fomId", fomId));
    }

    private void LoadNotes(long fomId, FomDocument document)
    {
        Query(
            "SELECT Name, QualifiedName, Label, Text, Semantics FROM FomNotes WHERE FomId = @fomId ORDER BY Ordinal, Id;",
            reader =>
            {
                // FomNotes carries no NoteRefs column — a note cannot reference itself.
                var note = new FomNote
                {
                    Name = ReadString(reader, "Name") ?? "",
                    QualifiedName = ReadString(reader, "QualifiedName") ?? "",
                    Semantics = ReadString(reader, "Semantics"),
                    Label = ReadString(reader, "Label"),
                    Text = ReadString(reader, "Text"),
                };
                document.Notes.Add(note);
            },
            ("@fomId", fomId));
    }

    private void LoadTime(long fomId, FomDocument document)
    {
        Query(
            """
            SELECT TimeStampDataType, TimeStampSemantics, LookaheadDataType, LookaheadSemantics
            FROM TimeRepresentation WHERE FomId = @fomId;
            """,
            reader => document.Time = new FomTime
            {
                TimeStampDataType = ReadString(reader, "TimeStampDataType"),
                TimeStampSemantics = ReadString(reader, "TimeStampSemantics"),
                LookaheadDataType = ReadString(reader, "LookaheadDataType"),
                LookaheadSemantics = ReadString(reader, "LookaheadSemantics"),
            },
            ("@fomId", fomId));
    }

    private void LoadDiagnostics(long fomId, FomDocument document)
    {
        Query(
            "SELECT Severity, Message, Line, Path FROM Diagnostics WHERE FomId = @fomId ORDER BY Ordinal, Id;",
            reader => document.Diagnostics.Add(new ParseDiagnostic
            {
                Severity = ReadSeverity(reader, "Severity"),
                Message = ReadString(reader, "Message") ?? "",
                Line = ReadNullableInt32(reader, "Line"),
                Path = ReadString(reader, "Path"),
            }),
            ("@fomId", fomId));
    }

    // ---------------------------------------------------------------------------------------
    // Comparison history
    // ---------------------------------------------------------------------------------------

    /// <inheritdoc />
    public long SaveComparison(ComparisonResult result, long leftId, long rightId)
    {
        lock (_gate)
            return SaveComparisonCore(result, leftId, rightId);
    }

    /// <summary>Body of <see cref="SaveComparison"/>; the caller must already hold <c>_gate</c>.</summary>
    private long SaveComparisonCore(ComparisonResult result, long leftId, long rightId)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(result);

        var optionsJson = SerializeOptions(result.Options);

        using var transaction = _connection.BeginTransaction();
        var id = InsertWithId(transaction, "Comparisons",
            ("LeftFomId", leftId),
            ("RightFomId", rightId),
            ("CreatedUtc", result.CreatedUtc),
            ("OptionsJson", optionsJson),
            ("AddedCount", result.AddedCount),
            ("RemovedCount", result.RemovedCount),
            ("ModifiedCount", result.ModifiedCount),
            ("UnchangedCount", result.UnchangedCount));
        transaction.Commit();

        return id;
    }

    /// <inheritdoc />
    public IReadOnlyList<ComparisonHistoryEntry> ListComparisons(int limit = 50)
    {
        lock (_gate)
            return ListComparisonsCore(limit);
    }

    /// <summary>Body of <see cref="ListComparisons"/>; the caller must already hold <c>_gate</c>.</summary>
    private IReadOnlyList<ComparisonHistoryEntry> ListComparisonsCore(int limit)
    {
        ThrowIfDisposed();

        var entries = new List<ComparisonHistoryEntry>();
        Query(
            """
            SELECT c.Id AS Id, c.LeftFomId AS LeftFomId, c.RightFomId AS RightFomId,
                   c.CreatedUtc AS CreatedUtc, c.OptionsJson AS OptionsJson,
                   c.AddedCount AS AddedCount, c.RemovedCount AS RemovedCount,
                   c.ModifiedCount AS ModifiedCount, c.UnchangedCount AS UnchangedCount,
                   COALESCE(l.DisplayName, '(deleted)') AS LeftName,
                   COALESCE(r.DisplayName, '(deleted)') AS RightName
            FROM Comparisons c
            LEFT JOIN Foms l ON l.Id = c.LeftFomId
            LEFT JOIN Foms r ON r.Id = c.RightFomId
            ORDER BY c.CreatedUtc DESC, c.Id DESC
            LIMIT @limit;
            """,
            reader => entries.Add(new ComparisonHistoryEntry
            {
                Id = ReadInt64(reader, "Id"),
                LeftFomId = ReadInt64(reader, "LeftFomId"),
                RightFomId = ReadInt64(reader, "RightFomId"),
                LeftName = ReadString(reader, "LeftName") ?? "(deleted)",
                RightName = ReadString(reader, "RightName") ?? "(deleted)",
                CreatedUtc = ReadDateTime(reader, "CreatedUtc") ?? default,
                AddedCount = ReadInt32(reader, "AddedCount"),
                RemovedCount = ReadInt32(reader, "RemovedCount"),
                ModifiedCount = ReadInt32(reader, "ModifiedCount"),
                UnchangedCount = ReadInt32(reader, "UnchangedCount"),
                OptionsJson = ReadString(reader, "OptionsJson") ?? "{}",
            }),
            // SQLite treats a negative LIMIT as "no limit", which is the sane reading of limit <= 0.
            ("@limit", limit <= 0 ? -1 : limit));

        return entries;
    }

    /// <inheritdoc />
    public void DeleteComparison(long id)
    {
        lock (_gate)
            DeleteComparisonCore(id);
    }

    /// <summary>Body of <see cref="DeleteComparison"/>; the caller must already hold <c>_gate</c>.</summary>
    private void DeleteComparisonCore(long id)
    {
        ThrowIfDisposed();
        Execute("DELETE FROM Comparisons WHERE Id = @id;", ("@id", id));
    }

    /// <summary>Serialises the options, degrading to an empty object rather than failing a save.</summary>
    private static string SerializeOptions(ComparisonOptions? options)
    {
        if (options is null)
            return "{}";

        try
        {
            return JsonSerializer.Serialize(options, OptionsJsonSettings);
        }
        catch (NotSupportedException)
        {
            return "{}";
        }
    }

    // ---------------------------------------------------------------------------------------
    // Table browsing
    // ---------------------------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyList<RegistryTable> ListTables()
    {
        // The catalogue is static, but every public member goes through the gate so callers never
        // have to reason about which ones happen to touch the connection.
        lock (_gate)
            return ListTablesCore();
    }

    /// <summary>Body of <see cref="ListTables"/>; the caller must already hold <c>_gate</c>.</summary>
    private static IReadOnlyList<RegistryTable> ListTablesCore() => RegistryTables.All;

    /// <inheritdoc />
    public TableSnapshot ReadTable(long fomId, string tableName)
    {
        lock (_gate)
            return ReadTableCore(fomId, tableName);
    }

    /// <summary>Body of <see cref="ReadTable"/>; the caller must already hold <c>_gate</c>.</summary>
    private TableSnapshot ReadTableCore(long fomId, string tableName)
    {
        ThrowIfDisposed();

        // An unknown name is a stale selection in the UI, not a programming error — show nothing.
        var table = RegistryTables.Find(tableName);
        if (table is null)
            return TableSnapshot.Empty(tableName);

        var columns = new List<string>();
        var rows = new List<TableRow>();

        using var command = CreateCommand(null, table.Sql, new (string Name, object? Value)[] { ("@fomId", fomId) });
        using var reader = command.ExecuteReader();

        // Field names are available before the first Read, so an empty table still yields headers.
        var keyOrdinal = -1;
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (keyOrdinal < 0 && string.Equals(name, KeyColumnName, StringComparison.OrdinalIgnoreCase))
            {
                keyOrdinal = i;
                continue;
            }

            columns.Add(name);
        }

        while (reader.Read())
        {
            var values = new List<string?>(columns.Count);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i != keyOrdinal)
                    values.Add(ReadCell(reader, i));
            }

            var key = keyOrdinal < 0 ? "" : ReadCell(reader, keyOrdinal) ?? "";
            rows.Add(new TableRow(key, values));
        }

        return new TableSnapshot(table.Name, columns, rows);
    }

    /// <inheritdoc />
    public int CountRows(long fomId, string tableName)
    {
        lock (_gate)
            return CountRowsCore(fomId, tableName);
    }

    /// <summary>Body of <see cref="CountRows"/>; the caller must already hold <c>_gate</c>.</summary>
    private int CountRowsCore(long fomId, string tableName)
    {
        ThrowIfDisposed();

        var table = RegistryTables.Find(tableName);
        if (table is null)
            return 0;

        // The authored SELECTs are terminated statements; the trailing semicolon has to go before
        // one can be wrapped as a subquery. The ordering is pure waste when all we want is a count —
        // it forces a sort of every row — so it comes off too, when it can be removed safely.
        var inner = StripTrailingOrderBy(table.Sql.TrimEnd().TrimEnd(';'));

        using var command = CreateCommand(null, $"SELECT COUNT(*) FROM ({inner});",
            new (string Name, object? Value)[] { ("@fomId", fomId) });
        var scalar = command.ExecuteScalar();
        return scalar is null || scalar is DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Removes a trailing top-level <c>ORDER BY</c> clause so a SELECT can be wrapped in
    /// <c>SELECT COUNT(*) FROM (…)</c> without paying for a sort that cannot change the count.
    /// </summary>
    /// <remarks>
    /// The scan walks the statement once, skipping string literals, quoted and bracketed
    /// identifiers, and both comment styles, and only ever removes a clause found at parenthesis
    /// depth zero — so an <c>ORDER BY</c> inside a subquery, a window function, an aggregate or a
    /// literal is left alone. Anything it cannot read with certainty (an unterminated quote or
    /// comment, unbalanced parentheses, an embedded statement separator, or a <c>LIMIT</c>/
    /// <c>OFFSET</c> whose result the ordering decides) returns the SQL untouched: a slow count is
    /// far cheaper than a wrong one.
    /// </remarks>
    /// <param name="sql">A single SELECT statement, with any trailing semicolon already removed.</param>
    private static string StripTrailingOrderBy(string sql)
    {
        if (string.IsNullOrEmpty(sql))
            return sql;

        var depth = 0;
        var orderByStart = -1;
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            // A literal or a quoted identifier: swallow it whole, a doubled quote escapes itself.
            if (c is '\'' or '"' or '`')
            {
                var closed = false;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] != c)
                    {
                        i++;
                        continue;
                    }

                    if (i + 1 < sql.Length && sql[i + 1] == c)
                    {
                        i += 2;
                        continue;
                    }

                    i++;
                    closed = true;
                    break;
                }

                if (!closed)
                    return sql;

                continue;
            }

            if (c == '[')
            {
                var end = sql.IndexOf(']', i + 1);
                if (end < 0)
                    return sql;

                i = end + 1;
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i + 2);
                i = end < 0 ? sql.Length : end + 1;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    return sql;

                i = end + 2;
                continue;
            }

            if (c == '(')
            {
                depth++;
                i++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                if (depth < 0)
                    return sql;

                i++;
                continue;
            }

            // A second statement would make "the trailing clause" ambiguous.
            if (c == ';')
                return sql;

            if (!char.IsLetter(c) && c != '_')
            {
                i++;
                continue;
            }

            var start = i;
            while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$'))
                i++;

            if (depth != 0)
                continue;

            var word = sql.AsSpan(start, i - start);

            if (word.Equals("LIMIT", StringComparison.OrdinalIgnoreCase)
                || word.Equals("OFFSET", StringComparison.OrdinalIgnoreCase))
            {
                // The ordering picks which rows survive the limit, so it is load-bearing.
                return sql;
            }

            if (!word.Equals("ORDER", StringComparison.OrdinalIgnoreCase))
                continue;

            // "ORDER" only opens the clause when "BY" follows; a column of that name does not.
            var after = i;
            while (after < sql.Length && char.IsWhiteSpace(sql[after]))
                after++;

            var wordEnd = after;
            while (wordEnd < sql.Length && (char.IsLetterOrDigit(sql[wordEnd]) || sql[wordEnd] == '_' || sql[wordEnd] == '$'))
                wordEnd++;

            if (wordEnd > after && sql.AsSpan(after, wordEnd - after).Equals("BY", StringComparison.OrdinalIgnoreCase))
            {
                orderByStart = start;
                i = wordEnd;
            }
        }

        if (orderByStart < 0 || depth != 0)
            return sql;

        var head = sql[..orderByStart].TrimEnd();
        return head.Length == 0 ? sql : head;
    }

    /// <summary>Name of the leading identity column every <see cref="RegistryTable.Sql"/> projects.</summary>
    private const string KeyColumnName = "Key";

    /// <summary>Longest BLOB prefix rendered as hex before the value is elided.</summary>
    private const int MaxBlobPreviewBytes = 32;

    /// <summary>
    /// Renders one field as display text. Everything is formatted invariantly so the two sides of a
    /// comparison never differ merely because of the current locale, and an empty string collapses to
    /// null so a blank cell and a missing cell compare as the same thing.
    /// </summary>
    private static string? ReadCell(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var value = reader.GetValue(ordinal);
        var text = value switch
        {
            byte[] blob => FormatBlob(blob),
            double real => real.ToString(CultureInfo.InvariantCulture),
            long integer => integer.ToString(CultureInfo.InvariantCulture),
            string existing => existing,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        return text.Length == 0 ? null : text;
    }

    /// <summary>Uppercase hex preview of a BLOB, elided past <see cref="MaxBlobPreviewBytes"/>.</summary>
    private static string FormatBlob(byte[] blob)
    {
        if (blob.Length <= MaxBlobPreviewBytes)
            return "0x" + Convert.ToHexString(blob);

        return "0x" + Convert.ToHexString(blob.AsSpan(0, MaxBlobPreviewBytes)) + "…";
    }

    // ---------------------------------------------------------------------------------------
    // Row mapping
    // ---------------------------------------------------------------------------------------

    /// <summary>Column list shared by every registry-entry query.</summary>
    private const string EntrySelectSql = """
        SELECT Id, Key, DisplayName, FilePath, FileName, Standard, SourceNamespace, FileHash,
               CompanionPath, CompanionHash, ComposedFrom,
               FileSizeBytes, FileModifiedUtc, RegisteredUtc, LastParsedUtc,
               IdentName, IdentType, IdentVersion, IdentModificationDate, IdentSecurityClassification,
               IdentPurpose, IdentApplicationDomain, IdentDescription,
               ObjectClassCount, AttributeCount, InteractionClassCount, ParameterCount,
               DataTypeCount, DimensionCount, ErrorCount, WarningCount
        FROM Foms
        """;

    private static FomRegistryEntry ReadEntry(SqliteDataReader reader) => new()
    {
        Id = ReadInt64(reader, "Id"),
        Key = Guid.TryParse(ReadString(reader, "Key"), out var key) ? key : Guid.Empty,
        DisplayName = ReadString(reader, "DisplayName") ?? "",
        FilePath = ReadString(reader, "FilePath") ?? "",
        FileName = ReadString(reader, "FileName") ?? "",
        Standard = ReadStandard(reader, "Standard"),
        SourceNamespace = ReadString(reader, "SourceNamespace"),
        FileHash = ReadString(reader, "FileHash"),
        CompanionPath = ReadString(reader, "CompanionPath"),
        CompanionHash = ReadString(reader, "CompanionHash"),
        ComposedFrom = ReadString(reader, "ComposedFrom"),
        FileSizeBytes = ReadInt64(reader, "FileSizeBytes"),
        FileModifiedUtc = ReadDateTime(reader, "FileModifiedUtc"),
        RegisteredUtc = ReadDateTime(reader, "RegisteredUtc") ?? default,
        LastParsedUtc = ReadDateTime(reader, "LastParsedUtc") ?? default,
        IdentificationName = ReadString(reader, "IdentName"),
        IdentificationType = ReadString(reader, "IdentType"),
        Version = ReadString(reader, "IdentVersion"),
        ModificationDate = ReadString(reader, "IdentModificationDate"),
        SecurityClassification = ReadString(reader, "IdentSecurityClassification"),
        Purpose = ReadString(reader, "IdentPurpose"),
        ApplicationDomain = ReadString(reader, "IdentApplicationDomain"),
        Description = ReadString(reader, "IdentDescription"),
        ObjectClassCount = ReadInt32(reader, "ObjectClassCount"),
        AttributeCount = ReadInt32(reader, "AttributeCount"),
        InteractionClassCount = ReadInt32(reader, "InteractionClassCount"),
        ParameterCount = ReadInt32(reader, "ParameterCount"),
        DataTypeCount = ReadInt32(reader, "DataTypeCount"),
        DimensionCount = ReadInt32(reader, "DimensionCount"),
        ErrorCount = ReadInt32(reader, "ErrorCount"),
        WarningCount = ReadInt32(reader, "WarningCount"),
    };

    /// <summary>Fills the four <see cref="FomNode"/> columns every element table shares.</summary>
    private static T ReadNode<T>(SqliteDataReader reader, T node) where T : FomNode
    {
        node.Name = ReadString(reader, "Name") ?? "";
        node.QualifiedName = ReadString(reader, "QualifiedName") ?? "";
        node.Semantics = ReadString(reader, "Semantics");
        node.Notes = ReadString(reader, "NoteRefs");
        return node;
    }

    private static string? ReadString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static long ReadInt64(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? 0L : reader.GetInt64(ordinal);
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static int ReadInt32(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
    }

    private static int? ReadNullableInt32(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    /// <summary>Reads a round-trip ISO-8601 timestamp back as a UTC <see cref="DateTime"/>.</summary>
    private static DateTime? ReadDateTime(SqliteDataReader reader, string column) =>
        ParseTimestamp(ReadString(reader, column));

    /// <summary>Parses a stored timestamp, treating anything without a zone as already UTC.</summary>
    private static DateTime? ParseTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            return null;

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }

    /// <summary>
    /// The date this file was first registered, when it already is. Null when it is new here, which
    /// is what makes re-registration keep the original date and a first registration take today's.
    /// </summary>
    private DateTime? ReadFirstRegisteredUtc(SqliteTransaction transaction, string fullPath)
    {
        using var command = CreateCommand(transaction,
            "SELECT RegisteredUtc FROM Foms WHERE FilePath = @path COLLATE NOCASE LIMIT 1;",
            new (string, object?)[] { ("@path", fullPath) });

        var scalar = command.ExecuteScalar();
        return scalar is null or DBNull
            ? null
            : ParseTimestamp(Convert.ToString(scalar, CultureInfo.InvariantCulture));
    }

    /// <summary>Unrecognised stored values degrade to <see cref="FomStandard.Unknown"/>.</summary>
    private static FomStandard ReadStandard(SqliteDataReader reader, string column)
    {
        var value = ReadInt32(reader, column);
        return Enum.IsDefined(typeof(FomStandard), value) ? (FomStandard)value : FomStandard.Unknown;
    }

    /// <summary>Unrecognised stored values degrade to <see cref="DiagnosticSeverity.Info"/>.</summary>
    private static DiagnosticSeverity ReadSeverity(SqliteDataReader reader, string column)
    {
        var value = ReadInt32(reader, column);
        return Enum.IsDefined(typeof(DiagnosticSeverity), value) ? (DiagnosticSeverity)value : DiagnosticSeverity.Info;
    }

    // ---------------------------------------------------------------------------------------
    // Command plumbing
    // ---------------------------------------------------------------------------------------

    /// <summary>Runs a parameterised statement outside any explicit transaction.</summary>
    private void Execute(string sql, params (string Name, object? Value)[] parameters)
        => Execute((SqliteTransaction?)null, sql, parameters);

    /// <summary>Runs a parameterised statement, optionally enlisted in <paramref name="transaction"/>.</summary>
    private void Execute(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    /// <summary>Runs a parameterised query, handing every row to <paramref name="handler"/>.</summary>
    private void Query(string sql, Action<SqliteDataReader> handler, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(null, sql, parameters);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            handler(reader);
    }

    /// <summary>
    /// Inserts one row and returns its generated Id. Column names are compile-time literals;
    /// only values are ever parameterised.
    /// </summary>
    private long InsertWithId(SqliteTransaction transaction, string table, params (string Column, object? Value)[] columns)
    {
        var names = new StringBuilder();
        var placeholders = new StringBuilder();
        var parameters = new (string Name, object? Value)[columns.Length];

        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0)
            {
                names.Append(", ");
                placeholders.Append(", ");
            }

            var placeholder = "@p" + i.ToString(CultureInfo.InvariantCulture);
            names.Append(columns[i].Column);
            placeholders.Append(placeholder);
            parameters[i] = (placeholder, columns[i].Value);
        }

        var sql = $"INSERT INTO {table} ({names}) VALUES ({placeholders}); SELECT last_insert_rowid();";
        using var command = CreateCommand(transaction, sql, parameters);
        var scalar = command.ExecuteScalar();
        return scalar is null || scalar is DBNull ? 0L : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private SqliteCommand CreateCommand(SqliteTransaction? transaction, string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, ToDbValue(value));
        return command;
    }

    /// <summary>
    /// Normalises a CLR value for SQLite: nulls become <see cref="DBNull"/>, timestamps become
    /// round-trip ISO-8601 UTC strings, enums and booleans become integers.
    /// </summary>
    private static object ToDbValue(object? value) => value switch
    {
        null => DBNull.Value,
        DateTime timestamp => FormatUtc(timestamp),
        Enum enumeration => Convert.ToInt64(enumeration, CultureInfo.InvariantCulture),
        bool flag => flag ? 1L : 0L,
        _ => value,
    };

    /// <summary>Formats a timestamp as round-trippable UTC; unspecified kinds are taken to be UTC already.</summary>
    private static string FormatUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        return utc.ToString("O", CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------------------------------
    // Paths and lifetime
    // ---------------------------------------------------------------------------------------

    /// <summary>Absolute, comparable form of a source path; returns the input when it cannot be rooted.</summary>
    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return path;
        }
    }

    /// <summary>File name component of a path, or the whole path when it has no separator.</summary>
    private static string SafeFileName(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc />
    /// <remarks>
    /// Safe to call while another thread is mid-operation: the gate makes this wait for that
    /// operation to finish, and every later call sees <see cref="_disposed"/> and throws
    /// <see cref="ObjectDisposedException"/> from <see cref="ThrowIfDisposed"/> rather than running
    /// against a closed connection.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _connection.Dispose();
        }
    }
}
