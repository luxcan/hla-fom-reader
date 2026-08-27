using System;
using System.Windows;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.ViewModels;
using HLAFomReader.Core.Reporting;

namespace HLAFomReader.App.Views;

/// <summary>
/// Asks which classes an Excel export should include the members of, before the save prompt.
/// </summary>
/// <remarks>
/// <para>
/// Dialog plumbing only. Everything the screen knows lives in
/// <see cref="ExportSelectionViewModel"/> — the trees, the ticks, the cascade, the search and the
/// summary line — which is what lets the selection be tested without a message pump, and what keeps
/// this file to the three things a window has to do itself: open, close, and say which way it went.
/// </para>
/// <para>
/// Comes before the save prompt rather than after it, because the two questions are not the same
/// size. Choosing classes is the decision; choosing a file name is the confirmation, and a user who
/// backs out of the picker has changed their mind about exporting rather than about where to put it.
/// Asking for the path first and the content second would make cancelling the picker feel like
/// losing work already done.
/// </para>
/// </remarks>
public sealed partial class ExportSelectionWindow : Window
{
    private ExportSelectionWindow() => InitializeComponent();

    /// <summary>
    /// Shows the picker and returns what the user chose, or <c>null</c> if they cancelled.
    /// </summary>
    /// <param name="owner">Window to centre on; <c>null</c> centres on the screen.</param>
    /// <param name="model">The trees to show, already built from the document being exported.</param>
    /// <returns>
    /// The ticked classes — possibly none, which is a real answer meaning "just the hierarchies" —
    /// or <c>null</c> when the user cancelled, which must call the whole export off.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is null.</exception>
    public static ClassExportSelection? Prompt(Window? owner, ExportSelectionViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var dialog = new ExportSelectionWindow { DataContext = model };

        if (owner is not null && !ReferenceEquals(owner, dialog))
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return ModalScrim.ShowModal(dialog) == true ? model.ToSelection() : null;
    }

    private void Export_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Closing from the caption button leaves <see cref="Window.DialogResult"/> null, which reads as a cancel.</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
