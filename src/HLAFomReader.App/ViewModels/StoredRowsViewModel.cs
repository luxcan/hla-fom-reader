using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Registry;

namespace HLAFomReader.App.ViewModels;

/// <summary>
/// One browsable table in the list down the left of the Stored rows tab, carrying the cheap row
/// counts that let a user see where the two FOMs disagree before opening anything.
/// </summary>
public sealed class TableEntry : ObservableObject
{
    private int _leftCount;
    private int _rightCount;

    /// <summary>Wraps a registry table for presentation.</summary>
    /// <param name="table">The table metadata read from the repository.</param>
    public TableEntry(RegistryTable table)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
    }

    /// <summary>The underlying registry table, including the SQL used to read it.</summary>
    public RegistryTable Table { get; }

    /// <summary>Label shown in the list.</summary>
    public string DisplayName => Table.DisplayName;

    /// <summary>One-line explanation, shown as the item's tooltip.</summary>
    public string Description => Table.Description;

    /// <summary>Rows this table holds for FOM A.</summary>
    public int LeftCount
    {
        get => _leftCount;
        private set
        {
            if (!SetProperty(ref _leftCount, value)) return;
            OnPropertyChanged(nameof(CountBadge), nameof(HasRows), nameof(IsDifferent));
        }
    }

    /// <summary>Rows this table holds for FOM B.</summary>
    public int RightCount
    {
        get => _rightCount;
        private set
        {
            if (!SetProperty(ref _rightCount, value)) return;
            OnPropertyChanged(nameof(CountBadge), nameof(HasRows), nameof(IsDifferent));
        }
    }

    /// <summary>
    /// Trailing badge: <c>"30 / 32"</c> when the sides disagree, the bare count when they match, and
    /// an em dash when neither FOM has anything in this table.
    /// </summary>
    public string CountBadge =>
        LeftCount == 0 && RightCount == 0 ? "—"
        : LeftCount == RightCount ? LeftCount.ToString()
        : $"{LeftCount} / {RightCount}";

    /// <summary>True when at least one side has rows; false items are drawn muted.</summary>
    public bool HasRows => LeftCount > 0 || RightCount > 0;

    /// <summary>
    /// A differing row count guarantees a difference, so it is worth flagging before the table is
    /// opened. Equal counts prove nothing — the rows themselves may still differ.
    /// </summary>
    public bool IsDifferent => LeftCount != RightCount;

    /// <summary>Applies freshly counted rows for the current FOM pair.</summary>
    public void SetCounts(int left, int right)
    {
        LeftCount = left;
        RightCount = right;
    }

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}

/// <summary>
/// The Stored rows tab of the Compare screen: the same two FOMs the semantic diff uses, but shown
/// as the raw registry tables so a user can see exactly what was stored and where the two sides
/// diverge row by row.
/// </summary>
public sealed class StoredRowsViewModel : ViewModelBase
{
    private const string DefaultLeftLabel = "FOM A";
    private const string DefaultRightLabel = "FOM B";

    private readonly IFomRepository _repository;
    private readonly IDialogService _dialogs;

    private long? _leftId;
    private long? _rightId;
    private string _leftLabel = DefaultLeftLabel;
    private string _rightLabel = DefaultRightLabel;

    private TableEntry? _selectedTable;
    private TableComparison? _comparison;
    private RowPair? _selectedRow;
    private bool _onlyDifferences = true;
    private string _searchText = "";
    private bool _ignoreCase;
    private bool _isActive;
    private bool _isStale = true;

    // Bumped by each table read and re-checked when that read's worker returns. See LoadSelectedTableAsync.
    private int _generation;

    /// <summary>Creates the screen and loads the list of browsable tables.</summary>
    /// <param name="repository">Store the rows are read from.</param>
    /// <param name="dialogs">Used for the save dialog and for surfacing repository failures.</param>
    public StoredRowsViewModel(IFomRepository repository, IDialogService dialogs)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        RefreshCommand = new RelayCommand(Refresh);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ExportCsvCommand = new RelayCommand(ExportCsv, () => Comparison is not null);

        LoadTables();
    }

    /// <summary>Every table the registry can show, in presentation order.</summary>
    public ObservableCollection<TableEntry> Tables { get; } = new();

    /// <summary>
    /// The table read currently in flight, or a completed task when the screen is idle.
    /// </summary>
    /// <remarks>
    /// The entry points below are property setters and void callbacks, so they cannot hand their
    /// task back to whoever triggered them. Parking it here costs nothing and means the work is
    /// observable rather than merely discarded: a caller that must not race it can wait on this
    /// instead of guessing at a delay.
    /// </remarks>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    /// <summary>The aligned rows of <see cref="SelectedTable"/> that survive the current filters.</summary>
    public ObservableCollection<RowPair> Rows { get; } = new();

    /// <summary>Re-counts every table and re-reads the open one.</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>Empties <see cref="SearchText"/>.</summary>
    public RelayCommand ClearSearchCommand { get; }

    /// <summary>Writes the current table's comparison out as CSV.</summary>
    public RelayCommand ExportCsvCommand { get; }

    /// <summary>Selecting a table reads it for both FOMs and compares the two snapshots.</summary>
    public TableEntry? SelectedTable
    {
        get => _selectedTable;
        set
        {
            if (!SetProperty(ref _selectedTable, value)) return;
            PendingWork = LoadSelectedTableAsync(showBusy: true);
        }
    }

    /// <summary>The alignment of the selected table across the two FOMs; null until one is opened.</summary>
    public TableComparison? Comparison
    {
        get => _comparison;
        private set
        {
            if (!SetProperty(ref _comparison, value)) return;
            OnPropertyChanged(nameof(RowSummary));
            ExportCsvCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The row whose cells fill the detail pane.</summary>
    public RowPair? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;
            OnPropertyChanged(nameof(HasSelection), nameof(SelectedCells));
        }
    }

    /// <summary>Column-by-column values of <see cref="SelectedRow"/>, empty when nothing is selected.</summary>
    public IReadOnlyList<CellPair> SelectedCells => SelectedRow?.Cells ?? Array.Empty<CellPair>();

    /// <summary>
    /// Hides rows that match on both sides. Defaults to true: a stored table is mostly noise, and
    /// the point of this screen is the disagreement.
    /// </summary>
    public bool OnlyDifferences
    {
        get => _onlyDifferences;
        set
        {
            if (!SetProperty(ref _onlyDifferences, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>Free-text filter over the row key and every cell value, ordinal-ignore-case.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            ApplyFilter();
        }
    }

    /// <summary>
    /// Compares cell values without regard to case. Changing this re-runs the alignment, since the
    /// comparer decides which rows count as changed.
    /// </summary>
    public bool IgnoreCase
    {
        get => _ignoreCase;
        set
        {
            if (!SetProperty(ref _ignoreCase, value)) return;
            PendingWork = LoadSelectedTableAsync(showBusy: true);
        }
    }

    /// <summary>Display name of FOM A, or a placeholder before a pair is chosen.</summary>
    public string LeftLabel
    {
        get => _leftLabel;
        private set => SetProperty(ref _leftLabel, value);
    }

    /// <summary>Display name of FOM B, or a placeholder before a pair is chosen.</summary>
    public string RightLabel
    {
        get => _rightLabel;
        private set => SetProperty(ref _rightLabel, value);
    }

    /// <summary>True when a row is selected and the detail pane has something to show.</summary>
    public bool HasSelection => SelectedRow is not null;

    /// <summary>True once the parent has handed over both FOMs.</summary>
    public bool HasPair => _leftId is not null && _rightId is not null;

    /// <summary>True when the grid has at least one row after filtering.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>True before this pair has been read — the tab is empty because nothing ran yet.</summary>
    public bool IsAwaitingCompare => _isStale;

    /// <summary>Explains an empty grid — the reason differs and the user cannot be left guessing.</summary>
    public string EmptyMessage =>
        !HasPair ? "Choose FOM A and FOM B above to browse what the registry stored for them."
        : IsAwaitingCompare ? "Press Compare to read what the registry stored for these two FOMs."
        : SelectedTable is null ? "Pick a registry table on the left to see its stored rows."
        : Comparison is null ? "This table could not be read from the registry database."
        : Comparison.Rows.Count == 0 ? "This table is empty in both FOMs."
        : "No rows match the current filters.";

    /// <summary>
    /// Trailing summary on the filter strip, e.g. "12 of 30 rows · 3 changed · 1 added · 0 removed".
    /// Blank until a table has been read.
    /// </summary>
    public string RowSummary =>
        Comparison is not { } comparison
            ? ""
            : $"{Rows.Count} of {comparison.Rows.Count} row{(comparison.Rows.Count == 1 ? "" : "s")} · " +
              $"{comparison.ChangedCount} changed · {comparison.AddedCount} added · {comparison.RemovedCount} removed";

    /// <summary>
    /// Re-points the screen at a new pair of FOMs. Called by the Compare screen whenever its two
    /// pickers change, so both tabs always describe the same pair.
    /// </summary>
    /// <param name="left">FOM A, or null when the picker is empty.</param>
    /// <param name="right">FOM B, or null when the picker is empty.</param>
    /// <summary>
    /// True while the "Stored rows" tab is the visible one. Bound from the TabItem.
    /// </summary>
    /// <remarks>
    /// Selection only — showing this tab does not read anything. The Compare screen fills all three
    /// of its tabs in one pass so a single overlay covers the whole wait; see
    /// <see cref="PreloadAsync"/> and CompareViewModel.CompareAsync.
    /// </remarks>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>
    /// Reads this tab's data <i>without</i> selecting it, completing once the rows are on screen.
    /// </summary>
    /// <param name="showBusy">
    /// False when the caller already owns an overlay covering this view, so the two do not stack.
    /// </param>
    /// <remarks>
    /// Deliberately not an Activate: <see cref="IsActive"/> is TwoWay-bound to this tab's IsSelected,
    /// so raising it here would pull the tab strip onto Stored rows at the end of every comparison.
    /// The Compare screen lands on the attribute map; this tab just has to be ready when it is asked
    /// for. Refreshing the badges is one COUNT per table per side — currently 42 queries — which is
    /// why it is worth doing once here rather than on every picker change.
    /// </remarks>
    public async Task PreloadAsync(bool showBusy = true)
    {
        if (!_isStale) return;
        _isStale = false;
        RaiseRowState();

        var work = ReadAllAsync(showBusy);
        PendingWork = work;
        await work.ConfigureAwait(true);
    }

    private async Task ReadAllAsync(bool showBusy)
    {
        var busy = showBusy ? BeginBusy("Reading stored rows…") : null;
        try
        {
            await RefreshCountsAsync().ConfigureAwait(true);

            // Land on a table so this tab is readable the moment it is clicked. Counts alone leave a
            // list down the left and an empty grid beside it, which is the half-loaded state the
            // single-pass load exists to get rid of. First one carrying rows, because a table that is
            // empty in both FOMs demonstrates nothing; the plain first table if none of them do.
            //
            // Written to the field: the property's setter starts its own fire-and-forget read with
            // its own overlay, and the read wanted here is the awaited one below.
            if (_selectedTable is null && Tables.Count > 0)
            {
                _selectedTable = Tables.FirstOrDefault(entry => entry.HasRows) ?? Tables[0];
                OnPropertyChanged(nameof(SelectedTable));
            }

            // showBusy: false — the scope above already owns the overlay, and a second one nested
            // inside it would stack a second scrim over the same view.
            await LoadSelectedTableAsync(showBusy: false).ConfigureAwait(true);
        }
        finally
        {
            busy?.Dispose();
        }
    }

    public void SetPair(FomRegistryEntry? left, FomRegistryEntry? right)
    {
        _leftId = left?.Id;
        _rightId = right?.Id;

        LeftLabel = string.IsNullOrWhiteSpace(left?.DisplayName) ? DefaultLeftLabel : left!.DisplayName;
        RightLabel = string.IsNullOrWhiteSpace(right?.DisplayName) ? DefaultRightLabel : right!.DisplayName;

        ClearLoadedTable();

        // A table stays selected across a pair change, but re-reading it is Compare's job now: doing
        // it here would run 42 counts per picker for a result the next Compare replaces. See IsActive.
        _isStale = true;

        OnPropertyChanged(nameof(HasPair));
        RaiseRowState();
    }

    // ---- loading ----------------------------------------------------------------------------

    private void LoadTables()
    {
        try
        {
            foreach (var table in _repository.ListTables())
                Tables.Add(new TableEntry(table));
        }
        catch (Exception ex)
        {
            // An unusable table list leaves the screen empty rather than bringing the shell down.
            _dialogs.ShowError("Stored rows", $"The list of registry tables could not be read.\n\n{ex.Message}");
        }
    }

    private void Refresh()
    {
        PendingWork = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _isStale = false;
        await ReadAllAsync(showBusy: true).ConfigureAwait(true);
        StatusMessage = "Stored rows refreshed";
    }

    /// <summary>
    /// Re-counts every table on both sides, off the UI thread.
    /// </summary>
    /// <remarks>
    /// One COUNT per table per side — currently 42 queries. Run inline they block the dispatcher, so
    /// the overlay raised around this call could never be painted and the screen would simply freeze
    /// instead. The queries run on a worker; the badges are written back on the dispatcher after.
    /// </remarks>
    private async Task RefreshCountsAsync()
    {
        var leftId = _leftId;
        var rightId = _rightId;
        var names = Tables.Select(entry => entry.Table.Name).ToArray();

        try
        {
            var counts = await Task.Run(
                    () => names.Select(name => (Left: Count(leftId, name), Right: Count(rightId, name))).ToArray())
                .ConfigureAwait(true);

            // Indexed against the array built above rather than re-enumerating Tables: the two are
            // the same list in the same order, and pairing them positionally is what keeps a badge
            // from being written with another table's count.
            for (var i = 0; i < counts.Length && i < Tables.Count; i++)
                Tables[i].SetCounts(counts[i].Left, counts[i].Right);
        }
        catch (Exception ex)
        {
            // One report for the whole sweep — a broken database would otherwise raise a dialog per table.
            foreach (var entry in Tables)
                entry.SetCounts(0, 0);

            _dialogs.ShowError("Stored rows", $"The stored row counts could not be read.\n\n{ex.Message}");
        }
    }

    private int Count(long? fomId, string tableName) =>
        fomId is { } id ? _repository.CountRows(id, tableName) : 0;

    /// <summary>
    /// Reads both sides of the selected table and aligns them, off the UI thread.
    /// </summary>
    /// <param name="showBusy">
    /// False when the caller already owns an overlay covering this view, so the two do not stack.
    /// </param>
    /// <remarks>
    /// Two SELECTs plus the alignment. Small for most registry tables and not small for the row
    /// tables, but run inline the overlay raised here can never be painted either way: the flag goes
    /// up and back down inside one dispatcher turn, so WPF is never given a frame to draw it in, and
    /// the screen simply freezes. Off-thread it paints, and its bar keeps moving.
    /// </remarks>
    private async Task LoadSelectedTableAsync(bool showBusy)
    {
        ClearLoadedTable();

        if (SelectedTable is not { } entry || _leftId is not { } leftId || _rightId is not { } rightId)
        {
            RaiseRowState();
            return;
        }

        // Stamped before the await, checked after it. Clicking down the table list faster than the
        // reads complete would otherwise let a slower earlier read land last, painting its rows
        // under the name of whichever table is selected by then.
        var generation = ++_generation;

        // Captured on the dispatcher: the worker must not read properties the user can still change.
        var tableName = entry.Table.Name;
        var ignoreCase = IgnoreCase;

        var busy = showBusy ? BeginBusy($"Reading {entry.DisplayName}…") : null;
        try
        {
            var comparison = await Task.Run(() =>
            {
                var left = _repository.ReadTable(leftId, tableName);
                var right = _repository.ReadTable(rightId, tableName);
                return TableComparer.Compare(left, right, ignoreCase);
            }).ConfigureAwait(true);

            if (generation != _generation) return;

            Comparison = comparison;
            ApplyFilter();

            StatusMessage = comparison.IsIdentical
                ? $"{entry.DisplayName}: identical in both FOMs"
                : $"{entry.DisplayName}: {comparison.DifferenceCount} row difference" +
                  $"{(comparison.DifferenceCount == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            // A superseded read fails quietly: its table is not the one on screen any more, so a
            // dialog about it would name a table the user has already clicked away from.
            if (generation != _generation) return;

            Comparison = null;
            RaiseRowState();
            _dialogs.ShowError("Stored rows",
                $"\"{entry.DisplayName}\" could not be read from the registry database.\n\n{ex.Message}");
        }
        finally
        {
            busy?.Dispose();
        }
    }

    private void ClearLoadedTable()
    {
        Comparison = null;
        Rows.Clear();
        SelectedRow = null;
    }

    // ---- filtering --------------------------------------------------------------------------

    private void ApplyFilter()
    {
        // The selection survives a filter change whenever the row is still visible.
        var selectedKey = SelectedRow?.Key;

        Rows.Clear();

        if (Comparison is { } comparison)
        {
            foreach (var row in comparison.Rows)
            {
                if (Matches(row)) Rows.Add(row);
            }
        }

        SelectedRow = selectedKey is null
            ? null
            : Rows.FirstOrDefault(r => string.Equals(r.Key, selectedKey, StringComparison.Ordinal));

        RaiseRowState();
    }

    private bool Matches(RowPair row)
    {
        if (OnlyDifferences && !row.IsDifferent) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var needle = SearchText.Trim();
        if (Contains(row.Key, needle)) return true;

        foreach (var cell in row.Cells)
        {
            if (Contains(cell.Left, needle) || Contains(cell.Right, needle)) return true;
        }

        return false;
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void RaiseRowState() =>
        OnPropertyChanged(nameof(HasRows), nameof(EmptyMessage), nameof(RowSummary),
                          nameof(IsAwaitingCompare));

    // ---- CSV export -------------------------------------------------------------------------

    private void ExportCsv()
    {
        if (Comparison is not { } comparison || SelectedTable is not { } entry) return;

        var path = _dialogs.SaveFile(
            "Export stored rows",
            "CSV files|*.csv|All files|*.*",
            $"{Sanitize($"{entry.Table.Name}-{LeftLabel}-vs-{RightLabel}")}.csv",
            "csv");

        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            // The whole comparison is written, not the filtered view: a CSV is an archive of what
            // the two FOMs hold, and a reader cannot tell which filters were active when it was made.
            File.WriteAllText(path, BuildCsv(comparison), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            StatusMessage = $"Stored rows written to {Path.GetFileName(path)}";
            _dialogs.ShowInfo("Export complete", $"{comparison.Rows.Count} rows were written to:\n\n{path}");
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Export failed", ex.Message);
        }
    }

    /// <summary>
    /// Renders the comparison as RFC 4180 CSV, one line per cell so the file can be pivoted or
    /// filtered by column without any further parsing.
    /// </summary>
    private static string BuildCsv(TableComparison comparison)
    {
        var builder = new StringBuilder();
        builder.Append("Key,State,Column,FomA,FomB\r\n");

        foreach (var row in comparison.Rows)
        {
            if (row.Cells.Count == 0)
            {
                // A table with no display columns still has keys worth exporting.
                builder.Append(Quote(row.Key)).Append(',').Append(Quote(row.State.ToString()))
                       .Append(",,,\r\n");
                continue;
            }

            foreach (var cell in row.Cells)
            {
                builder.Append(Quote(row.Key)).Append(',')
                       .Append(Quote(row.State.ToString())).Append(',')
                       .Append(Quote(cell.Column)).Append(',')
                       .Append(Quote(cell.Left)).Append(',')
                       .Append(Quote(cell.Right)).Append("\r\n");
            }
        }

        return builder.ToString();
    }

    /// <summary>Quotes a field only when RFC 4180 requires it, doubling any embedded quote.</summary>
    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var needsQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        return needsQuotes ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "stored-rows" : cleaned;
    }
}
