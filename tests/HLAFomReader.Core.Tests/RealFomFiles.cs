using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// Finds the real FOM files a few tests exercise the readers against, when a machine has them.
/// </summary>
/// <remarks>
/// <para>
/// These tests used to name their inputs by absolute path. That put the folder layout of unrelated
/// projects, the names of the object models inside them, and a Windows user name into committed
/// source — the same class of thing <c>.gitignore</c> keeps out of this repository, since a model can
/// be vendor-supplied, customer-specific or export-controlled. The file contents were never at risk;
/// the index to them was.
/// </para>
/// <para>
/// So the location comes from the environment instead. Set <c>HLAFOMREADER_REAL_FOMS</c> to a folder
/// holding real FOMs and these tests run against whatever is in it; leave it unset — which is every
/// machine but the one that has the files — and they report that and pass. Nothing about which files
/// exist, or where, is written down here.
/// </para>
/// </remarks>
internal static class RealFomFiles
{
    /// <summary>The environment variable naming a folder of real FOM files.</summary>
    public const string Variable = "HLAFOMREADER_REAL_FOMS";

    /// <summary>Said in the test log when the folder is not configured, so a skip is never silent.</summary>
    public const string NotConfigured =
        "No real FOM folder on this machine: set " + Variable + " to run this. Skipped.";

    /// <summary>The configured folder, or null when it is unset or does not exist.</summary>
    private static string? Folder
    {
        get
        {
            var folder = Environment.GetEnvironmentVariable(Variable);
            return string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder) ? null : folder;
        }
    }

    /// <summary>
    /// Every file under the configured folder with one of <paramref name="extensions"/>, or none.
    /// </summary>
    /// <remarks>
    /// Searched rather than listed, so adding a FOM to that folder widens the test's coverage without
    /// anything being written down in the repository.
    /// </remarks>
    public static IReadOnlyList<string> WithExtensions(params string[] extensions) =>
        Folder is not { } folder
            ? Array.Empty<string>()
            : Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path => extensions.Any(extension =>
                    path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>
    /// The first file under the configured folder called <paramref name="fileName"/>, or null.
    /// </summary>
    /// <remarks>
    /// For the handful of tests that pin something specific to one model — a known parse defect, a
    /// documented rename — where any FOM will not do. The names these are called with are vendor
    /// product names, which is public; where the file sits is not, and that is what stays out.
    /// </remarks>
    public static string? Named(string fileName) =>
        Folder is not { } folder
            ? null
            : Directory.EnumerateFiles(folder, fileName, SearchOption.AllDirectories).FirstOrDefault();
}
