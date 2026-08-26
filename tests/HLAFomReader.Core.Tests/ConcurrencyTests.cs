using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Registry;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// The app uses one repository from two threads at once: view models read on the UI thread while
/// Compare and Register do their work inside Task.Run. A single SqliteConnection is not thread-safe,
/// so these tests pin down that the repository serialises access instead of deadlocking.
/// </summary>
public sealed class ConcurrencyTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _databasePath;
    private readonly SqliteFomRepository _repository;
    private readonly FomRegistryEntry _left;
    private readonly FomRegistryEntry _right;

    public ConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
        _databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-conc-{Guid.NewGuid():N}.db");
        _repository = new SqliteFomRepository(_databasePath);

        _left = Register("RestaurantFOM-1516-2010.xml");
        _right = Register("RestaurantFOM-1516-2010-v2.xml");
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

    private FomRegistryEntry Register(string fileName)
    {
        var path = Path.Combine(Samples, fileName);
        return _repository.Register(FomFileReader.ParseFile(path), Path.GetFileNameWithoutExtension(path), path);
    }

    public void Dispose()
    {
        _repository.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_databasePath + suffix)) File.Delete(_databasePath + suffix);
    }

    /// <summary>Fails the test rather than hanging the run forever if the repository deadlocks.</summary>
    private void RunWithDeadlockGuard(Action body, int seconds = 45)
    {
        var errors = new ConcurrentBag<Exception>();
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { errors.Add(ex); } })
        {
            IsBackground = true,
        };

        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(seconds)),
            $"Deadlocked: concurrent repository use did not finish within {seconds}s.");

        if (!errors.IsEmpty)
            throw new AggregateException("Concurrent repository use threw.", errors);
    }

    [Fact]
    public void ReadingOnOneThreadWhileComparingOnAnotherDoesNotDeadlock()
    {
        RunWithDeadlockGuard(() =>
        {
            var stop = new CancellationTokenSource();

            // Mimics the UI thread: StoredRowsViewModel.SetPair counts every table on each change.
            var reader = Task.Run(() =>
            {
                var loops = 0;
                while (!stop.IsCancellationRequested)
                {
                    foreach (var table in _repository.ListTables())
                    {
                        _repository.CountRows(_left.Id, table.Name);
                        _repository.CountRows(_right.Id, table.Name);
                    }
                    _repository.ListEntries();
                    loops++;
                }
                return loops;
            });

            // Mimics CompareViewModel.CompareAsync running inside Task.Run.
            var comparer = Task.Run(() =>
            {
                for (var i = 0; i < 8; i++)
                {
                    var a = _repository.LoadDocument(_left.Id);
                    var b = _repository.LoadDocument(_right.Id);
                    var result = new FomComparer().Compare(a, b);
                    _repository.SaveComparison(result, _left.Id, _right.Id);
                }
            });

            comparer.GetAwaiter().GetResult();
            stop.Cancel();
            _output.WriteLine($"reader completed {reader.GetAwaiter().GetResult()} sweeps");
        });

        Assert.Equal(8, _repository.ListComparisons().Count);
    }

    [Fact]
    public void RegisteringOnOneThreadWhileReadingOnAnotherDoesNotDeadlock()
    {
        RunWithDeadlockGuard(() =>
        {
            var stop = new CancellationTokenSource();

            var reader = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    _repository.ListEntries();
                    _repository.ReadTable(_left.Id, "ObjectAttributes");
                }
            });

            var writer = Task.Run(() =>
            {
                var path = Path.Combine(Samples, "RestaurantFOM-1.3.fed");
                var parsed = FomFileReader.ParseFile(path);

                // Re-registering the same path replaces the row, exercising the delete+insert
                // transaction that Register runs.
                for (var i = 0; i < 6; i++)
                    _repository.Register(parsed, "Restaurant 1.3", path);
            });

            writer.GetAwaiter().GetResult();
            stop.Cancel();
            reader.GetAwaiter().GetResult();
        });

        Assert.Equal(3, _repository.ListEntries().Count);
    }

    [Fact]
    public void ManyThreadsReadingAtOnceAllGetCorrectResults()
    {
        RunWithDeadlockGuard(() =>
        {
            var expectedAttributes = _repository.ReadTable(_left.Id, "ObjectAttributes").RowCount;

            var results = new ConcurrentBag<int>();
            Parallel.For(0, 32, _ =>
            {
                results.Add(_repository.ReadTable(_left.Id, "ObjectAttributes").RowCount);
            });

            Assert.Equal(32, results.Count);
            Assert.All(results, r => Assert.Equal(expectedAttributes, r));
        });
    }
}
