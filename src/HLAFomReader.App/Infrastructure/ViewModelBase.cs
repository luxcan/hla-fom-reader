using System;
using System.Windows.Threading;

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

    /// <summary>How long work may run before it is worth telling the user about.</summary>
    /// <remarks>
    /// Below roughly a fifth of a second an overlay reads as a flicker rather than as feedback: the
    /// eye registers that something flashed without registering what it said, which is worse than
    /// showing nothing at all.
    /// </remarks>
    protected static readonly TimeSpan DefaultBusyDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>How long the overlay stays up once it has appeared.</summary>
    /// <remarks>
    /// Without a floor, work finishing just after the delay elapses paints the overlay for a single
    /// frame — the exact flash the delay exists to prevent, merely moved.
    /// </remarks>
    protected static readonly TimeSpan MinimumBusyDwell = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Raises the busy overlay only if the work outlives <paramref name="delay"/>, and then keeps it
    /// up for at least <see cref="MinimumBusyDwell"/>.
    /// </summary>
    /// <param name="message">Text for the busy overlay, if it is ever shown.</param>
    /// <param name="delay">How long to wait before showing anything. Defaults to
    /// <see cref="DefaultBusyDelay"/>.</param>
    /// <remarks>
    /// <para>
    /// <see cref="BeginBusy"/> is unconditional, which is right for work that is always slow — two
    /// documents read out of SQLite, say. It is wrong for work that is usually instant and
    /// occasionally not: comparing two already-loaded classes takes well under a millisecond, and
    /// raising a scrim around it makes every keystroke in a class picker flash the screen grey.
    /// </para>
    /// <para>
    /// Disposing before the delay elapses shows nothing whatsoever — no property change is raised,
    /// so WPF never repaints. The overlay is therefore free to be armed on every pick.
    /// </para>
    /// </remarks>
    protected IDisposable BeginBusyAfter(string message, TimeSpan? delay = null) =>
        new DeferredBusyScope(this, message, delay ?? DefaultBusyDelay, MinimumBusyDwell);

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

    /// <summary>
    /// A busy scope that only ever opens if the work it wraps runs long enough to be worth showing,
    /// and that holds the overlay briefly once it has.
    /// </summary>
    /// <remarks>
    /// Driven by <see cref="DispatcherTimer"/> rather than a timer thread, so the open and the close
    /// both happen on the dispatcher — the only thread <see cref="IsBusy"/> may be written from.
    /// A dispatcher that is not running simply never fires them, which is the correct outcome: with
    /// nothing pumping, there was never going to be a frame to paint the overlay into.
    /// </remarks>
    private sealed class DeferredBusyScope : IDisposable
    {
        private readonly ViewModelBase _owner;
        private readonly string _message;
        private readonly TimeSpan _dwell;
        private readonly DispatcherTimer _timer;

        private BusyScope? _inner;
        private long _openedAt;
        private bool _disposed;

        internal DeferredBusyScope(ViewModelBase owner, string message, TimeSpan delay, TimeSpan dwell)
        {
            _owner = owner;
            _message = message;
            _dwell = dwell;

            _timer = new DispatcherTimer { Interval = delay };
            _timer.Tick += OnElapsed;
            _timer.Start();
        }

        private void OnElapsed(object? sender, EventArgs e)
        {
            _timer.Stop();
            _timer.Tick -= OnElapsed;

            // Disposal can win the race with a tick already queued on the dispatcher.
            if (_disposed) return;

            _inner = new BusyScope(_owner, _message);
            _openedAt = Environment.TickCount64;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timer.Stop();
            _timer.Tick -= OnElapsed;

            if (_inner is null) return;

            var shown = TimeSpan.FromMilliseconds(Environment.TickCount64 - _openedAt);
            if (shown >= _dwell)
            {
                _inner.Dispose();
                return;
            }

            // Held for the rest of its dwell. The scope is ref-counted, so an overlay a later
            // operation has already raised is unaffected by this one finally letting go.
            var closing = _inner;
            _inner = null;

            var wait = new DispatcherTimer { Interval = _dwell - shown };
            wait.Tick += Close;
            wait.Start();

            void Close(object? sender, EventArgs e)
            {
                wait.Stop();
                wait.Tick -= Close;
                closing.Dispose();
            }
        }
    }
}
