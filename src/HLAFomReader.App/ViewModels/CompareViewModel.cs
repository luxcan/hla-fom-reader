using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Registry;

namespace HLAFomReader.App.ViewModels;

/// <summary>
/// The Compare screen: pick two registered FOMs, diff them, and walk the result. Handles the
/// cross-standard case (an HLA 1.3 FED against a 1516 FOM) explicitly, since that is where the
/// advisories matter most.
/// </summary>
public sealed class CompareViewModel : ViewModelBase
{
    private readonly IFomRepository _repository;
    private readonly IDialogService _dialogs;
    private readonly FomComparer _comparer = new();

    private ComparisonResult? _result;
    private DiffNode? _resultRoot;

    // The options the comparison on screen was actually run under — the very clone handed to the
    // comparer, not a copy of the live ones. Null exactly when no result is on screen.
    private ComparisonOptions? _resultOptions;
    private FomRegistryEntry? _left;
    private FomRegistryEntry? _right;
    private ClassMapRow? _selectedClass;
    private ClassMap _classes = ClassMap.Empty;

    // The four status chips. Same is off by default so the screen opens on what needs doing; the
    // rest are on, because a class that appears on one side only is the loudest thing here.
    private bool _showChanged = true;
    private bool _showOnlyLeft = true;
    private bool _showOnlyRight = true;
    private bool _showSame;
    private string _searchText = "";
    private bool _hasCompared;

    public CompareViewModel(IFomRepository repository, IDialogService dialogs)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        StoredRows = new StoredRowsViewModel(repository, dialogs);
        AttributeMap = new AttributeMapViewModel(repository, dialogs);

        CompareCommand = new AsyncRelayCommand(CompareAsync, () => CanCompare);
        SwapCommand = new RelayCommand(Swap, () => Left is not null || Right is not null);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
    }

    /// <summary>
    /// The "Stored rows" tab: the same pair of FOMs, read straight back out of SQLite and lined up
    /// table by table. Driven by the same pickers as the difference tree.
    /// </summary>
    public StoredRowsViewModel StoredRows { get; }

    /// <summary>
    /// The "Attribute data" tab: one row per attribute a class effectively has, with its datatype on
    /// each side. This is the remapping view — what data exists and how it is encoded — as opposed to
    /// the difference tree, which reports on the model as a whole.
    /// </summary>
    public AttributeMapViewModel AttributeMap { get; }

    public ObservableCollection<FomRegistryEntry> Sources { get; } = new();
    /// <summary>The classes that survive the current chips and search, in the comparer's order.</summary>
    public ObservableCollection<ClassMapRow> ClassRows { get; } = new();
    public ObservableCollection<string> Advisories { get; } = new();

    public AsyncRelayCommand CompareCommand { get; }
    public RelayCommand SwapCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    public string Title => "Compare FOMs";

    /// <summary>
    /// How the difference tree is built. Only <see cref="Depth"/> and
    /// <see cref="IgnoreInexpressibleProperties"/> are reachable from the screen; everything else
    /// stays at the <see cref="ComparisonOptions"/> defaults, which are the values a cross-standard
    /// run needs and the ones there was never a good reason to change from here.
    /// </summary>
    /// <remarks>
    /// Read when Compare is pressed and not before: the setters store a value and start nothing, so
    /// these are the settings the <em>next</em> run will use. What the run already on screen used is
    /// held separately in <c>_resultOptions</c>, and the two disagreeing is what
    /// <see cref="IsResultStale"/> reports.
    /// </remarks>
    public ComparisonOptions Options { get; } = new();

    public FomRegistryEntry? Left
    {
        get => _left;
        set
        {
            if (!SetProperty(ref _left, value)) return;
            OnPropertyChanged(nameof(CanCompare), nameof(LeftSummary), nameof(PairSummary), nameof(IsCrossStandardPair));
            CompareCommand.RaiseCanExecuteChanged();
            SwapCommand.RaiseCanExecuteChanged();
            StoredRows.SetPair(_left, _right);
            AttributeMap.SetPair(_left, _right);
            ClearComparison();
        }
    }

    public FomRegistryEntry? Right
    {
        get => _right;
        set
        {
            if (!SetProperty(ref _right, value)) return;
            OnPropertyChanged(nameof(CanCompare), nameof(RightSummary), nameof(PairSummary), nameof(IsCrossStandardPair));
            CompareCommand.RaiseCanExecuteChanged();
            SwapCommand.RaiseCanExecuteChanged();
            StoredRows.SetPair(_left, _right);
            AttributeMap.SetPair(_left, _right);
            ClearComparison();
        }
    }

    public bool CanCompare => Left is not null && Right is not null && Left.Id != Right.Id;

    public string LeftSummary => Describe(Left);
    public string RightSummary => Describe(Right);

    public string PairSummary =>
        Left is null || Right is null
            ? "Choose two registered FOMs to compare."
            : Left.Id == Right.Id
                ? "Pick two different FOMs."
                : $"{Left.DisplayName} ({Left.StandardBadge})  ↔  {Right.DisplayName} ({Right.StandardBadge})";

    /// <summary>True when the two selected FOMs come from different HLA standards.</summary>
    public bool IsCrossStandardPair =>
        Left is not null && Right is not null && Left.Standard != Right.Standard;

    public bool HasCompared
    {
        get => _hasCompared;
        private set => SetProperty(ref _hasCompared, value);
    }

    public ComparisonResult? Result
    {
        get => _result;
        private set
        {
            if (!SetProperty(ref _result, value)) return;

            // Cached rather than recomputed per getter: the four chips, the headline and the empty
            // message would otherwise each re-walk the whole tree.
            _classes = value is null ? ClassMap.Empty : ClassMap.Build(value.Root);

            OnPropertyChanged(
                nameof(ChangedCount), nameof(OnlyLeftCount), nameof(OnlyRightCount),
                nameof(SameCount), nameof(TotalDifferences), nameof(AreIdentical),
                nameof(ResultHeadline), nameof(FormatGapNote), nameof(HasFormatGapNote),
                nameof(IsResultStale), nameof(StaleNote));
        }
    }

    // The chips count what the Classes tab draws, not what the comparison found. Reading
    // Result.AddedCount here would put the datatype tables — which on an RPR 1.0 to 2.0 pair dwarf
    // everything else — into a total sitting above a grid that does not contain them. The full
    // figures are still on Result, and are what the status bar and the exported report quote.
    public int ChangedCount => _classes.ChangedCount;
    public int OnlyLeftCount => _classes.OnlyInLeftCount;
    public int OnlyRightCount => _classes.OnlyInRightCount;

    /// <summary>Classes that match, renames included — a rename costs nothing, so it reads as same.</summary>
    public int SameCount => _classes.SameCount;

    /// <summary>Classes somebody has to do something about.</summary>
    public int TotalDifferences => _classes.ActionableCount;

    /// <summary>
    /// True when no class needs attention. Deliberately not "the two FOMs match": the datatype,
    /// dimension and switch tables can still differ, which the export reports and this tab does not.
    /// </summary>
    public bool AreIdentical => Result is not null && TotalDifferences == 0;

    public string ResultHeadline => Result is null
        ? "No comparison run yet"
        : AreIdentical
            ? "Every class lines up under the current options"
            : $"{TotalDifferences} class{(TotalDifferences == 1 ? "" : "es")} need attention";

    /// <summary>
    /// Trailing note on the summary strip breaking the total into authored changes versus format
    /// gaps, so a cross-standard result is never read as "everything changed".
    /// </summary>
    public string FormatGapNote
    {
        get
        {
            if (Result is not { } result || result.FormatGapPropertyCount == 0) return "";

            // Read from the options that produced this result, never the live ones. The checkbox
            // above decides what the next run will do; this line reports what the run already on
            // screen did. Reading the live value meant ticking the box announced that the format
            // gaps were hidden while every one of them was still being counted in the tree below.
            if (_resultOptions?.IgnoreInexpressibleProperties == true)
            {
                var hidden = result.FormatGapPropertyCount;
                return $"{hidden} format-gap propert{(hidden == 1 ? "y" : "ies")} hidden";
            }

            // Quote only the gaps actually being reported. The headline counts tree nodes while
            // these count properties, so say "property differences" explicitly — and use the
            // counted figure, or this line disagrees with the advisory directly above it whenever
            // the depth has stopped some rows counting.
            var gaps = result.CountedFormatGapDifferences;
            if (gaps == 0) return "";

            var total = gaps + result.AuthoredPropertyDifferenceCount;
            return $"{gaps} of {total} property differences are format gaps, not authored changes";
        }
    }

    public bool HasFormatGapNote => FormatGapNote.Length > 0;

    /// <summary>
    /// True when a comparison is on screen but the options have moved since it was run.
    /// </summary>
    /// <remarks>
    /// The figures are not wrong. They are a correct answer to a question the screen has stopped
    /// asking, which is worse, because nothing about them looks provisional — the same class of
    /// mistake as reading a FOM module on its own: a confident number, arrived at honestly, that
    /// answers something other than what the reader believes it answers.
    /// <para>
    /// Deliberately not handled the way a changed picker is. Selecting a different FOM makes the
    /// result answer about the wrong models, so <see cref="ClearComparison"/> throws it away. An
    /// option change leaves it answering about the right two under superseded rules, which is still
    /// worth reading while the next run is decided on — and by comparing values rather than setting
    /// a flag, changing a switch back makes the result current again instead of costing a re-run.
    /// </para>
    /// </remarks>
    public bool IsResultStale => _resultOptions is not null && !Options.Matches(_resultOptions);

    /// <summary>Names what has moved since the run, so the notice says what re-running would change.</summary>
    public string StaleNote
    {
        get
        {
            if (!IsResultStale) return "";

            var changed = new List<string>(2);
            if (Options.Depth != _resultOptions!.Depth) changed.Add("the comparison depth");
            if (Options.IgnoreInexpressibleProperties != _resultOptions.IgnoreInexpressibleProperties)
                changed.Add("format-gap handling");

            // Matches checks every setting, including the ones no control on this screen reaches, so
            // the empty case is reachable and has to read as a sentence rather than trail off.
            var what = changed.Count switch
            {
                0 => "the options have",
                1 => $"{changed[0]} has",
                _ => $"{string.Join(" and ", changed)} have",
            };

            return $"These figures are from the previous run — {what} changed since.";
        }
    }

    /// <summary>The highlighted class. Kept across a filter change whenever it is still visible.</summary>
    public ClassMapRow? SelectedClass
    {
        get => _selectedClass;
        set => SetProperty(ref _selectedClass, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            OnPropertyChanged(nameof(HasSearchText));
            ApplyFilter();
        }
    }

    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    // ---- filter chips -------------------------------------------------------------------

    public bool ShowChanged
    {
        get => _showChanged;
        set { if (!SetProperty(ref _showChanged, value)) return; ApplyFilter(); }
    }

    public bool ShowOnlyLeft
    {
        get => _showOnlyLeft;
        set { if (!SetProperty(ref _showOnlyLeft, value)) return; ApplyFilter(); }
    }

    public bool ShowOnlyRight
    {
        get => _showOnlyRight;
        set { if (!SetProperty(ref _showOnlyRight, value)) return; ApplyFilter(); }
    }

    public bool ShowSame
    {
        get => _showSame;
        set { if (!SetProperty(ref _showSame, value)) return; ApplyFilter(); }
    }

    // ---- comparison option pass-throughs (bindable) -------------------------------------

    /// <summary>
    /// How much of each matched element to compare. Bound to the three depth buttons; the default is
    /// name and datatype, which is what decides whether two FOMs actually agree.
    /// </summary>
    public ComparisonDepth Depth
    {
        get => Options.Depth;
        set
        {
            if (Options.Depth == value) return;
            Options.Depth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsStructureDepth), nameof(IsDataTypeDepth), nameof(IsFullDepth),
                              nameof(DepthSummary), nameof(IsResultStale), nameof(StaleNote));
        }
    }

    // Bound from the three mutually exclusive buttons. Setting one to true selects that depth;
    // setting one to false is ignored, because a radio group always leaves exactly one chosen.
    public bool IsStructureDepth
    {
        get => Depth == ComparisonDepth.Structure;
        set { if (value) Depth = ComparisonDepth.Structure; }
    }

    public bool IsDataTypeDepth
    {
        get => Depth == ComparisonDepth.DataTypes;
        set { if (value) Depth = ComparisonDepth.DataTypes; }
    }

    public bool IsFullDepth
    {
        get => Depth == ComparisonDepth.Full;
        set { if (value) Depth = ComparisonDepth.Full; }
    }

    public string DepthSummary => Depth switch
    {
        ComparisonDepth.Structure => "Reporting only what exists on one side and not the other.",
        ComparisonDepth.DataTypes => "Comparing element names and datatypes. Other properties are shown but not counted.",
        _ => "Comparing every OMT property, including sharing, ownership, accuracy and prose.",
    };

    /// <summary>
    /// Hides the differences that exist only because one standard cannot express a concept. Matters
    /// most for a 1.3-versus-1516 pair, where format gaps outnumber authored changes roughly 70 to 1.
    /// </summary>
    public bool IgnoreInexpressibleProperties
    {
        get => Options.IgnoreInexpressibleProperties;
        set
        {
            if (Options.IgnoreInexpressibleProperties == value) return;
            Options.IgnoreInexpressibleProperties = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsResultStale), nameof(StaleNote));
        }
    }

    // ---- behaviour ----------------------------------------------------------------------

    /// <summary>Re-points the two pickers at the current registry contents, preserving the selection.</summary>
    public void RefreshSources(IEnumerable<FomRegistryEntry> entries)
    {
        var leftId = Left?.Id;
        var rightId = Right?.Id;

        Sources.Clear();
        foreach (var entry in entries)
            Sources.Add(entry);

        Left = Sources.FirstOrDefault(e => e.Id == leftId) ?? Sources.FirstOrDefault();
        Right = Sources.FirstOrDefault(e => e.Id == rightId)
                ?? Sources.FirstOrDefault(e => Left is null || e.Id != Left.Id);

        OnPropertyChanged(nameof(HasEnoughSources));
    }

    public bool HasEnoughSources => Sources.Count >= 2;

    private async Task CompareAsync()
    {
        if (Left is not { } left || Right is not { } right) return;

        using (BeginBusy("Comparing…"))
        {
            try
            {
                var leftId = left.Id;
                var rightId = right.Id;
                var leftLabel = left.DisplayName;
                var rightLabel = right.DisplayName;
                var options = Options.Clone();

                var comparison = await Task.Run(() =>
                {
                    var a = _repository.LoadDocument(leftId);
                    var b = _repository.LoadDocument(rightId);
                    var outcome = _comparer.Compare(a, b, options);
                    outcome.LeftLabel = leftLabel;
                    outcome.RightLabel = rightLabel;
                    return outcome;
                }).ConfigureAwait(true);

                // Assigned before Result, not after: setting Result raises FormatGapNote, which
                // reports on the run these options describe. Stamping them afterwards left that note
                // quoting the previous run's settings for the length of one property change.
                _resultOptions = options;

                Result = comparison;
                _resultRoot = comparison.Root;

                Advisories.Clear();
                foreach (var advisory in comparison.Advisories)
                    Advisories.Add(advisory);

                HasCompared = true;
                ApplyFilter();

                StatusMessage = comparison.AreIdentical
                    ? "No differences found"
                    : $"{comparison.TotalDifferences} differences";

                // The overlay carries the running total from here on. The diff is finished and its
                // figure is known, but two thirds of the wait is still ahead, and a bar that says
                // nothing for the rest of it is the reason this looked like a hang: the user cannot
                // tell a long job from a dead one. Each stage names itself and quotes what the stage
                // before it found, so the wait reads as progress rather than as a stall.
                BusyMessage = $"{StatusMessage} · mapping attribute data…";

                // Land on the attribute map. It is the view this screen exists for — what data each
                // class carries and how it is encoded on both sides — so a fresh comparison should
                // open there rather than wherever the tab strip was left.
                //
                // Awaited, not fired and forgotten. Setting IsActive would start the same rebuild as
                // a task nobody holds, and this busy scope would then lift while the map was still
                // being built — putting the user in front of the empty columns filling themselves in,
                // which is the thing the overlay exists to cover. showBusy: false because this scrim
                // already spans the whole tab strip; the map raising its own underneath would stack
                // two of them and dim the screen twice over.
                //
                // Deliberately BEFORE the save. SaveComparison is a SQLite write that can fail on its
                // own — a swapped database disposes the repository this call still holds — and it runs
                // when the results are already on screen. Selecting the tab afterwards meant a failed
                // write left the user looking at a finished comparison, an error dialog, and the wrong
                // tab. Which tab is showing is not the database's business.
                await AttributeMap.ActivateAsync(showBusy: false).ConfigureAwait(true);

                // The map's own headline, if it built one. This is the figure the screen is actually
                // for — how many attributes need remapping — and quoting it here means the user has
                // read it before the overlay lifts, not only after.
                var mapped = string.IsNullOrEmpty(AttributeMap.StatusMessage)
                    ? "Mapped attribute data"
                    : AttributeMap.StatusMessage!;

                BusyMessage = $"{mapped} · reading stored rows…";

                // Preload, not Activate: StoredRows.IsActive is TwoWay-bound to its TabItem, so
                // raising it would drag the tab strip off the attribute map at the end of every
                // comparison. This tab only has to be ready for when it is clicked.
                await StoredRows.PreloadAsync(showBusy: false).ConfigureAwait(true);

                BusyMessage = $"{mapped} · saving…";

                // Off the UI thread for the same reason as the diff: this writes the whole comparison
                // out to SQLite, and inline it froze the dispatcher — leaving the overlay on screen
                // but unable to repaint, which looks exactly like a crash.
                await Task.Run(() => _repository.SaveComparison(comparison, leftId, rightId))
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _dialogs.ShowError("Compare", $"The comparison could not be completed.\n\n{ex.Message}");
            }
        }
    }

    /// <summary>
    /// Drops the last comparison, so the tree stops answering for a pair that is no longer selected.
    /// </summary>
    /// <remarks>
    /// Called from both pickers. Without it, changing FOM B left the previous pair's differences on
    /// screen with nothing saying so — the tree, the counts and the advisories all still described
    /// the old comparison, and the only clue was that pressing Compare changed them. An empty tree
    /// saying "run a comparison" is the honest state: the answer genuinely is not known yet.
    /// </remarks>
    private void ClearComparison()
    {
        _resultOptions = null;

        Result = null;
        _resultRoot = null;
        HasCompared = false;
        SelectedClass = null;

        ClassRows.Clear();
        Advisories.Clear();

        StatusMessage = null;

        OnPropertyChanged(nameof(HasVisibleNodes), nameof(EmptyTreeMessage),
                          nameof(IsResultStale), nameof(StaleNote));
    }

    /// <summary>Re-applies the chips and the search to the flattened class list.</summary>
    private void ApplyFilter()
    {
        var previous = SelectedClass?.QualifiedName;

        ClassRows.Clear();
        foreach (var row in _classes.Rows.Where(Matches))
            ClassRows.Add(row);

        OnPropertyChanged(nameof(HasVisibleNodes), nameof(EmptyTreeMessage));

        SelectedClass = previous is null
            ? null
            : ClassRows.FirstOrDefault(r => r.QualifiedName == previous);
    }

    private bool Matches(ClassMapRow row)
    {
        var statusOk = row.Status switch
        {
            ClassMapStatus.Changed => ShowChanged,
            ClassMapStatus.OnlyInLeft => ShowOnlyLeft,
            ClassMapStatus.OnlyInRight => ShowOnlyRight,
            _ => ShowSame,
        };

        if (!statusOk) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var needle = SearchText.Trim();
        return Contains(row.Name, needle)
            || Contains(row.LeftName, needle)
            || Contains(row.RightName, needle)
            || Contains(row.Why, needle);

        static bool Contains(string? haystack, string needle) =>
            haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public bool HasVisibleNodes => ClassRows.Count > 0;

    public string EmptyTreeMessage =>
        !HasCompared ? "Press Compare to see how the classes line up."
        : _classes.Rows.Count == 0 ? "Neither FOM declares a single object or interaction class."
        : "No classes match the current filters.";

    private void Swap()
    {
        (Left, Right) = (Right, Left);
        StatusMessage = "Swapped A and B";
    }

    private static string Describe(FomRegistryEntry? entry) =>
        entry is null
            ? "—"
            : $"{entry.StandardDisplayName} · {entry.ObjectClassCount} classes · {entry.AttributeCount} attributes · " +
              $"{entry.InteractionClassCount} interactions · {entry.DataTypeCount} datatypes";
}
