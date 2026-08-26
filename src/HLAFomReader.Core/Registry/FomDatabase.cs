using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace HLAFomReader.Core.Registry;

/// <summary>
/// Owns the physical SQLite store: where it lives, how it is opened, and what its
/// schema looks like. A registered FOM is written relationally — one table per OMT
/// table — so the store can be queried, not just replayed.
/// </summary>
public static class FomDatabase
{
    /// <summary>Schema version written by this build. Bump it together with a migration step.</summary>
    public const int CurrentSchemaVersion = 5;

    /// <summary>File name of the default store.</summary>
    private const string DatabaseFileName = "hlafomreader.db";

    /// <summary>
    /// <c>hlafomreader.db</c> in the folder the executable was launched from. An existing file there
    /// is reused; otherwise it is created on the first <see cref="Open"/>.
    /// </summary>
    /// <remarks>
    /// The store used to live under <c>%APPDATA%\HLAFomReader</c>. It now sits beside the executable,
    /// which keeps a portable single-file build self-contained: copy the folder and the registry
    /// travels with it, with no per-profile state left behind.
    /// <para>
    /// "Beside the executable" means <see cref="AppConfig.AppDirectory"/>, not
    /// <c>AppContext.BaseDirectory</c>. In a self-extracting single-file build the latter is a
    /// randomly named folder under %TEMP% that changes every launch, so a database created there
    /// would vanish between runs.
    /// </para>
    /// </remarks>
    public static string GetDefaultDatabasePath()
        => Path.Combine(AppConfig.AppDirectory, DatabaseFileName);

    /// <summary>
    /// The default store's path — see <see cref="GetDefaultDatabasePath"/>, which this simply
    /// forwards to. Retained as a property because existing callers are written against it.
    /// </summary>
    public static string DefaultDatabasePath => GetDefaultDatabasePath();

    /// <summary>How long a command waits for a busy database before failing, in seconds.</summary>
    private const int CommandTimeoutSeconds = 30;

    private static bool _providerReady;

    /// <summary>
    /// Selects the native SQLite build once per process.
    /// </summary>
    /// <remarks>
    /// The app references Microsoft.Data.Sqlite.<b>Core</b> plus SQLitePCLRaw.bundle_e_sqlcipher
    /// rather than the all-in-one Microsoft.Data.Sqlite package, because only the SQLCipher build
    /// can open an encrypted database. Core does not pick a provider for you, so this must run
    /// before the first connection or every open fails with "You need to call SQLitePCL.raw.SetProvider".
    /// </remarks>
    internal static void EnsureProvider()
    {
        if (_providerReady) return;
        SQLitePCL.Batteries_V2.Init();
        _providerReady = true;
    }

    /// <summary>
    /// Opens (creating the file if needed) the store at <paramref name="path"/> with foreign keys
    /// enforced and write-ahead logging enabled. The caller owns the returned connection.
    /// </summary>
    /// <param name="path">Absolute or relative path to the database file.</param>
    /// <param name="password">
    /// SQLCipher key used to unlock the file. Null or empty opens a plaintext database, which
    /// remains the default.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or blank.</exception>
    /// <exception cref="SqliteException">
    /// The file is encrypted and <paramref name="password"/> is wrong or missing (or the file is
    /// plaintext and a password was supplied).
    /// </exception>
    public static SqliteConnection Open(string path, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A database path is required.", nameof(path));

        EnsureProvider();

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Deliberately the default (private) cache. Shared cache buys nothing for an application
        // that holds a single connection, and it costs plenty: it swaps SQLite's file-level locking
        // for table-level locks that SQLITE_BUSY handling cannot retry out of, so contention turns
        // into "database table is locked" and hangs rather than a brief wait. Do not re-add
        // Cache = SqliteCacheMode.Shared.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // Pooling and per-file keys must never be mixed: a pooled connection goes back to the
            // pool with its SQLCipher key still applied, and can then be handed to a caller that
            // asked for a different file or a different password. Always off, encrypted or not,
            // so there is only one behaviour to reason about.
            Pooling = false,
        };

        if (!string.IsNullOrEmpty(password))
            builder.Password = password;

        var connection = new SqliteConnection(builder.ConnectionString)
        {
            // A genuinely busy database should surface a clear SqliteException at a known bound
            // instead of appearing to hang forever.
            DefaultTimeout = CommandTimeoutSeconds,
        };

        try
        {
            connection.Open();

            // SQLCipher validates the key lazily: Open() succeeds with the wrong password and the
            // failure only appears at whatever statement happens to run first. Force a read here so
            // a bad password is a SqliteException from Open, and so the pragmas below are never run
            // against a file we cannot actually decrypt.
            Validate(connection);

            // All three pragmas are connection-scoped (journal_mode persists in the file) and must
            // run outside any transaction, hence before EnsureSchema.
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                pragma.ExecuteNonQuery();
            }

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode = WAL;";
                pragma.ExecuteNonQuery();
            }

            // Wait rather than fail immediately when another writer (or another process sharing the
            // file) holds the lock; SQLite retries internally for up to this many milliseconds.
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 5000;";
                pragma.ExecuteNonQuery();
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Tests whether the store at <paramref name="path"/> can be opened and read with
    /// <paramref name="password"/>, without creating anything or leaving a connection behind.
    /// </summary>
    /// <param name="path">Path to an existing database file.</param>
    /// <param name="password">SQLCipher key to try; null or empty tests for a plaintext file.</param>
    /// <returns>True when the file exists and the key decrypts it.</returns>
    public static bool CanOpen(string path, string? password)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            EnsureProvider();

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                // ReadWrite rather than ReadWriteCreate: a probe must report failure for a missing
                // file, not quietly conjure an empty database and then call that success.
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            };

            if (!string.IsNullOrEmpty(password))
                builder.Password = password;

            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            Validate(connection);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is an existing, unencrypted SQLite database — that is,
    /// one readable with no key at all.
    /// </summary>
    public static bool IsPlaintextDatabase(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        return CanOpen(path, password: null);
    }

    /// <summary>
    /// True when <paramref name="path"/> exists but is not readable without a key, i.e. it is
    /// SQLCipher-encrypted and the user must be prompted for a password.
    /// </summary>
    public static bool IsEncrypted(string path) => File.Exists(path) && !IsPlaintextDatabase(path);

    /// <summary>
    /// Encrypts an existing plaintext store in place under <paramref name="password"/>. The original
    /// file is replaced only once the encrypted copy has been written successfully.
    /// </summary>
    /// <param name="path">Path to the plaintext database.</param>
    /// <param name="password">The SQLCipher key to apply. Must not be empty.</param>
    public static void EncryptPlaintext(string path, string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("A password is required to encrypt the database.", nameof(password));

        Export(path, sourcePassword: null, targetPassword: password);
    }

    /// <summary>
    /// Decrypts an encrypted store in place back to plaintext. The original file is replaced only
    /// once the plaintext copy has been written successfully.
    /// </summary>
    /// <param name="path">Path to the encrypted database.</param>
    /// <param name="password">The current SQLCipher key, needed to read the source.</param>
    public static void DecryptToPlaintext(string path, string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("The current password is required to decrypt the database.", nameof(password));

        Export(path, sourcePassword: password, targetPassword: null);
    }

    /// <summary>
    /// Re-keys an already-encrypted store from <paramref name="oldPassword"/> to
    /// <paramref name="newPassword"/> via SQLCipher's <c>PRAGMA rekey</c>, which rewrites every
    /// page in place.
    /// </summary>
    /// <param name="path">Path to the encrypted database.</param>
    /// <param name="oldPassword">The current key. A wrong value throws before anything is written.</param>
    /// <param name="newPassword">The replacement key. Must not be empty.</param>
    /// <exception cref="SqliteException"><paramref name="oldPassword"/> does not open the file.</exception>
    public static void ChangePassword(string path, string oldPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(newPassword))
            throw new ArgumentException("A new password is required.", nameof(newPassword));

        EnsureProvider();

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Password = oldPassword,
            Pooling = false,
        }.ConnectionString))
        {
            connection.Open();
            Validate(connection); // Fails here, before any write, when oldPassword is wrong.

            // The store runs in WAL mode, so committed pages may still be sitting in the -wal
            // sidecar under the OLD key. Fold them into the main file first: rekey only rewrites
            // the database itself, and a sidecar left holding old-key frames would be unreadable
            // afterwards.
            Checkpoint(connection);

            using (var rekey = connection.CreateCommand())
            {
                // PRAGMA takes no bound parameters, so the key has to be inlined; doubling any
                // apostrophe keeps a password like O'Brien from terminating the literal early.
                rekey.CommandText = $"PRAGMA rekey = '{newPassword.Replace("'", "''")}';";
                rekey.ExecuteNonQuery();
            }

            // And again afterwards, so the newly re-keyed pages are in the main file rather than
            // in a sidecar that the cleanup below is about to remove.
            Checkpoint(connection);
        }

        DeleteSidecars(path);
    }

    /// <summary>
    /// Copies the store to a fresh file in the opposite encryption state via SQLCipher's
    /// <c>sqlcipher_export</c>, then swaps it over the original.
    /// </summary>
    /// <param name="path">The database to convert.</param>
    /// <param name="sourcePassword">Key that opens the existing file, or null when it is plaintext.</param>
    /// <param name="targetPassword">Key for the new file, or null to produce a plaintext one.</param>
    private static void Export(string path, string? sourcePassword, string? targetPassword)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A database path is required.", nameof(path));

        EnsureProvider();

        // Both scratch names use a dot separator on purpose. SQLite's own sidecars are "<path>-wal"
        // and "<path>-shm", so a dash-separated suffix here would look like a sidecar of the main
        // database — to SQLite and to the cleanup below alike.
        var temporary = path + ".export.tmp";
        var backup = path + ".export.bak";

        if (File.Exists(temporary))
            File.Delete(temporary);

        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        };

        if (!string.IsNullOrEmpty(sourcePassword))
            sourceBuilder.Password = sourcePassword;

        using (var source = new SqliteConnection(sourceBuilder.ConnectionString))
        {
            source.Open();
            Validate(source); // Fails here, before anything is created, when sourcePassword is wrong.

            // The store runs in WAL mode. Committed pages can still live in the -wal sidecar, and
            // that sidecar is keyed with the SOURCE password; once the file swap below replaces the
            // main database, a leftover sidecar would be stale at best and undecryptable at worst.
            // Checkpointing folds everything back into the file being exported.
            Checkpoint(source);

            using (var attach = source.CreateCommand())
            {
                // KEY '' produces a plaintext target; a non-empty key produces an encrypted one.
                attach.CommandText = "ATTACH DATABASE $path AS export KEY $key;";
                attach.Parameters.AddWithValue("$path", temporary);
                attach.Parameters.AddWithValue("$key", targetPassword ?? "");
                attach.ExecuteNonQuery();
            }

            using (var export = source.CreateCommand())
            {
                export.CommandText = "SELECT sqlcipher_export('export');";
                export.ExecuteNonQuery();
            }

            using (var detach = source.CreateCommand())
            {
                detach.CommandText = "DETACH DATABASE export;";
                detach.ExecuteNonQuery();
            }
        }

        // Move rather than overwrite, so the original survives until the replacement is safely in
        // place; only then is the backup discarded.
        if (File.Exists(backup))
            File.Delete(backup);

        File.Move(path, backup);
        File.Move(temporary, path);
        File.Delete(backup);

        // The main database is now a different file with a different key, so any sidecar left over
        // from before the swap describes a database that no longer exists. Drop both, along with
        // anything the temporary file accumulated.
        DeleteSidecars(path);
        DeleteSidecars(temporary);
    }

    /// <summary>
    /// Folds the write-ahead log back into the database file and truncates it, so no committed page
    /// is left in a sidecar while the file itself is being replaced or re-keyed.
    /// </summary>
    private static void Checkpoint(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes the <c>-wal</c> and <c>-shm</c> files belonging to <paramref name="path"/>. Only ever
    /// called after a checkpoint, so nothing committed is discarded.
    /// </summary>
    private static void DeleteSidecars(string path)
    {
        foreach (var sidecar in new[] { path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(sidecar))
                    File.Delete(sidecar);
            }
            catch (IOException)
            {
                // Another process may still have the shared-memory file mapped. SQLite recreates or
                // discards both sidecars on the next open, so a failure here is not worth surfacing.
            }
            catch (UnauthorizedAccessException)
            {
                // Same reasoning: cleanup is a tidiness measure, never a correctness one.
            }
        }
    }

    /// <summary>
    /// Forces a read of the schema. SQLCipher applies the key lazily, so this is what turns a wrong
    /// password into a <see cref="SqliteException"/> at a predictable place.
    /// </summary>
    private static void Validate(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT count(*) FROM sqlite_master;";
        check.ExecuteScalar();
    }

    /// <summary>
    /// Creates every table and index that is missing and stamps <see cref="CurrentSchemaVersion"/>.
    /// Safe to call on every start-up; existing data is never rewritten.
    /// </summary>
    /// <param name="connection">An open connection, typically from <see cref="Open"/>.</param>
    /// <exception cref="InvalidOperationException">
    /// The file was written by a newer build and cannot be read by this one.
    /// </exception>
    public static void EnsureSchema(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var transaction = connection.BeginTransaction();

        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = SchemaSql;
            create.ExecuteNonQuery();
        }

        var storedVersion = ReadSchemaVersion(connection, transaction);
        if (storedVersion is null)
        {
            StampSchemaVersion(connection, transaction, CurrentSchemaVersion);
        }
        else if (storedVersion.Value < CurrentSchemaVersion)
        {
            Migrate(connection, transaction, storedVersion.Value);
            StampSchemaVersion(connection, transaction, CurrentSchemaVersion);
        }
        else if (storedVersion.Value > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"The database was created by a newer version of HLAFomReader (schema {storedVersion.Value}; this build understands {CurrentSchemaVersion}).");
        }

        transaction.Commit();
    }

    /// <summary>Reads the single SchemaVersion row, or null when the table is still empty.</summary>
    private static int? ReadSchemaVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Version FROM SchemaVersion LIMIT 1;";
        var scalar = command.ExecuteScalar();
        if (scalar is null || scalar is DBNull)
            return null;

        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    /// <summary>Replaces the SchemaVersion row so the table keeps exactly one row.</summary>
    private static void StampSchemaVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM SchemaVersion; INSERT INTO SchemaVersion (Version) VALUES (@version);";
        command.Parameters.AddWithValue("@version", version);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Upgrades an older store in place. Each future version adds one step here, in ascending order;
    /// the guard at the end fails loudly if a version is skipped.
    /// </summary>
    /// <param name="connection">The open connection being upgraded.</param>
    /// <param name="transaction">The transaction wrapping <see cref="EnsureSchema"/>.</param>
    /// <param name="fromVersion">The version currently stamped in the file.</param>
    private static void Migrate(SqliteConnection connection, SqliteTransaction transaction, int fromVersion)
    {
        var version = fromVersion;

        if (version == 1)
        {
            ApplyV2(connection, transaction);
            version = 2;
        }

        if (version == 2)
        {
            ApplyV3(connection, transaction);
            version = 3;
        }

        if (version == 3)
        {
            ApplyV4(connection, transaction);
            version = 4;
        }

        if (version == 4)
        {
            ApplyV5(connection, transaction);
            version = 5;
        }

        // Insertion point for schema 6 and beyond, e.g.:
        //   if (version == 5) { ApplyV6(connection, transaction); version = 6; }

        if (version != CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"No migration path from schema version {fromVersion} to {CurrentSchemaVersion}.");
        }
    }

    /// <summary>
    /// Schema 2: the five HLA 1.3 OMT value-description columns on attributes and parameters.
    /// </summary>
    /// <remarks>
    /// <c>CREATE TABLE IF NOT EXISTS</c> leaves an existing table exactly as it was, so a version-1
    /// file needs the columns added explicitly. All five are nullable and carry no default, which is
    /// the one shape SQLite can append by rewriting only the table header — the existing rows are
    /// left untouched and read back as NULL, which is precisely what a 1516 document means by them.
    /// </remarks>
    private static void ApplyV2(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var table in new[] { "ObjectAttributes", "InteractionParameters" })
        {
            foreach (var column in new[] { "Cardinality", "Units", "Resolution", "Accuracy", "AccuracyCondition" })
                AddColumnIfMissing(connection, transaction, table, column, "TEXT");
        }
    }

    /// <summary>
    /// Schema 3: the second source file recorded against a registered FOM.
    /// </summary>
    /// <remarks>
    /// An HLA 1.3 federation is described by a pair — the <c>.fed</c> the RTI loads, which carries the
    /// class structure but no datatypes at all, and the <c>.omt</c> document, which carries the
    /// attribute table and the datatype tables the RTI never reads. Neither file is the FOM on its
    /// own, so an entry built from both has to remember both: <c>CompanionPath</c> so it can be
    /// re-parsed, <c>CompanionHash</c> so a change to either half is detected. Both stay NULL for a
    /// single-file entry, which is every IEEE 1516 (Evolved) FOM and every pre-existing row.
    /// </remarks>
    private static void ApplyV3(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var column in new[] { "CompanionPath", "CompanionHash" })
            AddColumnIfMissing(connection, transaction, "Foms", column, "TEXT");
    }

    /// <summary>
    /// Schema 4: which registered FOMs an entry was composed from.
    /// </summary>
    /// <remarks>
    /// <c>CREATE TABLE IF NOT EXISTS</c> in <see cref="SchemaSql"/> covers a fresh file; a version-3
    /// file reaches the same shape by running that statement here. An older store has no composed
    /// entries in it, so there is nothing to backfill — every existing row is a single-file FOM and
    /// simply has no dependencies, which is exactly what an empty table says.
    /// </remarks>
    private static void ApplyV4(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = FomDependenciesSql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Schema 5: what a compiled FOM was built from, replacing the links to what it was built out of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Schema 4 recorded a composed entry as a set of links to other registered FOMs, because the
    /// merged model existed only in memory and the modules it came from were the only durable record
    /// of it. A compiled FOM is now written out as a file and registered as that file, so the model
    /// no longer depends on anything: the links describe a relationship that has stopped existing,
    /// and <c>ON DELETE RESTRICT</c> on them would refuse to unregister a module that a
    /// self-contained file merely happens to have been built from. The table goes.
    /// </para>
    /// <para>
    /// What is worth keeping is the provenance, which is one ordered list of file names rather than a
    /// set of foreign keys — so it becomes a column on the entry. Existing rows read back NULL, which
    /// says "not compiled", and is true of every entry registered before this.
    /// </para>
    /// <para>
    /// Nothing is migrated across from the old table. It was never written to: the only caller passed
    /// no dependencies, so every store in the field has it empty.
    /// </para>
    /// </remarks>
    private static void ApplyV5(SqliteConnection connection, SqliteTransaction transaction)
    {
        AddColumnIfMissing(connection, transaction, "Foms", "ComposedFrom", "TEXT");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DROP INDEX IF EXISTS IX_FomDependencies_DependsOn;"
            + "DROP TABLE IF EXISTS FomDependencies;";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds one column unless <paramref name="table"/> already has it.
    /// </summary>
    /// <remarks>
    /// SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>, and a duplicate add is an error rather than a
    /// no-op — so the column list is checked first. That also makes the step safe to re-run against
    /// a file whose stamped version and real shape have drifted apart.
    /// </remarks>
    private static void AddColumnIfMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string declaredType)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = @column;";
            probe.Parameters.AddWithValue("@table", table);
            probe.Parameters.AddWithValue("@column", column);

            var scalar = probe.ExecuteScalar();
            if (scalar is not null && scalar is not DBNull && Convert.ToInt32(scalar, CultureInfo.InvariantCulture) > 0)
                return;
        }

        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;

        // Table and column names are compile-time constants from the ApplyVn steps, never user input; DDL
        // cannot take bound identifiers, so they have to be interpolated.
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaredType};";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// The composition links schema 4 added and schema 5 dropped, kept only so a version-3 file can
    /// still climb the ladder through the shape every version-4 file in the field actually has.
    /// </summary>
    /// <remarks>
    /// Nothing creates this table any more. It recorded which registered FOMs an entry had been
    /// merged from, which stopped meaning anything once a compiled FOM became a file of its own —
    /// see <c>ApplyV5</c>. It was never written to either: the only caller passed no dependencies.
    /// </remarks>
    private const string FomDependenciesSql = """
        CREATE TABLE IF NOT EXISTS FomDependencies (
            FomId           INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            DependsOnFomId  INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE RESTRICT,
            Ordinal         INTEGER NOT NULL,
            DeclaredAs      TEXT,
            PRIMARY KEY (FomId, DependsOnFomId)
        );

        CREATE INDEX IF NOT EXISTS IX_FomDependencies_DependsOn ON FomDependencies (DependsOnFomId);

        """;

    /// <summary>
    /// The complete <see cref="CurrentSchemaVersion"/> schema, which a fresh file is created at
    /// directly — <see cref="Migrate"/> exists only to bring an older file up to the same shape.
    /// Every child table cascades from <c>Foms</c> and carries an <c>Ordinal</c> so document order
    /// survives a round trip.
    /// </summary>
    /// <remarks>
    /// No SQL comments inside a <c>CREATE TABLE</c>, however much a column would benefit from one.
    /// SQLite keeps the statement's original text and re-parses it to perform an
    /// <c>ALTER TABLE … DROP COLUMN</c>, and a comment in the column list makes the rewritten text
    /// unparseable — the drop fails with "incomplete input" and takes the migration down with it.
    /// Column notes belong in the C# that reads and writes them.
    /// </remarks>
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS SchemaVersion (
            Version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Foms (
            Id                          INTEGER PRIMARY KEY AUTOINCREMENT,
            Key                         TEXT    NOT NULL UNIQUE,
            DisplayName                 TEXT    NOT NULL,
            FilePath                    TEXT    NOT NULL,
            FileName                    TEXT    NOT NULL,
            Standard                    INTEGER NOT NULL,
            SourceNamespace             TEXT,
            FileHash                    TEXT,
            CompanionPath               TEXT,
            CompanionHash               TEXT,
            FileSizeBytes               INTEGER NOT NULL DEFAULT 0,
            FileModifiedUtc             TEXT,
            RegisteredUtc               TEXT    NOT NULL,
            LastParsedUtc               TEXT    NOT NULL,
            IdentName                   TEXT,
            IdentType                   TEXT,
            IdentVersion                TEXT,
            IdentModificationDate       TEXT,
            IdentSecurityClassification TEXT,
            IdentReleaseRestriction     TEXT,
            IdentPurpose                TEXT,
            IdentApplicationDomain      TEXT,
            IdentDescription            TEXT,
            IdentUseLimitation          TEXT,
            IdentReference              TEXT,
            IdentOther                  TEXT,
            IdentGlyph                  TEXT,
            ObjectClassCount            INTEGER NOT NULL DEFAULT 0,
            AttributeCount              INTEGER NOT NULL DEFAULT 0,
            InteractionClassCount       INTEGER NOT NULL DEFAULT 0,
            ParameterCount              INTEGER NOT NULL DEFAULT 0,
            DataTypeCount               INTEGER NOT NULL DEFAULT 0,
            DimensionCount              INTEGER NOT NULL DEFAULT 0,
            ErrorCount                  INTEGER NOT NULL DEFAULT 0,
            WarningCount                INTEGER NOT NULL DEFAULT 0,
            ComposedFrom                TEXT
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Foms_FilePath ON Foms (FilePath COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS IX_Foms_RegisteredUtc ON Foms (RegisteredUtc DESC);

        CREATE TABLE IF NOT EXISTS FomIdentificationValues (
            Id      INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId   INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            Kind    TEXT    NOT NULL,
            Ordinal INTEGER NOT NULL DEFAULT 0,
            Value   TEXT
        );

        CREATE INDEX IF NOT EXISTS IX_FomIdentificationValues_FomId ON FomIdentificationValues (FomId);

        CREATE TABLE IF NOT EXISTS ObjectClasses (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId         INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            ParentId      INTEGER NULL REFERENCES ObjectClasses (Id) ON DELETE CASCADE,
            Name          TEXT,
            QualifiedName TEXT,
            Sharing       TEXT,
            Semantics     TEXT,
            NoteRefs      TEXT,
            Ordinal       INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_ObjectClasses_FomId ON ObjectClasses (FomId);
        CREATE INDEX IF NOT EXISTS IX_ObjectClasses_ParentId ON ObjectClasses (ParentId);

        CREATE TABLE IF NOT EXISTS ObjectAttributes (
            Id                INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId             INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            ObjectClassId     INTEGER NOT NULL REFERENCES ObjectClasses (Id) ON DELETE CASCADE,
            Name              TEXT,
            QualifiedName     TEXT,
            DataType          TEXT,
            Cardinality       TEXT,
            Units             TEXT,
            Resolution        TEXT,
            Accuracy          TEXT,
            AccuracyCondition TEXT,
            UpdateType        TEXT,
            UpdateCondition   TEXT,
            Ownership         TEXT,
            Sharing           TEXT,
            Transportation    TEXT,
            "Order"           TEXT,
            RoutingSpace      TEXT,
            Semantics         TEXT,
            NoteRefs          TEXT,
            Ordinal           INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_ObjectAttributes_FomId ON ObjectAttributes (FomId);
        CREATE INDEX IF NOT EXISTS IX_ObjectAttributes_ObjectClassId ON ObjectAttributes (ObjectClassId);

        CREATE TABLE IF NOT EXISTS AttributeDimensions (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            AttributeId   INTEGER NOT NULL REFERENCES ObjectAttributes (Id) ON DELETE CASCADE,
            Ordinal       INTEGER NOT NULL DEFAULT 0,
            DimensionName TEXT
        );

        CREATE INDEX IF NOT EXISTS IX_AttributeDimensions_AttributeId ON AttributeDimensions (AttributeId);

        CREATE TABLE IF NOT EXISTS InteractionClasses (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId          INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            ParentId       INTEGER NULL REFERENCES InteractionClasses (Id) ON DELETE CASCADE,
            Name           TEXT,
            QualifiedName  TEXT,
            Sharing        TEXT,
            Transportation TEXT,
            "Order"        TEXT,
            RoutingSpace   TEXT,
            Semantics      TEXT,
            NoteRefs       TEXT,
            Ordinal        INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_InteractionClasses_FomId ON InteractionClasses (FomId);
        CREATE INDEX IF NOT EXISTS IX_InteractionClasses_ParentId ON InteractionClasses (ParentId);

        CREATE TABLE IF NOT EXISTS InteractionDimensions (
            Id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            InteractionClassId   INTEGER NOT NULL REFERENCES InteractionClasses (Id) ON DELETE CASCADE,
            Ordinal              INTEGER NOT NULL DEFAULT 0,
            DimensionName        TEXT
        );

        CREATE INDEX IF NOT EXISTS IX_InteractionDimensions_InteractionClassId ON InteractionDimensions (InteractionClassId);

        CREATE TABLE IF NOT EXISTS InteractionParameters (
            Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId              INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            InteractionClassId INTEGER NOT NULL REFERENCES InteractionClasses (Id) ON DELETE CASCADE,
            Name               TEXT,
            QualifiedName      TEXT,
            DataType           TEXT,
            Cardinality        TEXT,
            Units              TEXT,
            Resolution         TEXT,
            Accuracy           TEXT,
            AccuracyCondition  TEXT,
            Semantics          TEXT,
            NoteRefs           TEXT,
            Ordinal            INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_InteractionParameters_FomId ON InteractionParameters (FomId);
        CREATE INDEX IF NOT EXISTS IX_InteractionParameters_InteractionClassId ON InteractionParameters (InteractionClassId);

        CREATE TABLE IF NOT EXISTS DataTypes (
            Id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId                INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            Kind                 TEXT    NOT NULL,
            Name                 TEXT,
            QualifiedName        TEXT,
            Size                 TEXT,
            Interpretation       TEXT,
            Endian               TEXT,
            Encoding             TEXT,
            Representation       TEXT,
            Units                TEXT,
            Resolution           TEXT,
            Accuracy             TEXT,
            ElementDataType      TEXT,
            Cardinality          TEXT,
            Discriminant         TEXT,
            DiscriminantDataType TEXT,
            IncludeRef           TEXT,
            Semantics            TEXT,
            NoteRefs             TEXT,
            Ordinal              INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_DataTypes_FomId ON DataTypes (FomId);

        CREATE TABLE IF NOT EXISTS DataTypeMembers (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            DataTypeId    INTEGER NOT NULL REFERENCES DataTypes (Id) ON DELETE CASCADE,
            Kind          TEXT    NOT NULL,
            Name          TEXT,
            QualifiedName TEXT,
            MemberValues  TEXT,
            DataType      TEXT,
            Enumerator    TEXT,
            Semantics     TEXT,
            NoteRefs      TEXT,
            Ordinal       INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_DataTypeMembers_DataTypeId ON DataTypeMembers (DataTypeId);

        CREATE TABLE IF NOT EXISTS Dimensions (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId         INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            Name          TEXT,
            QualifiedName TEXT,
            DataType      TEXT,
            UpperBound    TEXT,
            Normalization TEXT,
            Value         TEXT,
            Semantics     TEXT,
            NoteRefs      TEXT,
            Ordinal       INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_Dimensions_FomId ON Dimensions (FomId);

        CREATE TABLE IF NOT EXISTS RoutingSpaces (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId         INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            Name          TEXT,
            QualifiedName TEXT,
            Semantics     TEXT,
            NoteRefs      TEXT,
            Ordinal       INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_RoutingSpaces_FomId ON RoutingSpaces (FomId);

        CREATE TABLE IF NOT EXISTS RoutingSpaceDimensions (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            RoutingSpaceId INTEGER NOT NULL REFERENCES RoutingSpaces (Id) ON DELETE CASCADE,
            Ordinal        INTEGER NOT NULL DEFAULT 0,
            Name           TEXT
        );

        CREATE INDEX IF NOT EXISTS IX_RoutingSpaceDimensions_RoutingSpaceId ON RoutingSpaceDimensions (RoutingSpaceId);

        CREATE TABLE IF NOT EXISTS Transportations (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId         INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            Name          TEXT,
            QualifiedName TEXT,
            Reliable      TEXT,
            Semantics     TEXT,
            NoteRefs      TEXT,
            Ordinal       INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_Transportations_FomId ON Transportations (FomId);

        CREATE TABLE IF NOT EXISTS Synchronizations (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId         INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            Name          TEXT,
            QualifiedName TEXT,
            Capability    TEXT,
            DataType      TEXT,
            Semantics     TEXT,
            NoteRefs      TEXT,
            Ordinal       INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_Synchronizations_FomId ON Synchronizations (FomId);

        CREATE TABLE IF NOT EXISTS UpdateRates (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId         INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            Name          TEXT,
            QualifiedName TEXT,
            Rate          TEXT,
            Semantics     TEXT,
            NoteRefs      TEXT,
            Ordinal       INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_UpdateRates_FomId ON UpdateRates (FomId);

        CREATE TABLE IF NOT EXISTS Switches (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId         INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            Name          TEXT,
            QualifiedName TEXT,
            IsEnabled     TEXT,
            ResignSwitch  TEXT,
            Semantics     TEXT,
            NoteRefs      TEXT,
            Ordinal       INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_Switches_FomId ON Switches (FomId);

        CREATE TABLE IF NOT EXISTS Tags (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId         INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            Name          TEXT,
            QualifiedName TEXT,
            DataType      TEXT,
            Semantics     TEXT,
            NoteRefs      TEXT,
            Ordinal       INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_Tags_FomId ON Tags (FomId);

        CREATE TABLE IF NOT EXISTS FomNotes (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId         INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            Name          TEXT,
            QualifiedName TEXT,
            Label         TEXT,
            Text          TEXT,
            Semantics     TEXT,
            Ordinal       INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_FomNotes_FomId ON FomNotes (FomId);

        CREATE TABLE IF NOT EXISTS TimeRepresentation (
            FomId              INTEGER PRIMARY KEY REFERENCES Foms (Id) ON DELETE CASCADE,
            TimeStampDataType  TEXT,
            TimeStampSemantics TEXT,
            LookaheadDataType  TEXT,
            LookaheadSemantics TEXT
        );

        CREATE TABLE IF NOT EXISTS Diagnostics (
            Id       INTEGER PRIMARY KEY AUTOINCREMENT,
            FomId    INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            Severity INTEGER NOT NULL,
            Message  TEXT    NOT NULL,
            Line     INTEGER NULL,
            Path     TEXT    NULL,
            Ordinal  INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_Diagnostics_FomId ON Diagnostics (FomId);

        CREATE TABLE IF NOT EXISTS Comparisons (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            LeftFomId     INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            RightFomId    INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
            CreatedUtc    TEXT    NOT NULL,
            OptionsJson   TEXT    NOT NULL,
            AddedCount    INTEGER NOT NULL DEFAULT 0,
            RemovedCount  INTEGER NOT NULL DEFAULT 0,
            ModifiedCount INTEGER NOT NULL DEFAULT 0,
            UnchangedCount INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_Comparisons_LeftFomId ON Comparisons (LeftFomId);
        CREATE INDEX IF NOT EXISTS IX_Comparisons_RightFomId ON Comparisons (RightFomId);
        CREATE INDEX IF NOT EXISTS IX_Comparisons_CreatedUtc ON Comparisons (CreatedUtc DESC);
        """;
}
