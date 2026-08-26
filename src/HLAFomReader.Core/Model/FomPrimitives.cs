using System.Collections.Generic;

namespace HLAFomReader.Core.Model;

/// <summary>The HLA standard a FOM/FED document conforms to.</summary>
public enum FomStandard
{
    Unknown = 0,
    /// <summary>HLA 1.3 / DoD 1.3 — parenthesised <c>.fed</c> Federation Execution Data file.</summary>
    Hla13,
    /// <summary>IEEE 1516-2000 OMT DIF (XML).</summary>
    Ieee1516_2000,
    /// <summary>IEEE 1516-2010 "HLA Evolved" OMT DIF (XML, FOM modules).</summary>
    Ieee1516_2010,
    /// <summary>IEEE 1516-2025 OMT DIF (XML).</summary>
    Ieee1516_2025,
}

public enum DiagnosticSeverity { Info = 0, Warning = 1, Error = 2 }

/// <summary>A message raised while reading a FOM/FED file.</summary>
public sealed class ParseDiagnostic
{
    public ParseDiagnostic() { }

    public ParseDiagnostic(DiagnosticSeverity severity, string message, int? line = null, string? path = null)
    {
        Severity = severity;
        Message = message;
        Line = line;
        Path = path;
    }

    public DiagnosticSeverity Severity { get; set; }
    public string Message { get; set; } = "";
    public int? Line { get; set; }
    /// <summary>Location inside the document, e.g. <c>objects/objectClass[Aircraft]</c>.</summary>
    public string? Path { get; set; }

    public override string ToString() =>
        Line.HasValue ? $"{Severity} (line {Line}): {Message}" : $"{Severity}: {Message}";
}

/// <summary>Base for every named element in the normalised object model.</summary>
public abstract class FomNode
{
    /// <summary>Local name exactly as written in the source document.</summary>
    public string Name { get; set; } = "";

    /// <summary>Dotted path identifying this node within its document, e.g. <c>HLAobjectRoot.Aircraft</c>.</summary>
    public string QualifiedName { get; set; } = "";

    /// <summary>OMT &lt;semantics&gt; text, if present.</summary>
    public string? Semantics { get; set; }

    /// <summary>Note references carried by the element (1516-2010 <c>notes</c> attribute).</summary>
    public string? Notes { get; set; }

    public override string ToString() => string.IsNullOrEmpty(QualifiedName) ? Name : QualifiedName;
}

/// <summary>Bookkeeping for the whole document.</summary>
public sealed class ModelIdentification
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Version { get; set; }
    public string? ModificationDate { get; set; }
    public string? SecurityClassification { get; set; }
    public string? ReleaseRestriction { get; set; }
    public string? Purpose { get; set; }
    public string? ApplicationDomain { get; set; }
    public string? Description { get; set; }
    public string? UseLimitation { get; set; }
    public string? Reference { get; set; }
    public string? Other { get; set; }
    public string? Glyph { get; set; }
    public List<string> Keywords { get; } = new();
    public List<string> PointsOfContact { get; } = new();
    public List<string> UseHistory { get; } = new();
}
