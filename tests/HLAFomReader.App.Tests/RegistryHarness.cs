using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.ViewModels;
using HLAFomReader.Core.Reporting;
using HLAFomReader.App.Views;
using HLAFomReader.Core.Registry;

namespace HLAFomReader.App.Tests;

/// <summary>
/// Runs a <see cref="RegistryViewModel"/> against a throwaway database, a scripted dialog service
/// and a private copy of the sample FOMs.
/// </summary>
/// <remarks>
/// The copy matters: several of these tests break, lock or delete the file they registered, and
/// doing that to the checked-in samples would poison every other test in the run.
/// </remarks>
internal static class RegistryHarness
{
    internal static string SamplesDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "samples")))
                directory = directory.Parent;
            return Path.Combine(directory!.FullName, "samples");
        }
    }

    internal static void Run(
        WpfAppFixture wpf,
        Action<RegistryViewModel, ScriptedDialogs, string, IFomRepository> body)
    {
        wpf.Invoke(() =>
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-reg-{Guid.NewGuid():N}.db");
            var work = Path.Combine(Path.GetTempPath(), $"hlafomreader-work-{Guid.NewGuid():N}");
            Directory.CreateDirectory(work);

            try
            {
                foreach (var file in Directory.GetFiles(SamplesDirectory))
                    File.Copy(file, Path.Combine(work, Path.GetFileName(file)));

                using var repository = new SqliteFomRepository(databasePath);

                var dialogs = new ScriptedDialogs();
                var vm = new RegistryViewModel(repository, dialogs);
                vm.Load();

                body(vm, dialogs, work, repository);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
                try { Directory.Delete(work, recursive: true); } catch (IOException) { }
            }
        });
    }

    /// <summary>
    /// Runs an async command to completion, pumping the dispatcher so its continuations run.
    /// </summary>
    internal static void Execute(AsyncRelayCommand command)
    {
        var task = command.ExecuteAsync();
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        task.GetAwaiter().GetResult();
        Drain();
    }

    internal static void Drain()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(15);
        }
    }

    /// <summary>Answers the registration dialogs with scripted values and records what was shown.</summary>
    internal sealed class ScriptedDialogs : IDialogService
    {
        public List<FomRegistrationRequest> Next { get; set; } = new();
        public bool ConfirmAnswer { get; set; }
        public List<string> Errors { get; } = new();

        /// <summary>Where the save prompt reports the user chose, or null for a cancel.</summary>
        public string? SavePath { get; set; }

        /// <summary>The file name each save prompt opened with, in order.</summary>
        public List<string> SaveSuggestions { get; } = new();

        /// <summary>The message of every confirmation asked for, in order.</summary>
        public List<string> Confirmations { get; } = new();

        public IReadOnlyList<FomRegistrationRequest>? RequestRegistrations() => Next;

        public string[]? OpenFiles(string title, string filter, bool multiSelect = true) => null;

        public string? SaveFile(string title, string filter, string defaultFileName, string defaultExt)
        {
            SaveSuggestions.Add(defaultFileName);
            return SavePath;
        }

        public bool Confirm(string title, string message)
        {
            Confirmations.Add(message);
            return ConfirmAnswer;
        }

        public void ShowError(string title, string message) => Errors.Add($"{title}: {message}");
        public void ShowInfo(string title, string message) { }
        public void ShowDataTypeDetail(DataTypeDetailViewModel model) { }

        /// <summary>What the export picker answers with. Null is a cancel; None is "just the hierarchies".</summary>
        public ClassExportSelection? ExportSelection { get; set; } = ClassExportSelection.None;

        /// <summary>The FOM name each export picker opened on, in order.</summary>
        public List<string> ExportPrompts { get; } = new();

        public ClassExportSelection? RequestExportSelection(ExportSelectionViewModel model)
        {
            ExportPrompts.Add(model.FomName);
            return ExportSelection;
        }
    }
}
