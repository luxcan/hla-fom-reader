using System;
using System.Collections.Generic;
using System.Windows;
using HLAFomReader.App.ViewModels;
using HLAFomReader.App.Views;
using HLAFomReader.Core.Reporting;
using Microsoft.Win32;

namespace HLAFomReader.App.Infrastructure;

/// <summary>
/// The file and message dialogs a view model is allowed to ask for. Keeping this behind an
/// interface means view models stay unit-testable — a fake can answer "the user picked these
/// files" or "the user said no" without a WPF message pump.
/// </summary>
public interface IDialogService
{
    /// <summary>Prompts for one or more existing files.</summary>
    /// <param name="title">Dialog caption.</param>
    /// <param name="filter">Win32 filter string, e.g. <c>"FOM files|*.xml;*.fed|All files|*.*"</c>.</param>
    /// <param name="multiSelect">Whether more than one file may be picked.</param>
    /// <returns>The selected paths, or <c>null</c> when the user cancelled.</returns>
    string[]? OpenFiles(string title, string filter, bool multiSelect = true);

    /// <summary>
    /// Asks the user what to register, via the guided dialog: which HLA standard, and for 1.3 both
    /// the FED and its OMT. Returns null when cancelled.
    /// </summary>
    IReadOnlyList<FomRegistrationRequest>? RequestRegistrations();

    /// <summary>Prompts for a destination path.</summary>
    /// <param name="title">Dialog caption.</param>
    /// <param name="filter">Win32 filter string.</param>
    /// <param name="defaultFileName">File name to pre-fill.</param>
    /// <param name="defaultExt">Extension appended when the user types a bare name.</param>
    /// <returns>The chosen path, or <c>null</c> when the user cancelled.</returns>
    string? SaveFile(string title, string filter, string defaultFileName, string defaultExt);

    /// <summary>Asks a yes/no question. Returns <c>true</c> only for an explicit Yes.</summary>
    bool Confirm(string title, string message);

    /// <summary>Reports a failure the user needs to see.</summary>
    void ShowError(string title, string message);

    /// <summary>Reports a successful outcome.</summary>
    void ShowInfo(string title, string message);

    /// <summary>
    /// Opens the read-only datatype inspector: what one FOM declares about one datatype, and what
    /// values it can carry.
    /// </summary>
    /// <param name="model">The prepared datatype, already read out of the FOM.</param>
    void ShowDataTypeDetail(DataTypeDetailViewModel model);

    /// <summary>
    /// Asks which classes an Excel export should include the members of.
    /// </summary>
    /// <param name="model">The class trees to offer, already built from the document being exported.</param>
    /// <returns>
    /// The ticked classes, or <c>null</c> when the user cancelled. The two are different answers and
    /// callers must keep them apart: an empty selection means "export, just the hierarchies", while
    /// null means "do not export at all".
    /// </returns>
    ClassExportSelection? RequestExportSelection(ExportSelectionViewModel model);
}

/// <summary>
/// The live <see cref="IDialogService"/>, implemented over the common Win32 dialogs and the app's
/// own <see cref="MessageWindow"/>. This is the only place in the app that touches them.
/// </summary>
public sealed class DialogService : IDialogService
{
    /// <inheritdoc />
    /// <inheritdoc />
    public IReadOnlyList<FomRegistrationRequest>? RequestRegistrations() =>
        RegisterFomWindow.Prompt(Owner);

    public string[]? OpenFiles(string title, string filter, bool multiSelect = true)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = multiSelect,
            CheckFileExists = true,
            CheckPathExists = true,
        };

        if (ShowDialog(dialog) != true) return null;

        var files = dialog.FileNames;
        return files.Length == 0 ? null : files;
    }

    /// <inheritdoc />
    public string? SaveFile(string title, string filter, string defaultFileName, string defaultExt)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            DefaultExt = NormalizeExtension(defaultExt),
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (ShowDialog(dialog) != true) return null;

        return string.IsNullOrWhiteSpace(dialog.FileName) ? null : dialog.FileName;
    }

    /// <inheritdoc />
    public bool Confirm(string title, string message) =>
        Show(title, message, MessageKind.Question, MessageButtons.YesNo) == MessageResult.Yes;

    /// <inheritdoc />
    public void ShowError(string title, string message) =>
        Show(title, message, MessageKind.Error, MessageButtons.Ok);

    /// <inheritdoc />
    public void ShowInfo(string title, string message) =>
        Show(title, message, MessageKind.Information, MessageButtons.Ok);

    /// <inheritdoc />
    public void ShowDataTypeDetail(DataTypeDetailViewModel model) =>
        DataTypeDetailWindow.Open(Owner, model);

    /// <inheritdoc />
    public ClassExportSelection? RequestExportSelection(ExportSelectionViewModel model) =>
        ExportSelectionWindow.Prompt(Owner, model);

    /// <summary>
    /// The window dialogs should be modal to. Null during startup and shutdown, which the dialog
    /// APIs handle by centring on the screen instead.
    /// </summary>
    private static Window? Owner
    {
        get
        {
            try
            {
                return Application.Current?.MainWindow;
            }
            catch (InvalidOperationException)
            {
                // MainWindow throws if touched from a non-UI thread; an unowned dialog is fine.
                return null;
            }
        }
    }

    /// <summary>Shows a common dialog modal to the shell window when there is one.</summary>
    private static bool? ShowDialog(CommonDialog dialog)
    {
        var owner = Owner;

        // Dimmed for the same reason an app-owned dialog is: the shell behind a file picker is not
        // usable, and it should not look as though it is. See ModalScrim.
        using (ModalScrim.Cover(owner))
            return owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }

    /// <summary>
    /// Shows a message over the shell window.
    /// </summary>
    /// <remarks>
    /// Every caller in this app already writes these as "what happened", a blank line, then the
    /// particulars — a path, an exception, the consequences. <see cref="MessageWindow.Split"/> takes
    /// them at their word, which is what lets the dialog set the first line in the heavier weight
    /// rather than running the whole thing together at one size.
    /// </remarks>
    private static MessageResult Show(string title, string message, MessageKind kind, MessageButtons buttons)
    {
        var (headline, body) = MessageWindow.Split(message);

        // MessageWindow dims the owner itself, so there is no ModalScrim.Cover here.
        return MessageWindow.Show(Owner, title, headline, body, kind, buttons);
    }

    /// <summary>Accepts "html" or ".html" and returns the form <see cref="SaveFileDialog"/> expects.</summary>
    private static string NormalizeExtension(string defaultExt)
    {
        if (string.IsNullOrWhiteSpace(defaultExt)) return "";

        var trimmed = defaultExt.Trim();
        return trimmed.StartsWith('.') ? trimmed[1..] : trimmed;
    }
}
