using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Registry;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// Covers reading the registry database back table by table and lining two FOMs up row by row —
/// the "Stored rows" tab. These go straight at SQLite rather than at the parsed model, so they also
/// catch a schema/SQL drift that the object-level tests would miss.
/// </summary>
public sealed class StoredRowsTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _databasePath;
    private readonly SqliteFomRepository _repository;
    private readonly FomRegistryEntry _v1;
    private readonly FomRegistryEntry _v2;
    private readonly FomRegistryEntry _fed;

    public StoredRowsTests(ITestOutputHelper output)
    {
        _output = output;
        _databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-rows-{Guid.NewGuid():N}.db");
        _repository = new SqliteFomRepository(_databasePath);

        _v1 = Register("RestaurantFOM-1516-2010.xml");
        _v2 = Register("RestaurantFOM-1516-2010-v2.xml");
        _fed = Register("RestaurantFOM-1.3.fed");
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
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }

    [Fact]
    public void EveryCatalogedTableCanBeReadForEveryRegisteredFom()
    {
        var tables = _repository.ListTables();
        Assert.NotEmpty(tables);

        foreach (var entry in new[] { _v1, _v2, _fed })
        {
            foreach (var table in tables)
            {
                var snapshot = _repository.ReadTable(entry.Id, table.Name);

                Assert.Equal(table.Name, snapshot.TableName);
                Assert.Equal(snapshot.RowCount, _repository.CountRows(entry.Id, table.Name));

                // The Key column drives alignment and must never leak into the display columns,
                // and surrogate ids must never be shown to the user.
                Assert.DoesNotContain("Key", snapshot.Columns, StringComparer.OrdinalIgnoreCase);
                Assert.DoesNotContain(snapshot.Columns,
                    c => c.Equals("Id", StringComparison.OrdinalIgnoreCase)
                      || c.Equals("FomId", StringComparison.OrdinalIgnoreCase)
                      || c.Equals("ParentId", StringComparison.OrdinalIgnoreCase));

                Assert.All(snapshot.Rows, row =>
                {
                    Assert.NotNull(row.Key);
                    Assert.Equal(snapshot.Columns.Count, row.Values.Count);
                });
            }
        }
    }

    [Fact]
    public void AnUnknownTableNameYieldsAnEmptySnapshotRatherThanThrowing()
    {
        var snapshot = _repository.ReadTable(_v1.Id, "DefinitelyNotATable");

        Assert.Empty(snapshot.Rows);
        Assert.Empty(snapshot.Columns);
        Assert.Equal(0, _repository.CountRows(_v1.Id, "DefinitelyNotATable"));
    }

    [Fact]
    public void StoredAttributeRowCountsMatchTheParsedModel()
    {
        var attributes = _repository.ReadTable(_v1.Id, "ObjectAttributes");
        var classes = _repository.ReadTable(_v1.Id, "ObjectClasses");

        Assert.Equal(_v1.AttributeCount, attributes.RowCount);
        Assert.Equal(_v1.ObjectClassCount, classes.RowCount);

        // Keys are the dotted qualified names, so they identify a row unambiguously.
        Assert.Contains(attributes.Rows, r => r.Key.EndsWith("Customer.PartySize", StringComparison.Ordinal));
    }

    [Fact]
    public void ComparingAFomAgainstItselfFindsNoRowDifferences()
    {
        foreach (var table in _repository.ListTables())
        {
            var left = _repository.ReadTable(_v1.Id, table.Name);
            var right = _repository.ReadTable(_v1.Id, table.Name);
            var comparison = TableComparer.Compare(left, right);

            Assert.True(comparison.IsIdentical,
                $"{table.Name}: expected no differences, got {comparison.DifferenceCount}");
            Assert.Equal(left.RowCount, comparison.Rows.Count);
        }
    }

    [Fact]
    public void AttributeRowsLineUpAndShowExactlyTheAuthoredChanges()
    {
        var comparison = TableComparer.Compare(
            _repository.ReadTable(_v1.Id, "ObjectAttributes"),
            _repository.ReadTable(_v2.Id, "ObjectAttributes"));

        foreach (var row in comparison.Rows.Where(r => r.IsDifferent))
            _output.WriteLine($"{row.State,-8} {row.Key}  [{row.ChangedColumns}]");

        // v2 adds LoyaltyPoints plus the Manager class's two attributes.
        Assert.Equal(3, comparison.AddedCount);
        // ...removes Chef.YearsExperience...
        Assert.Equal(1, comparison.RemovedCount);
        // ...and changes PartySize's dataType, TipTotal's transportation, DishesWashed's order.
        Assert.Equal(3, comparison.ChangedCount);

        var partySize = comparison.Rows.Single(r => r.Key.EndsWith("Customer.PartySize", StringComparison.Ordinal));
        Assert.Equal(RowState.Changed, partySize.State);
        Assert.Contains("DataType", partySize.ChangedColumns, StringComparison.Ordinal);

        var dataTypeCell = partySize.Cells.Single(c => c.Column.Equals("DataType", StringComparison.Ordinal));
        Assert.True(dataTypeCell.IsDifferent);
        Assert.NotEqual(dataTypeCell.Left, dataTypeCell.Right);
    }

    [Fact]
    public void OneSidedRowsReportTheirPopulatedColumnsAsChanged()
    {
        var comparison = TableComparer.Compare(
            _repository.ReadTable(_v1.Id, "ObjectClasses"),
            _repository.ReadTable(_v2.Id, "ObjectClasses"));

        var manager = comparison.Rows.Single(r => r.Key.EndsWith("Employee.Manager", StringComparison.Ordinal));

        Assert.Equal(RowState.Added, manager.State);
        Assert.True(manager.IsDifferent);
        Assert.NotEmpty(manager.ChangedColumns);

        // The left side of an added row is empty throughout.
        Assert.All(manager.Cells, cell => Assert.True(string.IsNullOrEmpty(cell.Left)));
    }

    [Fact]
    public void Hla13StoresNoDatatypeOrDimensionRowsButDoesStoreRoutingSpaces()
    {
        Assert.Equal(0, _repository.CountRows(_fed.Id, "DataTypes"));
        Assert.Equal(0, _repository.CountRows(_fed.Id, "DataTypeMembers"));
        Assert.Equal(0, _repository.CountRows(_fed.Id, "Dimensions"));

        Assert.True(_repository.CountRows(_fed.Id, "RoutingSpaces") > 0);
        Assert.True(_repository.CountRows(_fed.Id, "ObjectAttributes") > 0);
    }

    [Fact]
    public void CrossStandardTableCompareShowsTheWholeDatatypeTableAsAdded()
    {
        var comparison = TableComparer.Compare(
            _repository.ReadTable(_fed.Id, "DataTypes"),
            _repository.ReadTable(_v1.Id, "DataTypes"));

        Assert.Equal(0, comparison.RemovedCount);
        Assert.Equal(0, comparison.ChangedCount);
        Assert.Equal(_repository.CountRows(_v1.Id, "DataTypes"), comparison.AddedCount);
    }

    [Fact]
    public void DuplicateKeysOnEitherSideDoNotThrowAndStillAlign()
    {
        var columns = new List<string> { "Value" };
        var left = new TableSnapshot("T", columns, new List<TableRow>
        {
            new("dup", new List<string?> { "a" }),
            new("dup", new List<string?> { "b" }),
            new("unique", new List<string?> { "c" }),
        });
        var right = new TableSnapshot("T", columns, new List<TableRow>
        {
            new("dup", new List<string?> { "a" }),
            new("dup", new List<string?> { "CHANGED" }),
            new("unique", new List<string?> { "c" }),
        });

        var comparison = TableComparer.Compare(left, right);

        Assert.Equal(3, comparison.Rows.Count);
        Assert.Equal(1, comparison.ChangedCount);
        Assert.Equal(0, comparison.AddedCount);
        Assert.Equal(0, comparison.RemovedCount);
    }

    [Fact]
    public void IgnoreCaseAlignsKeysThatDifferOnlyInCasing()
    {
        var columns = new List<string> { "Value" };
        var left = new TableSnapshot("T", columns,
            new List<TableRow> { new("Alpha", new List<string?> { "x" }) });
        var right = new TableSnapshot("T", columns,
            new List<TableRow> { new("ALPHA", new List<string?> { "X" }) });

        Assert.Equal(2, TableComparer.Compare(left, right).Rows.Count);

        var folded = TableComparer.Compare(left, right, ignoreCase: true);
        Assert.Single(folded.Rows);
        Assert.Equal(RowState.Same, folded.Rows[0].State);
    }
}
