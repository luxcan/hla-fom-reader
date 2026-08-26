using System;
using System.IO;
using HLAFomReader.Core.Registry;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// <c>config.json</c> is what tells the app which database to open, and it is read before any
/// database exists — so it has to be robust on its own and must never stop the app starting.
/// </summary>
public sealed class AppConfigTests : IDisposable
{
    private readonly string _directory;

    public AppConfigTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"hlafomreader-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [Fact]
    public void NothingIsRememberedBeforeAnythingIsSaved()
    {
        Assert.Null(AppConfig.GetLastDatabasePath(_directory));
    }

    [Fact]
    public void ThePathSurvivesARoundTrip()
    {
        var path = Path.Combine(_directory, "registry.db");

        AppConfig.SetLastDatabasePath(path, _directory);

        Assert.Equal(path, AppConfig.GetLastDatabasePath(_directory));
    }

    [Fact]
    public void TheSettingsFileIsConfigJson()
    {
        AppConfig.SetLastDatabasePath(@"C:\data\registry.db", _directory);

        Assert.True(File.Exists(ConfigPath), "expected config.json beside the executable");
        Assert.Contains("lastDatabasePath", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void WritingAgainReplacesThePreviousChoice()
    {
        AppConfig.SetLastDatabasePath(@"C:\one\first.db", _directory);
        AppConfig.SetLastDatabasePath(@"C:\two\second.db", _directory);

        Assert.Equal(@"C:\two\second.db", AppConfig.GetLastDatabasePath(_directory));
    }

    [Fact]
    public void ClearForgetsTheChoice()
    {
        AppConfig.SetLastDatabasePath(@"C:\one\first.db", _directory);
        AppConfig.Clear(_directory);

        Assert.Null(AppConfig.GetLastDatabasePath(_directory));
    }

    [Fact]
    public void ClearingWhenNothingWasSavedIsHarmless()
    {
        AppConfig.Clear(_directory);
        AppConfig.Clear(_directory);

        Assert.Null(AppConfig.GetLastDatabasePath(_directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("{ \"lastDatabasePath\": null }")]
    [InlineData("{ \"lastDatabasePath\": \"   \" }")]
    [InlineData("not json at all {{{")]
    public void AnEmptyBlankOrBrokenFileReadsAsNothingRemembered(string contents)
    {
        // Written directly rather than through SetLastDatabasePath, to simulate a truncated or
        // hand-edited file. A broken settings file degrades to "nothing remembered", never a crash.
        File.WriteAllText(ConfigPath, contents);

        Assert.Null(AppConfig.GetLastDatabasePath(_directory));
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmed()
    {
        File.WriteAllText(ConfigPath, "{ \"lastDatabasePath\": \"  C:\\\\data\\\\registry.db  \" }");

        Assert.Equal(@"C:\data\registry.db", AppConfig.GetLastDatabasePath(_directory));
    }

    [Fact]
    public void RecentDatabasesAreRememberedMostRecentFirstAndDeduplicated()
    {
        AppConfig.SetLastDatabasePath(@"C:\one.db", _directory);
        AppConfig.SetLastDatabasePath(@"C:\two.db", _directory);
        AppConfig.SetLastDatabasePath(@"C:\one.db", _directory);

        Assert.Equal(new[] { @"C:\one.db", @"C:\two.db" }, AppConfig.GetRecentDatabases(_directory));
    }

    [Fact]
    public void TheSidebarStartsExpanded()
    {
        Assert.False(AppConfig.GetSidebarCollapsed(_directory));
    }

    [Fact]
    public void TheSidebarStateSurvivesARoundTrip()
    {
        AppConfig.SetSidebarCollapsed(true, _directory);
        Assert.True(AppConfig.GetSidebarCollapsed(_directory));

        AppConfig.SetSidebarCollapsed(false, _directory);
        Assert.False(AppConfig.GetSidebarCollapsed(_directory));
    }

    [Fact]
    public void CollapsingTheSidebarKeepsTheRememberedDatabase()
    {
        // Both settings share one file, so writing either must not drop the other — losing the
        // database path would send the next launch back to the picker.
        AppConfig.SetLastDatabasePath(@"C:\data\registry.db", _directory);
        AppConfig.SetSidebarCollapsed(true, _directory);

        Assert.Equal(@"C:\data\registry.db", AppConfig.GetLastDatabasePath(_directory));
        Assert.True(AppConfig.GetSidebarCollapsed(_directory));

        AppConfig.SetLastDatabasePath(@"C:\data\other.db", _directory);
        Assert.True(AppConfig.GetSidebarCollapsed(_directory));
    }

    [Fact]
    public void AConfigFileFromAnEarlierBuildReadsAsAnExpandedSidebar()
    {
        // Files written before the sidebar could collapse have no such key at all.
        File.WriteAllText(ConfigPath, "{ \"lastDatabasePath\": \"C:\\\\data\\\\registry.db\" }");

        Assert.False(AppConfig.GetSidebarCollapsed(_directory));
    }

    [Fact]
    public void AnUnwritableLocationDoesNotBringTheAppDown()
    {
        var missing = Path.Combine(_directory, "no", "such", "folder");

        // Best-effort by contract: the app still runs for this session, it just cannot remember.
        AppConfig.SetLastDatabasePath(@"C:\data\registry.db", missing);

        Assert.Null(AppConfig.GetLastDatabasePath(missing));
    }

    /// <summary>
    /// The regression this whole change exists for. A self-extracting single-file build reports an
    /// <c>AppContext.BaseDirectory</c> under %TEMP% that changes on every launch, so settings written
    /// there are lost between runs. <see cref="AppConfig.AppDirectory"/> must follow the executable.
    /// </summary>
    [Fact]
    public void TheAppDirectoryFollowsTheExecutableNotAnExtractionFolder()
    {
        var directory = AppConfig.AppDirectory;

        Assert.False(string.IsNullOrWhiteSpace(directory));
        Assert.True(Directory.Exists(directory), $"not a real directory: {directory}");

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
            Assert.Equal(Path.GetDirectoryName(processPath), directory);
    }

    [Fact]
    public void TheDefaultDatabaseSitsBesideTheExecutable()
    {
        Assert.Equal(
            Path.Combine(AppConfig.AppDirectory, "hlafomreader.db"),
            FomDatabase.GetDefaultDatabasePath());
    }
}
