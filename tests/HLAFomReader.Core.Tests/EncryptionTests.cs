using System;
using System.IO;
using System.Linq;
using System.Text;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Registry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// SQLCipher behaviour for the registry database: a plaintext store by default, an encrypted one when
/// a password is given, and in-place migration in both directions. Includes the WAL-sidecar case,
/// which matters here because the registry runs in WAL mode: a rekey must not leave a <c>-wal</c> or
/// <c>-shm</c> file behind that was written under the key the database no longer has.
/// </summary>
public sealed class EncryptionTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly string _databasePath;

    public EncryptionTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-enc-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        foreach (var suffix in new[] { "", "-wal", "-shm", ".bak", ".export.tmp" })
            TryDelete(_databasePath + suffix);
    }

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }

    private static string Samples
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "samples")))
                directory = directory.Parent;
            return Path.Combine(directory!.FullName, "samples");
        }
    }

    /// <summary>Registers one sample FOM so the database has real content to protect.</summary>
    private void Seed(string? password)
    {
        var path = Path.Combine(Samples, "RestaurantFOM-1516-2010.xml");
        using var repository = new SqliteFomRepository(_databasePath, password);
        repository.Register(FomFileReader.ParseFile(path), "Restaurant Evolved", path);
    }

    private int CountEntries(string? password)
    {
        using var repository = new SqliteFomRepository(_databasePath, password);
        return repository.ListEntries().Count;
    }

    [Fact]
    public void NoPasswordGivesAPlaintextDatabase()
    {
        Seed(password: null);
        SqliteConnection.ClearAllPools();

        Assert.True(FomDatabase.IsPlaintextDatabase(_databasePath));
        Assert.False(FomDatabase.IsEncrypted(_databasePath));
    }

    [Fact]
    public void EncryptedDatabaseRoundTripsWithTheCorrectPassword()
    {
        Seed(Password);
        SqliteConnection.ClearAllPools();

        Assert.True(FomDatabase.IsEncrypted(_databasePath));
        Assert.Equal(1, CountEntries(Password));
    }

    [Fact]
    public void TheFileOnDiskIsNotReadableAsPlaintext()
    {
        Seed(Password);
        SqliteConnection.ClearAllPools();

        // A plaintext SQLite file begins "SQLite format 3\0"; an encrypted one does not.
        var head = File.ReadAllBytes(_databasePath)[..16];

        Assert.NotEqual("SQLite format 3\0", Encoding.ASCII.GetString(head));
        Assert.False(FomDatabase.IsPlaintextDatabase(_databasePath));
    }

    [Fact]
    public void TheWrongPasswordIsRejected()
    {
        Seed(Password);
        SqliteConnection.ClearAllPools();

        Assert.False(FomDatabase.CanOpen(_databasePath, "wrong password"));
        Assert.False(FomDatabase.CanOpen(_databasePath, null));
        Assert.True(FomDatabase.CanOpen(_databasePath, Password));

        Assert.ThrowsAny<SqliteException>(() => new SqliteFomRepository(_databasePath, "wrong password"));
    }

    [Fact]
    public void APlaintextDatabaseCanBeEncryptedInPlaceWithoutLosingContent()
    {
        Seed(password: null);
        SqliteConnection.ClearAllPools();
        Assert.True(FomDatabase.IsPlaintextDatabase(_databasePath));

        FomDatabase.EncryptPlaintext(_databasePath, Password);
        SqliteConnection.ClearAllPools();

        Assert.True(FomDatabase.IsEncrypted(_databasePath));
        Assert.Equal(1, CountEntries(Password));
    }

    [Fact]
    public void AnEncryptedDatabaseCanBeDecryptedBackToPlaintext()
    {
        Seed(Password);
        SqliteConnection.ClearAllPools();

        FomDatabase.DecryptToPlaintext(_databasePath, Password);
        SqliteConnection.ClearAllPools();

        Assert.True(FomDatabase.IsPlaintextDatabase(_databasePath));
        Assert.Equal(1, CountEntries(null));
    }

    [Fact]
    public void ThePasswordCanBeChanged()
    {
        Seed(Password);
        SqliteConnection.ClearAllPools();

        FomDatabase.ChangePassword(_databasePath, Password, "new password");
        SqliteConnection.ClearAllPools();

        Assert.False(FomDatabase.CanOpen(_databasePath, Password));
        Assert.True(FomDatabase.CanOpen(_databasePath, "new password"));
        Assert.Equal(1, CountEntries("new password"));
    }

    /// <summary>
    /// The app runs in WAL mode, so a rekey must not leave -wal/-shm sidecars written under the old
    /// key beside the re-keyed file — SQLite would try to replay them and fail, or silently resurrect
    /// stale pages.
    /// </summary>
    [Fact]
    public void EncryptingLeavesNoStaleWalSidecarsBehind()
    {
        Seed(password: null);
        SqliteConnection.ClearAllPools();

        FomDatabase.EncryptPlaintext(_databasePath, Password);

        Assert.False(File.Exists(_databasePath + "-wal"), "a -wal sidecar survived the rekey");
        Assert.False(File.Exists(_databasePath + "-shm"), "a -shm sidecar survived the rekey");
        Assert.False(File.Exists(_databasePath + ".bak"), "the backup file was not cleaned up");

        // And the re-keyed database is genuinely usable.
        Assert.Equal(1, CountEntries(Password));
    }

    [Fact]
    public void EverythingParsedSurvivesTheRoundTripThroughEncryption()
    {
        var source = Path.Combine(Samples, "RestaurantFOM-1516-2010.xml");
        var parsed = FomFileReader.ParseFile(source);

        using (var repository = new SqliteFomRepository(_databasePath, Password))
            repository.Register(parsed, "Restaurant Evolved", source);

        SqliteConnection.ClearAllPools();

        using var reopened = new SqliteFomRepository(_databasePath, Password);
        var entry = reopened.ListEntries().Single();
        var reloaded = reopened.LoadDocument(entry.Id);

        Assert.Equal(parsed.ObjectClassCount, reloaded.ObjectClassCount);
        Assert.Equal(parsed.AttributeCount, reloaded.AttributeCount);
        Assert.Equal(parsed.DataTypeCount, reloaded.DataTypeCount);

        var result = new Comparison.FomComparer().Compare(parsed, reloaded);
        Assert.True(result.AreIdentical,
            $"Encryption changed the stored model: {result.TotalDifferences} differences");
    }
}
