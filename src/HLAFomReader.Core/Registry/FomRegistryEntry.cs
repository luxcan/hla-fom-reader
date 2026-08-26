using System;
using System.Collections.Generic;
using System.Linq;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Registry;

/// <summary>Row-level summary of a FOM held in the SQLite store, as shown on the Registry screen.</summary>
public sealed class FomRegistryEntry
{
    public long Id { get; set; }
    public Guid Key { get; set; } = Guid.NewGuid();

    /// <summary>User-facing label; defaults to the FOM's own modelIdentification name, else the file name.</summary>
    public string DisplayName { get; set; } = "";

    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public FomStandard Standard { get; set; }
    public string? SourceNamespace { get; set; }

    /// <summary>
    /// The second source file, when this entry was built from a pair. An HLA 1.3 FOM needs two: the
    /// <c>.fed</c> the RTI loads, which has the structure but no types, and the <c>.omt</c> document,
    /// which has the types. Null for a single-file entry.
    /// </summary>
    public string? CompanionPath { get; set; }

    public string? CompanionHash { get; set; }

    /// <summary>True when this entry was assembled from a FED and its OMT together.</summary>
    public bool IsPair => !string.IsNullOrWhiteSpace(CompanionPath);

    /// <summary>
    /// The modules a compiled FOM was built from, in compile order — file names, one per line.
    /// Null for an entry registered from a single file, which is most of them.
    /// </summary>
    /// <remarks>
    /// A record, not a dependency. The compiled model was written out as a file and this entry was
    /// registered from that file, so nothing here has to be found or loaded for the entry to be
    /// read: it exists to answer "where did this one come from", months later, when a compiled FOM
    /// and a vendor-supplied one look exactly alike in the list.
    /// <para>
    /// This replaced a table of links to the registered FOMs an entry was composed from. Those links
    /// described a relationship that stopped existing once the compile produced a file of its own,
    /// and they would have refused to unregister a module a self-contained FOM merely happened to
    /// have been built from.
    /// </para>
    /// </remarks>
    public string? ComposedFrom { get; set; }

    /// <summary>True when this entry was compiled from several modules.</summary>
    public bool IsComposed => ComposedModules.Count > 1;

    /// <summary>The modules, split back out in compile order.</summary>
    public IReadOnlyList<string> ComposedModules =>
        string.IsNullOrWhiteSpace(ComposedFrom)
            ? Array.Empty<string>()
            : ComposedFrom.Split(ModuleSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// What separates one module name from the next in <see cref="ComposedFrom"/>.
    /// </summary>
    /// <remarks>
    /// A newline, because it is the one character a file name cannot contain — so the list survives
    /// names holding commas, semicolons and spaces, which vendor FOMs do.
    /// </remarks>
    public const char ModuleSeparator = '\n';

    /// <summary>Short label for the registry row, e.g. "3 modules".</summary>
    public string CompositionBadge => IsComposed ? $"{ComposedModules.Count} modules" : "";

    /// <summary>The modules numbered in compile order, for a tooltip.</summary>
    public string CompositionDetail =>
        IsComposed
            ? "Compiled in this order:\n"
              + string.Join("\n", ComposedModules.Select((name, index) => $"{index + 1}.  {name}"))
            : "";

    public string? FileHash { get; set; }
    public long FileSizeBytes { get; set; }
    public DateTime? FileModifiedUtc { get; set; }
    public DateTime RegisteredUtc { get; set; }
    public DateTime LastParsedUtc { get; set; }

    public string? IdentificationName { get; set; }
    public string? IdentificationType { get; set; }
    public string? Version { get; set; }
    public string? Purpose { get; set; }
    public string? ApplicationDomain { get; set; }
    public string? Description { get; set; }
    public string? ModificationDate { get; set; }
    public string? SecurityClassification { get; set; }

    public int ObjectClassCount { get; set; }
    public int AttributeCount { get; set; }
    public int InteractionClassCount { get; set; }
    public int ParameterCount { get; set; }
    public int DataTypeCount { get; set; }
    public int DimensionCount { get; set; }

    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public bool HasErrors => ErrorCount > 0;

    public string StandardDisplayName => Standard switch
    {
        FomStandard.Hla13 => "HLA 1.3",
        FomStandard.Ieee1516_2000 => "IEEE 1516-2000",
        FomStandard.Ieee1516_2010 => "IEEE 1516-2010 (Evolved)",
        FomStandard.Ieee1516_2025 => "IEEE 1516-2025",
        _ => "Unknown",
    };

    /// <summary>Short badge text for the grid: "1.3", "1516-2000", "Evolved", "2025".</summary>
    public string StandardBadge => Standard switch
    {
        FomStandard.Hla13 => "HLA 1.3",
        FomStandard.Ieee1516_2000 => "1516-2000",
        FomStandard.Ieee1516_2010 => "Evolved",
        FomStandard.Ieee1516_2025 => "1516-2025",
        _ => "Unknown",
    };

    /// <summary>True when the file on disk no longer matches the hash recorded at registration.</summary>
    public bool IsStale { get; set; }

    /// <summary>True when the source file is missing from disk.</summary>
    public bool IsMissing { get; set; }

    public override string ToString() => $"{DisplayName} [{StandardBadge}]";
}
