using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.Views;
using HLAFomReader.Core.Merging;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Registry;
using HLAFomReader.Core.Serialization;

namespace HLAFomReader.App.ViewModels;

/// <summary>One chip in the standard filter row.</summary>
public sealed class StandardFilter : ObservableObject
{
    private bool _isActive;
    private int _count;

    public StandardFilter(string label, FomStandard? standard, bool errorsOnly = false)
    {
        Label = label;
        Standard = standard;
        ErrorsOnly = errorsOnly;
    }

    public string Label { get; }

    /// <summary>Null means "all standards".</summary>
    public FomStandard? Standard { get; }

    /// <summary>When true this chip selects entries that failed to parse cleanly, regardless of standard.</summary>
    public bool ErrorsOnly { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public bool Matches(FomRegistryEntry entry) =>
        ErrorsOnly ? entry.HasErrors
        : Standard is null || entry.Standard == Standard.Value;
}

/// <summary>
/// The Registry screen: register FOM/FED files into the SQLite store, then inspect exactly what
/// the parser understood.
/// </summary>
public sealed class RegistryViewModel : ViewModelBase
{
    private readonly IFomRepository _repository;
    private readonly IDialogService _dialogs;

    private FomRegistryEntry? _selectedEntry;
    private FomDocument? _selectedDocument;
    private string _searchText = "";
    private StandardFilter _activeFilter;
    private string _lastRegistrationSummary = "";

    public RegistryViewModel(IFomRepository repository, IDialogService dialogs)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        Filters = new ObservableCollection<StandardFilter>
        {
            new("All", null),
            new("HLA 1.3", FomStandard.Hla13),
            new("1516-2000", FomStandard.Ieee1516_2000),
            new("Evolved", FomStandard.Ieee1516_2010),
            new("1516-2025", FomStandard.Ieee1516_2025),
            new("With errors", null, errorsOnly: true),
        };

        _activeFilter = Filters[0];
        _activeFilter.IsActive = true;

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = FilterEntry;

        RegisterCommand = new AsyncRelayCommand(RegisterAsync);
        ReparseCommand = new AsyncRelayCommand(ReparseSelectedAsync, () => SelectedEntry is not null);
        UnregisterCommand = new RelayCommand(UnregisterSelected, () => SelectedEntry is not null);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        SetFilterCommand = new RelayCommand<StandardFilter>(ApplyFilter);
        OpenContainingFolderCommand = new RelayCommand(OpenContainingFolder, () => SelectedEntry is not null);
        CopyPathCommand = new RelayCommand(CopyPath, () => SelectedEntry is not null);
        OpenDetailCommand = new RelayCommand(OpenDetail, () => SelectedEntry is not null);
    }

    /// <summary>Raised whenever the set of registered FOMs changes, so the shell and Compare screen refresh.</summary>
    public event EventHandler? RegistryChanged;

    /// <summary>
    /// Raised when the user asks to inspect one FOM in full — by double-clicking its row. The shell
    /// answers by swapping in the detail screen; this view model does not own navigation.
    /// </summary>
    public event EventHandler<FomRegistryEntry>? DetailRequested;

    public ObservableCollection<FomRegistryEntry> Entries { get; } = new();
    public ICollectionView EntriesView { get; }
    public ObservableCollection<StandardFilter> Filters { get; }
    public ObservableCollection<FomTreeNode> Structure { get; } = new();
    public ObservableCollection<ParseDiagnostic> Diagnostics { get; } = new();

    public AsyncRelayCommand RegisterCommand { get; }
    public AsyncRelayCommand ReparseCommand { get; }
    public RelayCommand UnregisterCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand<StandardFilter> SetFilterCommand { get; }
    public RelayCommand OpenContainingFolderCommand { get; }
    public RelayCommand CopyPathCommand { get; }

    /// <summary>Opens the full-width explorer for the selected entry. Bound to row double-click.</summary>
    public RelayCommand OpenDetailCommand { get; }

    public string Title => "Registered FOMs";

    public FomRegistryEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!SetProperty(ref _selectedEntry, value)) return;

            LoadSelectedDocument();
            ReparseCommand.RaiseCanExecuteChanged();
            UnregisterCommand.RaiseCanExecuteChanged();
            OpenContainingFolderCommand.RaiseCanExecuteChanged();
            CopyPathCommand.RaiseCanExecuteChanged();
            OpenDetailCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedEntry is not null;

    /// <summary>The fully rehydrated document for <see cref="SelectedEntry"/>, read back from SQLite.</summary>
    public FomDocument? SelectedDocument
    {
        get => _selectedDocument;
        private set => SetProperty(ref _selectedDocument, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                EntriesView.Refresh();
                OnPropertyChanged(nameof(HasSearchText), nameof(ResultSummary));
            }
        }
    }

    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    public StandardFilter ActiveFilter
    {
        get => _activeFilter;
        private set => SetProperty(ref _activeFilter, value);
    }

    /// <summary>Trailing text on the filter row, e.g. "6 of 9 FOMs · 3 standards".</summary>
    public string ResultSummary
    {
        get
        {
            var shown = EntriesView.Cast<object>().Count();
            var standards = Entries.Select(e => e.Standard).Distinct().Count();
            return Entries.Count == 0
                ? "Nothing registered yet"
                : $"{shown} of {Entries.Count} FOM{(Entries.Count == 1 ? "" : "s")} · {standards} standard{(standards == 1 ? "" : "s")}";
        }
    }

    public string LastRegistrationSummary
    {
        get => _lastRegistrationSummary;
        private set => SetProperty(ref _lastRegistrationSummary, value);
    }

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>Reloads every entry from the database and re-checks the files on disk.</summary>
    public void Load()
    {
        var previouslySelected = SelectedEntry?.Id;

        var entries = _repository.ListEntries();
        _repository.RefreshFileState(entries);

        Entries.Clear();
        foreach (var entry in entries)
            Entries.Add(entry);

        UpdateFilterCounts();
        EntriesView.Refresh();

        SelectedEntry = previouslySelected is { } id
            ? Entries.FirstOrDefault(e => e.Id == id) ?? Entries.FirstOrDefault()
            : Entries.FirstOrDefault();

        OnPropertyChanged(nameof(IsEmpty), nameof(ResultSummary));
        RegistryChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RegisterAsync()
    {
        var chosen = _dialogs.RequestRegistrations();
        if (chosen is null || chosen.Count == 0) return;

        // Compiling comes first and separately, because it produces a file that did not exist a
        // moment ago. Everything past this line is registering files off disk, which is what it
        // always was.
        var requests = await CompileAndSaveAsync(chosen).ConfigureAwait(true);
        if (requests.Count == 0) return;

        using (BeginBusy($"Parsing {requests.Count} FOM{(requests.Count == 1 ? "" : "s")}…"))
        {
            var outcomes = await Task.Run(() => RegisterRequests(requests)).ConfigureAwait(true);

            LastRegistrationSummary = BuildRegistrationSummary(outcomes);
            StatusMessage = LastRegistrationSummary;

            ReportFailures(outcomes);
        }

        Load();

        // Land the user on something they just registered.
        var firstNew = Entries.FirstOrDefault(e =>
            requests.Any(r => string.Equals(r.PrimaryPath, e.FilePath, StringComparison.OrdinalIgnoreCase)));
        if (firstNew is not null) SelectedEntry = firstNew;
    }

    /// <summary>
    /// Turns each compiled request into an ordinary one by building the model its modules describe
    /// and writing it out as a file the user names and places.
    /// </summary>
    /// <returns>
    /// The requests to go on and register. A compile the user cancelled or that failed is dropped
    /// from the list rather than registered from its modules.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The compiled FOM becomes a real file for a reason that outlasts this method. Registering the
    /// merge from memory left the entry's path pointing at the last module, so the registry said a
    /// model was a file that did not contain it: re-parsing that entry would quietly reload one
    /// module in place of the whole model, and every count on screen would drop without anything
    /// having gone wrong. Saving it makes the entry's file and the entry's contents the same thing
    /// again, and a re-parse re-reads the compiled model because that is now what is on disk.
    /// </para>
    /// <para>
    /// The prompt comes after the merge, not before it, so nobody is asked where to put a file that
    /// then turns out not to be buildable.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<FomRegistrationRequest>> CompileAndSaveAsync(
        IReadOnlyList<FomRegistrationRequest> requests)
    {
        if (!requests.Any(r => r.IsCompiled)) return requests;

        var resolved = new List<FomRegistrationRequest>(requests.Count);

        foreach (var request in requests)
        {
            if (!request.IsCompiled)
            {
                resolved.Add(request);
                continue;
            }

            var modulePaths = request.ModulePaths!;
            FomModuleMergeResult merged;

            using (BeginBusy($"Compiling {modulePaths.Count} modules…"))
            {
                try
                {
                    merged = await Task.Run(() =>
                    {
                        var modules = modulePaths.Select(FomFileReader.ParseFile).ToList();
                        var result = FomModuleMerger.Merge(modules);

                        // The merge inherits the last module's identification, dependency
                        // references included, so without this the compiled file would go on asking
                        // for the very modules it now contains.
                        FomModuleMerger.StampAsCompiled(result.Document, request.DisplayName,
                            modulePaths.Select(Path.GetFileName).ToList()!);

                        return result;
                    }).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _dialogs.ShowError("Compile",
                        $"The modules could not be compiled into one FOM.\n\n{Describe(ex)}");
                    continue;
                }
            }

            // Two modules defining the same element differently is a problem with the module set,
            // not with the merge. First-writer-wins already resolved it; saying so is the only way
            // the user finds out that one of the two definitions is not in the file they are about
            // to save.
            if (merged.Conflicts.Count > 0 && !ConfirmDespiteConflicts(merged.Conflicts)) continue;

            var target = _dialogs.SaveFile(
                "Save the compiled FOM",
                "IEEE 1516 FOM (*.xml)|*.xml|All files (*.*)|*.*",
                SuggestedFileName(request),
                "xml");

            if (target is null) continue;

            try
            {
                await Task.Run(() => Ieee1516XmlWriter.Write(merged.Document, target)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _dialogs.ShowError("Save compiled FOM",
                    $"The compiled FOM could not be written to {Path.GetFileName(target)}.\n\n{Describe(ex)}");
                continue;
            }

            // From here it is a FOM file like any other, and is registered as one. Nothing
            // downstream needs to know it was compiled, because nothing downstream has to reassemble
            // it — the file on disk is the whole model.
            resolved.Add(new FomRegistrationRequest(
                false, target, null, request.DisplayName,
                ModulePaths: null,
                ComposedFrom: modulePaths.Select(Path.GetFileName).ToList()!));
        }

        return resolved;
    }

    private bool ConfirmDespiteConflicts(IReadOnlyList<string> conflicts)
    {
        var shown = string.Join("\n", conflicts.Take(8));
        var more = conflicts.Count > 8 ? $"\n\n…and {conflicts.Count - 8} more." : "";

        return _dialogs.Confirm(
            conflicts.Count == 1 ? "One module conflict" : $"{conflicts.Count} module conflicts",
            "These modules define the same element in incompatible ways. The earlier module wins "
            + "each one, so the later definition will not be in the saved FOM.\n\n"
            + shown + more + "\n\nSave it anyway?");
    }

    /// <summary>The file name the save dialog opens with, taken from the name the user gave.</summary>
    /// <remarks>
    /// Characters a path cannot hold are dropped rather than substituted. A FOM called "RPR 2.0 /
    /// NETN" is not improved by becoming "RPR 2.0 _ NETN"; the user is standing in front of the save
    /// dialog and can type whatever they want.
    /// </remarks>
    private static string SuggestedFileName(FomRegistrationRequest request)
    {
        var name = new string((request.DisplayName ?? "")
            .Where(c => !Path.GetInvalidFileNameChars().Contains(c))
            .ToArray())
            .Trim();

        if (name.Length == 0)
            name = Path.GetFileNameWithoutExtension(request.PrimaryPath) + "-compiled";

        return name + ".xml";
    }

    /// <summary>
    /// Parses and stores each requested registration. An HLA 1.3 request may name two files, in
    /// which case the FED supplies the structure and the OMT supplies the datatypes and they are
    /// merged into a single entry — neither 1.3 file is complete on its own.
    /// </summary>
    private List<RegistrationOutcome> RegisterRequests(IReadOnlyList<FomRegistrationRequest> requests)
    {
        var outcomes = new List<RegistrationOutcome>();

        foreach (var request in requests)
        {
            try
            {
                var document = FomFileReader.ParseFile(request.PrimaryPath);
                string? companionPath = null;

                // A compiled request never reaches here: CompileAndSaveAsync has already built the
                // model, written it out and handed back a request naming that file. What arrives is
                // always a file on disk holding the whole model.
                if (!string.IsNullOrWhiteSpace(request.CompanionPath))
                {
                    // Not the same operation, despite also being a merge. An HLA 1.3 FED and its OMT
                    // are two views of one model — structure and meaning — reconciled into a whole.
                    // HLA 1.3 has no module concept at all; modules arrived with IEEE 1516-2010, so
                    // a 1.3 registration never carries a module list.
                    var companion = FomFileReader.ParseFile(request.CompanionPath!);
                    var merged = FomMerger.Merge(document, companion);

                    document = merged.Document;
                    companionPath = request.CompanionPath;
                }

                // The readers are deliberately forgiving, which is right for a damaged vendor file
                // that can still be mostly recovered — but a parse that recovered *nothing* is a
                // file that could not be read, and storing it produces an entry with no model in it
                // that nevertheless looks registered.
                if (WhyUnusable(document) is { } reason)
                {
                    outcomes.Add(new RegistrationOutcome(request.PrimaryPath, null, null, reason));
                    continue;
                }

                // A FOM that reaches for datatypes nothing defines is a module missing the modules
                // it was written against. Registering it anyway is what produced a screen reporting
                // an absent module's attributes as deletions somebody had authored, so it is refused
                // here with the names that say which module to go and add.
                if (FomCompleteness.Check(document) is { IsComplete: false } completeness)
                {
                    var names = completeness.MissingDataTypes.Take(6).Select(m => m.DataType);
                    outcomes.Add(new RegistrationOutcome(request.PrimaryPath, null, null,
                        $"{completeness.Summary} Undefined: {string.Join(", ", names)}"
                        + (completeness.MissingDataTypes.Count > 6 ? ", …" : "")));
                    continue;
                }

                var displayName = !string.IsNullOrWhiteSpace(request.DisplayName)
                    ? request.DisplayName!
                    : !string.IsNullOrWhiteSpace(document.Identification.Name)
                        ? document.Identification.Name!
                        : Path.GetFileNameWithoutExtension(request.PrimaryPath);

                var entry = _repository.Register(
                    document, displayName, request.PrimaryPath, companionPath, request.ComposedFrom);
                outcomes.Add(new RegistrationOutcome(request.PrimaryPath, entry, document, null));
            }
            catch (Exception ex)
            {
                outcomes.Add(new RegistrationOutcome(request.PrimaryPath, null, null, Describe(ex)));
            }
        }

        return outcomes;
    }

    /// <summary>
    /// Why <paramref name="document"/> cannot stand as a registration, or null when it can.
    /// </summary>
    /// <remarks>
    /// The distinction is between a file that was read and found wanting, and a file that was not
    /// read at all. The readers recover what they can and report the rest as diagnostics — a FOM
    /// with a broken block still yields every class outside it, and that entry is worth keeping.
    /// A parse that produced errors and <em>no model whatsoever</em> is the other thing: a locked
    /// file, a file that is not a FOM, a file replaced by something else. Storing that gives an
    /// entry that lists as registered and holds nothing, which is worse than refusing it.
    /// </remarks>
    private static string? WhyUnusable(FomDocument document)
    {
        var empty = document.ObjectClassCount == 0
                 && document.InteractionClassCount == 0
                 && document.DataTypeCount == 0
                 && document.DimensionCount == 0;

        if (!empty || !document.HasErrors) return null;

        var first = document.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        return first is null
            ? "Nothing could be read from this file."
            : $"Nothing could be read from this file.\n\n{first.Message}";
    }

    /// <summary>
    /// Says why a registration failed, rather than only how many did.
    /// </summary>
    /// <remarks>
    /// A count in the status bar is not a report. "1 failed" is indistinguishable from the app
    /// ignoring the click, which is exactly how a locked file, a moved file or a file that no longer
    /// parses used to present — the reason was caught, counted and thrown away. Registering is a
    /// deliberate action, so a failure has to be shown, and it has to name the file and the cause.
    /// </remarks>
    private void ReportFailures(IReadOnlyList<RegistrationOutcome> outcomes)
    {
        var failures = outcomes.Where(o => o.Entry is null).ToList();
        if (failures.Count == 0) return;

        var detail = string.Join("\n\n", failures.Select(f =>
            $"{Path.GetFileName(f.Path)}\n{f.Error}"));

        _dialogs.ShowError(
            failures.Count == 1 ? "Could not register this FOM" : $"Could not register {failures.Count} FOMs",
            detail);
    }

    private static string BuildRegistrationSummary(IReadOnlyList<RegistrationOutcome> outcomes)
    {
        var ok = outcomes.Count(o => o.Entry is not null && o.Document?.HasErrors != true);
        var withErrors = outcomes.Count(o => o.Entry is not null && o.Document?.HasErrors == true);
        var failed = outcomes.Count(o => o.Entry is null);

        var parts = new List<string>();
        if (ok > 0) parts.Add($"{ok} registered");
        if (withErrors > 0) parts.Add($"{withErrors} registered with parse errors");
        if (failed > 0) parts.Add($"{failed} failed");

        return parts.Count == 0 ? "Nothing registered" : string.Join(" · ", parts);
    }

    private async Task ReparseSelectedAsync()
    {
        if (SelectedEntry is not { } entry) return;

        if (!File.Exists(entry.FilePath))
        {
            _dialogs.ShowError("Re-parse", $"The source file is no longer on disk:\n\n{entry.FilePath}");
            return;
        }

        // A 1.3 entry is only whole as a pair. Re-parsing the FED alone would strip the datatypes
        // the OMT contributed, so a companion that has gone missing is refused rather than obeyed.
        if (entry.IsPair && !File.Exists(entry.CompanionPath!))
        {
            _dialogs.ShowError("Re-parse",
                $"This entry was built from two files, and the second one is no longer on disk:\n\n{entry.CompanionPath}\n\n"
                + "Re-parsing the FED on its own would drop every datatype the OMT supplied. "
                + "Restore the file, or register the FOM again.");
            return;
        }

        using (BeginBusy($"Re-parsing {entry.FileName}…"))
        {
            var path = entry.FilePath;
            var name = entry.DisplayName;
            var companionPath = entry.CompanionPath;

            string? refusal;

            try
            {
                refusal = await Task.Run(() =>
                {
                    var document = FomFileReader.ParseFile(path);

                    // An entry built from a FED and its OMT has to be rebuilt from both, otherwise
                    // re-parsing quietly strips the datatypes the OMT contributed.
                    if (!string.IsNullOrWhiteSpace(companionPath))
                        document = FomMerger.Merge(document, FomFileReader.ParseFile(companionPath!)).Document;

                    // Checked *before* storing. Register replaces the row, so a parse that read
                    // nothing would otherwise destroy the good copy already in the registry and
                    // leave an entry with no model in it — while reporting success.
                    if (WhyUnusable(document) is { } reason) return reason;

                    _repository.Register(document, name, path, companionPath);
                    return null;
                }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Same reasoning as ReportFailures: a re-parse that fails silently is
                // indistinguishable from a button that does nothing.
                _dialogs.ShowError("Re-parse", $"{entry.FileName} could not be re-parsed.\n\n{Describe(ex)}");
                StatusMessage = $"Could not re-parse {entry.FileName}";
                return;
            }

            if (refusal is not null)
            {
                _dialogs.ShowError("Re-parse",
                    $"{entry.FileName} could not be re-parsed, so the copy already in the registry has been kept.\n\n{refusal}");
                StatusMessage = $"Could not re-parse {entry.FileName}";
                return;
            }
        }

        StatusMessage = $"Re-parsed {entry.FileName}";
        Load();
    }

    private void UnregisterSelected()
    {
        if (SelectedEntry is not { } entry) return;

        if (!_dialogs.Confirm("Unregister FOM",
                $"Remove \"{entry.DisplayName}\" from the registry?\n\nThe source file on disk is not deleted."))
            return;

        try
        {
            _repository.Delete(entry.Id);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Unregister", $"{entry.DisplayName} could not be removed.\n\n{Describe(ex)}");
            return;
        }

        StatusMessage = $"Unregistered {entry.DisplayName}";
        Load();
    }

    /// <summary>
    /// Turns an exception into the sentence a user can act on — the innermost message, which is the
    /// one that names the real problem, with the type in front of it when the message alone is not
    /// self-explanatory.
    /// </summary>
    private static string Describe(Exception exception)
    {
        var root = exception;
        while (root is AggregateException { InnerExceptions.Count: 1 } aggregate)
            root = aggregate.InnerExceptions[0];

        var message = root.Message;

        // A wrapped failure usually names its cause one level down: "Could not read the file" says
        // far less than the IOException underneath it.
        if (root.InnerException is { } inner && !message.Contains(inner.Message, StringComparison.Ordinal))
            message = $"{message}\n\n{inner.Message}";

        return $"{root.GetType().Name}: {message}";
    }

    private void LoadSelectedDocument()
    {
        Structure.Clear();
        Diagnostics.Clear();

        if (SelectedEntry is not { } entry)
        {
            SelectedDocument = null;
            return;
        }

        try
        {
            var document = _repository.LoadDocument(entry.Id);
            SelectedDocument = document;

            foreach (var node in FomTreeNode.Build(document))
                Structure.Add(node);

            foreach (var diagnostic in document.Diagnostics.OrderByDescending(d => d.Severity))
                Diagnostics.Add(diagnostic);
        }
        catch (Exception ex)
        {
            SelectedDocument = null;
            Diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error,
                $"Could not read this FOM back from the registry database: {ex.Message}"));
        }

        OnPropertyChanged(nameof(SelectedDocument));
    }

    private void ApplyFilter(StandardFilter? filter)
    {
        if (filter is null) return;

        foreach (var candidate in Filters)
            candidate.IsActive = ReferenceEquals(candidate, filter);

        ActiveFilter = filter;
        EntriesView.Refresh();
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void UpdateFilterCounts()
    {
        foreach (var filter in Filters)
            filter.Count = Entries.Count(filter.Matches);
    }

    private bool FilterEntry(object item)
    {
        if (item is not FomRegistryEntry entry) return false;
        if (!ActiveFilter.Matches(entry)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var needle = SearchText.Trim();
        return Contains(entry.DisplayName, needle)
            || Contains(entry.FileName, needle)
            || Contains(entry.FilePath, needle)
            || Contains(entry.IdentificationName, needle)
            || Contains(entry.Version, needle)
            || Contains(entry.StandardDisplayName, needle)
            || Contains(entry.ApplicationDomain, needle);
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void OpenContainingFolder()
    {
        if (SelectedEntry is not { } entry) return;

        var directory = Path.GetDirectoryName(entry.FilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            _dialogs.ShowError("Open folder", $"The folder no longer exists:\n\n{directory}");
            return;
        }

        var argument = File.Exists(entry.FilePath) ? $"/select,\"{entry.FilePath}\"" : $"\"{directory}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
    }

    private void OpenDetail()
    {
        if (SelectedEntry is { } entry)
            DetailRequested?.Invoke(this, entry);
    }

    private void CopyPath()
    {
        if (SelectedEntry is not { } entry) return;

        try
        {
            System.Windows.Clipboard.SetText(entry.FilePath);
            StatusMessage = "Path copied to clipboard";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Copy path", ex.Message);
        }
    }

    private sealed record RegistrationOutcome(
        string Path,
        FomRegistryEntry? Entry,
        FomDocument? Document,
        string? Error);
}
