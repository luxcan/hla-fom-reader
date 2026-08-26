using System;

namespace HLAFomReader.App.Infrastructure;

/// <summary>
/// Base for screen-level view models. Adds the busy/status pair every screen in the shell binds to.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    private bool _isBusy;
    private string? _busyMessage;
    private string? _statusMessage;

    /// <summary>True while a long-running operation owns the screen; drives the busy overlay.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Text shown inside the busy overlay, e.g. "Comparing…".</summary>
    public string? BusyMessage
    {
        get => _busyMessage;
        set => SetProperty(ref _busyMessage, value);
    }

    /// <summary>Last outcome message for the status bar. Survives the end of a busy scope.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Raises the busy flag and message for the lifetime of the returned scope:
    /// <c>using (BeginBusy("Comparing…")) { … }</c>. Nested scopes are counted, so an inner
    /// operation finishing does not clear the overlay an outer one is still using.
    /// </summary>
    /// <param name="message">Text for the busy overlay.</param>
    protected IDisposable BeginBusy(string message) => new BusyScope(this, message);

    private int _busyDepth;

    /// <summary>
    /// Ref-counted busy token. Each scope restores the message that was showing when it started, so
    /// unwinding a nested operation leaves the outer overlay reading correctly. Disposing more than
    /// once is harmless.
    /// </summary>
    private sealed class BusyScope : IDisposable
    {
        private readonly string? _previousMessage;
        private ViewModelBase? _owner;

        internal BusyScope(ViewModelBase owner, string message)
        {
            _owner = owner;
            _previousMessage = owner.BusyMessage;

            owner._busyDepth++;
            owner.BusyMessage = message;
            owner.IsBusy = true;
        }

        public void Dispose()
        {
            var owner = _owner;
            if (owner is null) return;
            _owner = null;

            if (owner._busyDepth > 0) owner._busyDepth--;

            owner.BusyMessage = _previousMessage;
            if (owner._busyDepth == 0) owner.IsBusy = false;
        }
    }
}
