using System.Windows;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.ViewModels;

namespace HLAFomReader.App.Views;

/// <summary>
/// Read-only inspector for one datatype: what the FOM declares, and what values it can carry.
/// </summary>
/// <remarks>
/// Opened from an encoding cell in the attribute map. That column answers "are these two the same
/// bytes?", which is what the map is for; this answers the question straight after it — "so what can
/// this field actually hold?" — which the canonical form cannot, because everything that would
/// answer it is exactly what the canonical form drops.
/// <para>
/// Dialog plumbing only. Everything shown is prepared by <see cref="DataTypeDetailViewModel"/>.
/// </para>
/// </remarks>
public sealed partial class DataTypeDetailWindow : Window
{
    /// <summary>Prefer <see cref="Open"/>; this exists because WPF requires a public ctor for XAML.</summary>
    public DataTypeDetailWindow() => InitializeComponent();

    /// <summary>Opens the inspector modally over <paramref name="owner"/>.</summary>
    /// <param name="owner">Window to centre on and block. Null centres on the screen instead.</param>
    /// <param name="model">The datatype to show.</param>
    public static void Open(Window? owner, DataTypeDetailViewModel model)
    {
        var window = new DataTypeDetailWindow
        {
            DataContext = model,
            Title = model.WindowTitle,
        };

        if (owner is not null && !ReferenceEquals(owner, window))
            window.Owner = owner;

        ModalScrim.ShowModal(window);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
