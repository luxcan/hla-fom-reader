using HLAFomReader.App.Infrastructure;

namespace HLAFomReader.App.ViewModels;

/// <summary>The screens the shell can host.</summary>
public enum AppScreen
{
    Registry,
    Compare,
    Settings,
}

/// <summary>One row in the sidebar navigation list.</summary>
public sealed class NavigationItem : ObservableObject
{
    private string _count = "";
    private bool _isSelected;

    public NavigationItem(AppScreen screen, string title, string description)
    {
        Screen = screen;
        Title = title;
        Description = description;
    }

    public AppScreen Screen { get; }
    public string Title { get; }

    /// <summary>Tooltip text explaining what the screen is for.</summary>
    public string Description { get; }

    /// <summary>
    /// What the row's tooltip says. Leads with the title because a collapsed sidebar shows the icon
    /// only — the tooltip is then the sole place the screen is named.
    /// </summary>
    public string Tooltip => $"{Title} — {Description}";

    /// <summary>Trailing badge, e.g. the number of registered FOMs.</summary>
    public string Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
