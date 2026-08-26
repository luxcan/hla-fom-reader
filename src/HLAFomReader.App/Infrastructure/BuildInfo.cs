using System;
using System.Linq;
using System.Reflection;

namespace HLAFomReader.App.Infrastructure;

/// <summary>
/// What the About window says about this build. Everything but the repository is read from the
/// assembly, so a build describes itself rather than repeating a version that would have to be kept
/// in step by hand.
/// </summary>
/// <remarks>
/// Read from <em>this</em> assembly rather than from <see cref="Assembly.GetEntryAssembly"/>, which
/// under a test host is the test runner and would describe that instead.
/// </remarks>
public static class BuildInfo
{
    /// <summary>Where the source lives. The one value here that the assembly cannot supply.</summary>
    public const string RepositoryUrl = "https://github.com/luxcan/hla-fom-reader";

    private static readonly Assembly Self = typeof(BuildInfo).Assembly;

    static BuildInfo()
    {
        ProductName = Attribute<AssemblyProductAttribute>()?.Product ?? "HLA FOM Reader";
        Description = Attribute<AssemblyDescriptionAttribute>()?.Description ?? "";

        // The SDK appends "+<commit sha>" to the informational version, so the two are split apart:
        // the number is what a user quotes, the commit is what identifies the build.
        var informational = Attribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plus = informational?.IndexOf('+') ?? -1;

        Version = plus > 0
            ? informational![..plus]
            : informational ?? Self.GetName().Version?.ToString(3) ?? "unknown";

        Commit = plus > 0 && informational!.Length > plus + 1
            ? informational[(plus + 1)..][..Math.Min(7, informational.Length - plus - 1)]
            : null;

        // Stamped by the csproj at compile time. Null on a build that predates the stamp, which the
        // version line then simply omits rather than inventing a date for.
        BuildDate = Self.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "BuildDate", StringComparison.Ordinal))
            ?.Value;
    }

    /// <summary>Display name of the application, e.g. "HLA FOM Reader".</summary>
    public static string ProductName { get; }

    /// <summary>One-line summary of what the app is for.</summary>
    public static string Description { get; }

    /// <summary>Release number without any build metadata, e.g. "1.0.0".</summary>
    public static string Version { get; }

    /// <summary>Short commit hash this was built from, or null when built outside a repository.</summary>
    public static string? Commit { get; }

    /// <summary>
    /// The day this build was compiled, <c>yyyy-MM-dd</c>, or null on a build made before the stamp
    /// existed.
    /// </summary>
    /// <remarks>
    /// A date answers "am I looking at the build I just made?" in a way a commit hash does not —
    /// which matters here, because builds reach this app by hand and a stale executable looks
    /// exactly like a change that did not work.
    /// </remarks>
    public static string? BuildDate { get; }

    /// <summary>Where releases are published. Derived, so the two URLs cannot drift apart.</summary>
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    /// <summary>
    /// The version, carrying the commit it came from when the build recorded one. Builds reach this
    /// application by hand as often as by tag, and "1.0.0" alone does not say which one is installed.
    /// </summary>
    public static string VersionSummary =>
        Commit is null ? Version : $"{Version} ({Commit})";

    private static T? Attribute<T>() where T : Attribute =>
        Self.GetCustomAttribute<T>();
}
