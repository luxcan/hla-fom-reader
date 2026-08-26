using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace HLAFomReader.App.Tests;

/// <summary>
/// Hosts a single WPF <see cref="Application"/> on one STA thread for the whole test run, and runs
/// test bodies on that thread.
/// </summary>
/// <remarks>
/// Both of the reasons this exists are non-negotiable WPF facts rather than preferences.
/// <see cref="Application"/> is a per-process singleton — a second <c>new Application()</c> throws —
/// and it has thread affinity, so its resource dictionaries can only be resolved from the thread that
/// created it. Each test class spinning up its own STA thread and Application therefore works in
/// isolation and fails as soon as two of them run in the same process. One shared host solves both.
/// The dispatcher also gets a <see cref="DispatcherSynchronizationContext"/>, which is what
/// <c>Application.Run()</c> would normally install and what makes <c>ConfigureAwait(true)</c> in the
/// view models resume on the UI thread the way it does in the real app.
/// </remarks>
public sealed class WpfAppFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);

    private Dispatcher? _dispatcher;
    private Exception? _startupFailure;

    public WpfAppFixture()
    {
        _thread = new Thread(Run) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(60)))
            throw new InvalidOperationException("The WPF test host did not start within 60 seconds.");

        if (_startupFailure is not null)
            throw new InvalidOperationException("The WPF test host failed to start.", _startupFailure);
    }

    private void Run()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(_dispatcher));

            var application = Application.Current ?? new Application();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Pack URIs use the assembly name, which the csproj sets to HLAFomReader.
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/HLAFomReader;component/Themes/Precision.Dark.xaml"),
            });
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/HLAFomReader;component/Themes/Controls.xaml"),
            });
        }
        catch (Exception ex)
        {
            _startupFailure = ex;
            _ready.Set();
            return;
        }

        _ready.Set();
        Dispatcher.Run();
    }

    /// <summary>Runs <paramref name="action"/> on the host's UI thread and returns its result.</summary>
    public T Invoke<T>(Func<T> action)
    {
        if (_dispatcher is null) throw new InvalidOperationException("The WPF test host is not running.");

        Exception? failure = null;
        T? result = default;

        _dispatcher.Invoke(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        });

        if (failure is not null)
            throw new InvalidOperationException("The test body threw on the WPF host thread.", failure);

        return result!;
    }

    public void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

    public void Dispose()
    {
        _dispatcher?.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(10));
        _ready.Dispose();
    }
}

/// <summary>
/// Puts every WPF test in one collection so they share the single host and never run concurrently —
/// two tests driving one dispatcher at the same time would interleave unpredictably.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WpfCollection : ICollectionFixture<WpfAppFixture>
{
    public const string Name = "wpf";
}
