using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HLAFomReader.App.Infrastructure;

/// <summary>
/// Minimal hand-rolled <see cref="INotifyPropertyChanged"/> base. The app deliberately avoids a
/// third-party MVVM package, so every bindable object in the shell derives from this.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Assigns <paramref name="value"/> to <paramref name="field"/> and raises a change
    /// notification when the value actually moved.
    /// </summary>
    /// <returns><c>true</c> when the field changed, so callers can chain dependent updates.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for the calling property.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Raises <see cref="PropertyChanged"/> for several properties at once — used when one setter
    /// invalidates a cluster of computed properties.
    /// </summary>
    protected void OnPropertyChanged(params string[] names)
    {
        if (names is null) return;

        var handler = PropertyChanged;
        if (handler is null) return;

        foreach (var name in names)
            handler(this, new PropertyChangedEventArgs(name));
    }
}
