using System;
using System.IO;
using System.Linq;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Registry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// Opening a registry database that an older build created.
/// </summary>
/// <remarks>
/// Schema 5 is the first migration that takes something away rather than adding to it, and a
/// migration that drops a table has to be right the first time — the user's registry is the only
/// copy of what they have registered, and there is no undo. So the case is built rather than
/// assumed: a real database is walked back to the shape schema 4 left behind, reopened, and checked
/// for both halves of the change.
/// </remarks>
public sealed class SchemaMigrationTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"hlafomreader-migrate-{Guid.NewGuid():N}.db");

    [Fact]
    public void AVersionFourDatabaseGainsTheModuleListAndLosesTheDependencyTable()
    {
        long registeredId;

        using (var repository = new SqliteFomRepository(_databasePath))
            registeredId = repository.Register(Module("RPR 2.0"), "RPR 2.0", Temp("rpr.xml")).Id;

        WalkBackToSchemaFour();

        Assert.Equal(4, StoredVersion());
        Assert.True(TableExists("FomDependencies"), "the walk-back did not restore the schema-4 shape");
        Assert.False(ColumnExists("Foms", "ComposedFrom"), "the walk-back left the schema-5 column behind");

        // Reopening is what migrates it.
        using (var repository = new SqliteFomRepository(_databasePath))
        {
            // What was already registered is still registered, and still readable.
            var survivor = Assert.Single(repository.ListEntries());
            Assert.Equal(registeredId, survivor.Id);
            Assert.Equal("RPR 2.0", survivor.DisplayName);
            Assert.False(survivor.IsComposed);
            Assert.Equal(1, repository.LoadDocument(survivor.Id).AttributeCount);

            // And the new column works, rather than merely existing.
            var compiled = repository.Register(
                Module("NETN stack"), "NETN stack", Temp("netn.xml"),
                composedFrom: new[] { "a.xml", "b.xml" });

            Assert.Equal(
                new[] { "a.xml", "b.xml" },
                repository.ListEntries().Single(e => e.Id == compiled.Id).ComposedModules);
        }

        Assert.Equal(FomDatabase.CurrentSchemaVersion, StoredVersion());
        Assert.False(TableExists("FomDependencies"), "the dependency table survived the migration");
        Assert.False(IndexExists("IX_FomDependencies_DependsOn"), "the dependency index survived the migration");
    }

    /// <summary>A database already at the current schema is opened without being migrated.</summary>
    [Fact]
    public void ACurrentDatabaseIsLeftAlone()
    {
        using (var repository = new SqliteFomRepository(_databasePath))
            repository.Register(Module("RPR 2.0"), "RPR 2.0", Temp("rpr.xml"));

        Assert.Equal(FomDatabase.CurrentSchemaVersion, StoredVersion());

        using (var repository = new SqliteFomRepository(_databasePath))
            Assert.Single(repository.ListEntries());

        Assert.Equal(FomDatabase.CurrentSchemaVersion, StoredVersion());
        Assert.False(TableExists("FomDependencies"));
    }

    // ---- walking a real database back to the older shape ---------------------------------------

    private void WalkBackToSchemaFour()
    {
        SqliteConnection.ClearAllPools();

        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE Foms DROP COLUMN ComposedFrom;

            CREATE TABLE IF NOT EXISTS FomDependencies (
                FomId           INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE CASCADE,
                DependsOnFomId  INTEGER NOT NULL REFERENCES Foms (Id) ON DELETE RESTRICT,
                Ordinal         INTEGER NOT NULL,
                DeclaredAs      TEXT,
                PRIMARY KEY (FomId, DependsOnFomId)
            );

            CREATE INDEX IF NOT EXISTS IX_FomDependencies_DependsOn ON FomDependencies (DependsOnFomId);

            UPDATE SchemaVersion SET Version = 4;
            """;
        command.ExecuteNonQuery();
    }

    // ---- reading the raw shape ------------------------------------------------------------------

    private long StoredVersion() => Scalar<long>("SELECT Version FROM SchemaVersion LIMIT 1;");

    private bool TableExists(string name) =>
        Scalar<long>($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{name}';") > 0;

    private bool IndexExists(string name) =>
        Scalar<long>($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = '{name}';") > 0;

    private bool ColumnExists(string table, string column) =>
        Scalar<long>($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';") > 0;

    private T Scalar<T>(string sql)
    {
        SqliteConnection.ClearAllPools();

        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    // ---- fixtures --------------------------------------------------------------------------------

    private static FomDocument Module(string name)
    {
        var document = new FomDocument { Standard = FomStandard.Ieee1516_2010 };
        document.Identification.Name = name;

        var root = new FomObjectClass { Name = "HLAobjectRoot", QualifiedName = "HLAobjectRoot" };
        var aircraft = new FomObjectClass
        {
            Name = "Aircraft",
            QualifiedName = "HLAobjectRoot.Aircraft",
            Parent = root,
        };

        aircraft.Attributes.Add(new FomAttribute { Name = "Afterburner", DataType = "HLAboolean" });

        root.Children.Add(aircraft);
        document.ObjectClasses.Add(root);
        return document;
    }

    private string Temp(string name) =>
        Path.Combine(Path.GetDirectoryName(_databasePath)!, $"{Guid.NewGuid():N}-{name}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(_databasePath + suffix)) File.Delete(_databasePath + suffix);
        }
    }
}
