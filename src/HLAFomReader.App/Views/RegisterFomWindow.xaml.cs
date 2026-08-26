using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using Microsoft.Win32;
using HLAFomReader.App.Infrastructure;

namespace HLAFomReader.App.Views;

/// <summary>
/// One registration the user asked for in <see cref="RegisterFomWindow"/>.
/// </summary>
/// <param name="IsHla13">True for an HLA 1.3 federation, false for HLA Evolved / IEEE 1516.</param>
/// <param name="PrimaryPath">The <c>.fed</c> for 1.3, or the <c>.xml</c> for Evolved.</param>
/// <param name="CompanionPath">The <c>.omt</c>/<c>.omd</c> object model that carries the datatypes
/// for an HLA 1.3 federation. <c>null</c> for Evolved, and also for 1.3 when the user chose to
/// register the FED on its own.</param>
/// <param name="DisplayName">Name to register the model under, or <c>null</c> to use the model's
/// own name as parsed out of the file.</param>
/// <param name="ModulePaths">
/// When set, the ordered list of IEEE 1516 FOM modules to compile into this one entry — bases
/// first, exactly as the module list handed to <c>createFederationExecution</c>. <c>PrimaryPath</c>
/// is the last of them, which is the file the entry is filed and named under. <c>null</c> for a
/// registration built from a single file.
/// </param>
/// <param name="ComposedFrom">
/// The modules this registration's file was compiled from, in compile order — file names, not
/// paths. Set once the compile has been written out, at which point <c>PrimaryPath</c> names the
/// saved model rather than a module. Deliberately separate from <c>ModulePaths</c>: that one says
/// "compile these", this one says "remember it came from these", and a request that has been
/// compiled must not look like a request still asking to be.
/// </param>
public sealed record FomRegistrationRequest(
    bool IsHla13,
    string PrimaryPath,
    string? CompanionPath,
    string? DisplayName,
    IReadOnlyList<string>? ModulePaths = null,
    IReadOnlyList<string>? ComposedFrom = null)
{
    /// <summary>True when this registration compiles several modules into one FOM.</summary>
    public bool IsCompiled => ModulePaths is { Count: > 1 };
}

/// <summary>
/// Guided dialog for adding FOMs to the registry: it asks which standard the model follows and
/// then collects the file — or, for HLA 1.3, the pair of files — that describes it.
/// </summary>
/// <remarks>
/// This holds dialog plumbing only. It never parses or stores anything; the single call into
/// <see cref="FomFileReader.DetectStandard"/> exists purely to stop an obviously wrong file being
/// handed over as an OMT. Everything else is the caller's job, driven by the returned requests.
/// </remarks>
/// <summary>One module in the merge-order list, numbered as the user sees it.</summary>
/// <param name="Position">1-based place in the merge order.</param>
/// <param name="FileName">File name alone, which is what distinguishes modules from each other.</param>
/// <param name="FullPath">The path, kept for the tooltip and for the request.</param>
public sealed record ModuleRow(int Position, string FileName, string FullPath);

public sealed partial class RegisterFomWindow : Window
{
    /// <summary>The FED half of HLA 1.3, as the reader describes it.</summary>
    private static readonly FomFileFormat FedFormat =
        FomFileReader.SupportedFormats.First(f => f.Standard == FomStandard.Hla13 && f.HasExtension(".fed"));

    /// <summary>The OMT half of HLA 1.3; its extensions also drive the auto-pairing search.</summary>
    private static readonly FomFileFormat OmtFormat =
        FomFileReader.SupportedFormats.First(f => f.Standard == FomStandard.Hla13 && f.HasExtension(".omt"));

    /// <summary>Every file selected in the Evolved row, in the order the dialog returned them.</summary>
    private readonly List<string> _evolvedPaths = new();

    /// <summary>
    /// Why the chosen OMT cannot be used, or <c>null</c> when there is no OMT or it is fine.
    /// Cached because working it out touches the disk, while the state refresh does not.
    /// </summary>
    private string? _omtError;

    /// <summary>True while the OMT box holds a path this dialog filled in rather than one the user browsed to.</summary>
    private bool _omtWasAutoPaired;

    /// <summary>What the user asked for, or <c>null</c> while the dialog is open or was cancelled.</summary>
    private IReadOnlyList<FomRegistrationRequest>? Requests { get; set; }

    private RegisterFomWindow()
    {
        InitializeComponent();
        UpdateState();
    }

    // ---------------------------------------------------------------- events

    private void Standard_Checked(object sender, RoutedEventArgs e) => UpdateState();

    private void ModuleList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateMoveButtons();

    private void Name_Changed(object sender, TextChangedEventArgs e) => UpdateState();

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(+1);

    /// <summary>
    /// Moves the selected module one place through the merge order.
    /// </summary>
    /// <remarks>
    /// The order is the input, not a presentation detail: an RTI merges a module list in the order
    /// it is given, so a base placed after the module extending it produces a different — and wrong
    /// — result. Selection follows the row so a module can be walked several places with repeated
    /// clicks rather than re-found after each one.
    /// </remarks>
    private void Move(int delta)
    {
        var index = ModuleList.SelectedIndex;
        var target = index + delta;

        if (index < 0 || target < 0 || target >= _evolvedPaths.Count) return;

        (_evolvedPaths[index], _evolvedPaths[target]) = (_evolvedPaths[target], _evolvedPaths[index]);

        RebuildModuleList();
        ModuleList.SelectedIndex = target;
    }

    private void BrowseFed_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the HLA 1.3 FED file",
            Filter = BuildFilter("HLA 1.3 FED file", FedFormat),
            InitialDirectory = StartingDirectory(FedPathBox.Text),
            CheckFileExists = true,
            CheckPathExists = true,
        };

        if (ModalScrim.ShowModal(dialog, this) != true) return;

        SetPath(FedPathBox, dialog.FileName);

        // Auto-pairing: the OMT almost always sits next to the FED under the same base name, so
        // offer it rather than making the user find the second half of a pair they already have.
        // A path the user browsed to themselves is never overwritten.
        if (OmtPathBox.Text.Length == 0 || _omtWasAutoPaired)
        {
            var companion = FindCompanionOmt(dialog.FileName);
            SetPath(OmtPathBox, companion ?? "");
            _omtWasAutoPaired = companion is not null;
        }

        ValidateOmt();
        UpdateState();
    }

    private void BrowseOmt_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the HLA 1.3 OMT object model",
            Filter = BuildFilter("HLA 1.3 OMT object model", OmtFormat),
            InitialDirectory = StartingDirectory(OmtPathBox.Text.Length != 0 ? OmtPathBox.Text : FedPathBox.Text),
            CheckFileExists = true,
            CheckPathExists = true,
        };

        if (ModalScrim.ShowModal(dialog, this) != true) return;

        SetPath(OmtPathBox, dialog.FileName);
        _omtWasAutoPaired = false;

        ValidateOmt();
        UpdateState();
    }

    private void ClearOmt_Click(object sender, RoutedEventArgs e)
    {
        SetPath(OmtPathBox, "");
        _omtWasAutoPaired = false;

        ValidateOmt();
        UpdateState();
    }

    private void BrowseEvolved_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the HLA Evolved / IEEE 1516 FOM",
            Filter = BuildFilter(
                "IEEE 1516 / HLA Evolved FOM",
                FomFileReader.SupportedFormats.Where(f => f.Standard != FomStandard.Hla13).ToArray()),
            InitialDirectory = StartingDirectory(_evolvedPaths.FirstOrDefault()),
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = true,
        };

        if (ModalScrim.ShowModal(dialog, this) != true) return;

        _evolvedPaths.Clear();
        _evolvedPaths.AddRange(dialog.FileNames);
        RebuildModuleList();

        UpdateState();
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        // The button is only enabled when this holds, but re-checking costs nothing and keeps
        // Enter-on-default from ever producing a half-filled request.
        if (!IsValid()) return;

        Requests = BuildRequests(
            Hla13Choice.IsChecked == true,
            FedPathBox.Text,
            OmtPathBox.Text.Length == 0 ? null : OmtPathBox.Text,
            _evolvedPaths,
            string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim());

        DialogResult = true;
    }

    /// <summary>
    /// The registrations a given selection describes. Always exactly one.
    /// </summary>
    /// <remarks>
    /// Separated from the click handler so the rule it encodes can be checked without a window —
    /// setting <see cref="Window.DialogResult"/> throws on anything that was not shown as a dialog,
    /// which puts the assertion on the far side of an exception. The rule is worth pinning because
    /// changing it is silent: several 1516 files are the modules of one FOM, compiled in the order
    /// given, and never one entry each. A registry of modules looks exactly like a registry of small
    /// FOMs, and only reveals itself as wrong once something is compared against one of them.
    /// </remarks>
    private static FomRegistrationRequest[] BuildRequests(
        bool isHla13,
        string fedPath,
        string? omtPath,
        IReadOnlyList<string> evolvedPaths,
        string? name)
    {
        if (isHla13)
            return new[] { new FomRegistrationRequest(true, fedPath, omtPath, name) };

        // PrimaryPath is the last path because the merged model takes its identity from the last
        // module, and that is the file the entry is filed under. ModulePaths stays null for a single
        // file so nothing downstream treats a lone FOM as a one-module compile.
        return new[]
        {
            new FomRegistrationRequest(
                false, evolvedPaths[^1], null, name,
                evolvedPaths.Count > 1 ? evolvedPaths.ToArray() : null),
        };
    }

    /// <summary>Refills the order list from the current paths, keeping the numbers in step.</summary>
    private void RebuildModuleList()
    {
        ModuleList.ItemsSource = _evolvedPaths
            .Select((path, index) => new ModuleRow(index + 1, System.IO.Path.GetFileName(path), path))
            .ToList();
    }

    private void UpdateMoveButtons()
    {
        var index = ModuleList.SelectedIndex;
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < _evolvedPaths.Count - 1;
    }

    /// <summary>
    /// True when the selection is a module list rather than a single file.
    /// </summary>
    /// <remarks>
    /// Several 1516 files chosen at once are the modules of one FOM, always. This used to be a
    /// question put to the user, with registering them separately as the other answer; that answer
    /// produced a registry of modules, and a module on its own is not a small FOM but a misleading
    /// one. Registering unrelated FOMs is one file at a time.
    /// </remarks>
    private bool IsCompiling => Hla13Choice.IsChecked != true && _evolvedPaths.Count > 1;

    /// <summary>Closing from the caption button leaves <see cref="Window.DialogResult"/> null, which reads as a cancel.</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- state

    /// <summary>
    /// Brings every conditional part of the window into line with the current selection: which
    /// standard's file rows are on show, which notes apply, and whether Register can be pressed.
    /// </summary>
    private void UpdateState()
    {
        // Checked fires while the XAML is still being loaded, before the later named elements exist.
        if (!IsInitialized) return;

        var isHla13 = Hla13Choice.IsChecked == true;

        Hla13Files.Visibility = isHla13 ? Visibility.Visible : Visibility.Collapsed;
        EvolvedFiles.Visibility = isHla13 ? Visibility.Collapsed : Visibility.Visible;

        // --- HLA 1.3 annotations
        var hasOmt = OmtPathBox.Text.Length != 0;
        ClearOmtButton.IsEnabled = hasOmt;
        OmtPairedNote.Visibility = hasOmt && _omtWasAutoPaired ? Visibility.Visible : Visibility.Collapsed;
        OmtMissingWarning.Visibility = hasOmt ? Visibility.Collapsed : Visibility.Visible;
        ShowText(Hla13Error, _omtError);

        // --- Evolved annotations
        if (_evolvedPaths.Count > 1)
        {
            // The box cannot show several paths, so it shows what they have in common and the
            // note carries the names; the tooltip keeps the full list one hover away.
            var folder = DirectoryOf(_evolvedPaths[0]);
            SetPath(EvolvedPathBox, folder.Length != 0 ? folder : _evolvedPaths[0]);

            var names = _evolvedPaths.Select(System.IO.Path.GetFileName);
            EvolvedCountNote.Text = $"{_evolvedPaths.Count} files selected — {string.Join(", ", names)}";
            EvolvedCountNote.ToolTip = string.Join(Environment.NewLine, _evolvedPaths);
            EvolvedCountNote.Visibility = Visibility.Visible;
        }
        else
        {
            SetPath(EvolvedPathBox, _evolvedPaths.Count == 1 ? _evolvedPaths[0] : "");
            EvolvedCountNote.Visibility = Visibility.Collapsed;
        }

        // --- Compiling several modules into one FOM.
        //
        // HLA 1.3 never offers this. Modules arrived with IEEE 1516-2010; a 1.3 federation loads one
        // FED and that is the whole model. The FED/OMT pairing above is a different operation that
        // happens to also be a merge — two views of one model rather than several models — and
        // conflating them would suggest a 1.3 FOM could be assembled from parts, which it cannot.
        var compiling = IsCompiling;
        CompileSection.Visibility = compiling ? Visibility.Visible : Visibility.Collapsed;

        if (compiling)
        {
            if (ModuleList.SelectedIndex < 0) ModuleList.SelectedIndex = 0;
            UpdateMoveButtons();
        }

        // The name box is always shown — every registration this dialog produces is a single entry,
        // so there is no longer a case where one name would have to describe several of them — but
        // what it is for changes. A single file carries its own model name to fall back on; a
        // compiled set does not, and would otherwise be filed under whichever module came last.
        NameRequirement.Text = compiling ? "   REQUIRED" : "   OPTIONAL";
        NameBox.Tag = compiling
            ? "Name the compiled FOM"
            : "Leave blank to use the FOM's own name";

        // --- Footer
        RegisterButton.IsEnabled = IsValid();
        ShowText(BlockedHint, RegisterButton.IsEnabled ? null : BlockedReason(isHla13));
    }

    /// <summary>True when the current selection describes at least one registration that can go ahead.</summary>
    private bool IsValid() => Hla13Choice.IsChecked == true
        ? FedPathBox.Text.Length != 0 && _omtError is null
        : _evolvedPaths.Count != 0 && (!IsCompiling || NameBox.Text.Trim().Length != 0);

    /// <summary>The line shown beside a disabled Register button, saying what is still missing.</summary>
    private string BlockedReason(bool isHla13)
    {
        if (!isHla13)
        {
            return _evolvedPaths.Count == 0
                ? "Choose at least one FOM file."
                : "Name the compiled FOM to continue.";
        }

        return _omtError is not null ? "Fix the OMT file to continue." : "Choose the FED file to continue.";
    }

    /// <summary>
    /// Decides whether the chosen OMT can be used, caching the answer in <see cref="_omtError"/>.
    /// </summary>
    /// <remarks>
    /// The reader reports both HLA 1.3 dialects as <see cref="FomStandard.Hla13"/>, so the check is
    /// deliberately coarse: it rejects anything that is not a 1.3 document at all — an Evolved XML,
    /// say — and anything that is plainly the FED again. Whether the file is a well-formed object
    /// model is for the parser to say, not this dialog.
    /// </remarks>
    private void ValidateOmt()
    {
        _omtError = null;

        var omt = OmtPathBox.Text;
        if (omt.Length == 0) return;

        if (SamePath(omt, FedPathBox.Text))
        {
            _omtError = "The OMT and the FED cannot be the same file — the FED carries no datatypes.";
            return;
        }

        if (FedFormat.HasExtension(System.IO.Path.GetExtension(omt)))
        {
            _omtError = "That is a FED file. The OMT is the object model beside it, usually .omt or .omd.";
            return;
        }

        if (FomFileReader.DetectStandard(omt) != FomStandard.Hla13)
            _omtError = "That file does not read as an HLA 1.3 object model. Choose the .omt or .omd that goes with the FED.";
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Builds an <c>OpenFileDialog.Filter</c> from the reader's own format table, so the masks can
    /// never drift from the extensions the parser actually accepts.
    /// </summary>
    private static string BuildFilter(string label, params FomFileFormat[] formats)
    {
        // The three 1516 revisions share .xml/.fdd, so they collapse into one group: separate
        // entries would filter identically and detection is by content anyway.
        var extensions = formats
            .SelectMany(f => f.Extensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mask = string.Join(";", extensions.Select(e => "*" + e));

        return $"{label} ({mask})|{mask}|All files (*.*)|*.*";
    }

    /// <summary>The sibling OMT of <paramref name="fedPath"/>, or <c>null</c> when there is none.</summary>
    private static string? FindCompanionOmt(string fedPath)
    {
        foreach (var extension in OmtFormat.Extensions)
        {
            string candidate;
            try
            {
                candidate = System.IO.Path.ChangeExtension(fedPath, extension);
            }
            catch (ArgumentException)
            {
                return null;
            }

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>Puts a path in a read-only box and keeps its tooltip in step, since long paths are clipped.</summary>
    private static void SetPath(System.Windows.Controls.TextBox box, string path)
    {
        box.Text = path;
        box.ToolTip = path.Length == 0 ? null : path;
    }

    /// <summary>Shows a message line, or hides it entirely when there is nothing to say.</summary>
    private static void ShowText(System.Windows.Controls.TextBlock block, string? message)
    {
        block.Text = message ?? "";
        block.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Where a file dialog should open: next to what is already chosen, or nowhere in particular —
    /// an empty string leaves Windows to fall back on its own last-used location.
    /// </summary>
    private static string StartingDirectory(string? knownPath) =>
        string.IsNullOrEmpty(knownPath) ? "" : DirectoryOf(knownPath);

    /// <summary>The folder holding <paramref name="path"/>, or "" when it has none or does not exist.</summary>
    private static string DirectoryOf(string path)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : "";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    /// <summary>True when two paths name the same file, allowing for casing and relative segments.</summary>
    private static bool SamePath(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
            return false;

        try
        {
            return string.Equals(
                System.IO.Path.GetFullPath(left),
                System.IO.Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- entry point

    /// <summary>
    /// Shows the dialog and returns the registrations the user asked for, or <c>null</c> if they
    /// cancelled. The list holds one entry per file to register: always one for HLA 1.3, and one
    /// per selected file for HLA Evolved.
    /// </summary>
    /// <param name="owner">Window to centre on; <c>null</c> centres on the screen.</param>
    public static IReadOnlyList<FomRegistrationRequest>? Prompt(Window? owner)
    {
        var dialog = new RegisterFomWindow();

        if (owner is not null)
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return ModalScrim.ShowModal(dialog) == true ? dialog.Requests : null;
    }
}
