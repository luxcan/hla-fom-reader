using System.IO;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Parsing;

/// <summary>Reads one family of FOM/FED files into the normalised <see cref="FomDocument"/>.</summary>
public interface IFomParser
{
    /// <summary>The standard this parser produces.</summary>
    FomStandard Standard { get; }

    /// <summary>
    /// Reads the document. Content problems are reported through
    /// <see cref="FomDocument.Diagnostics"/> rather than thrown, so a partially
    /// broken file still yields whatever could be understood.
    /// </summary>
    FomDocument Parse(TextReader reader, string? sourcePath = null);
}
