using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Every collected diagnostic observation with exact category counts. Categories borrow bounded result
/// inventories and are serialized once, in full, by <see cref="CoverageDiagnosticWriter"/>.</summary>
internal sealed class CoverageDiagnosticDocument(string command, int exit, CoverageCampaign input, string? tableSha256, string? refusal,
    string? resumedEvidenceSha256)
{
    readonly Dictionary<string, CoverageDiagnosticCategory> categories = new(StringComparer.Ordinal);
    int refusedCandidates;

    internal string Command => command;
    internal string Outcome => exit == 0 ? "accepted" : "refused";
    internal string InputSha256 => input.InputSha256;
    internal CoverageRecessionIdentity Identity => input.Identity;
    internal string? TableSha256 => tableSha256;
    internal string? Refusal => refusal;
    internal string ToolCodeSha256 { get; } = CoverageBuildInputJson.CodeHash();
    internal string Render3DCodeSha256 { get; } = CoverageBuildInputJson.Render3DCodeHash();
    /// <summary>SHA-256 of the exact evidence root bytes that resume admission read, or null when none was read.</summary>
    internal string? ResumedEvidenceSha256 => resumedEvidenceSha256;

    internal void Add<T>(string name, IReadOnlyCollection<T> values) => categories[name] = CoverageDiagnosticCategory.Of(values);
    internal void Add(string name, CoverageDiagnosticCategory values) => categories[name] = values;
    internal void AddCandidates(IReadOnlyCollection<CoverageCandidateDiagnostic> candidates)
    {
        Add("candidates", candidates);
        refusedCandidates = candidates.Count(c => c.Outcome == "refused");
    }
    internal CoverageDiagnosticCategory Category(string name) => categories.GetValueOrDefault(name, CoverageDiagnosticCategory.Empty);

    /// <summary>Blocking findings of a refused run. An accepted run reports zero, whatever it observed.</summary>
    internal long FindingCount => exit == 0 ? 0 : (long)Category("holes").Count + Category("selectedFailures").Count
        + Category("replayFailures").Count + Category("missingDenseWork").Count + Category("analysisRefusals").Count
        + Category("runRefusals").Count + refusedCandidates;

    internal void WriteTo(Stream destination) => CoverageDiagnosticWriter.Write(destination, this);
}
