using System.Windows;
using System.Windows.Controls;
using HLAFomReader.App.ViewModels;

namespace HLAFomReader.App.Views;

/// <summary>Compare screen: diff two registered FOMs and walk the result.</summary>
public partial class CompareView : UserControl
{
    public CompareView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Opens the screen on the "Attribute data" tab.
    /// </summary>
    /// <remarks>
    /// Selection only. The tab's IsSelected is TwoWay-bound to
    /// <see cref="AttributeMapViewModel.IsActive"/>, and that binding pushes false onto the first
    /// TabItem as soon as the DataContext arrives, so the tab strip cannot be relied on to settle on
    /// the attribute map by itself. Raising the flag here picks the tab and nothing else — pressing
    /// Compare is what fills it, along with the other two.
    /// </remarks>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CompareViewModel vm)
            vm.AttributeMap.IsActive = true;
    }
}
