using System;
using System.Collections.Generic;
using System.IO;

namespace KhaozEngine.Sfx;

/// <summary>What the bake will do with one entry.</summary>
public enum SfxAction
{
    /// <summary>Call the API + encode (new, changed, forced, or missing output).</summary>
    Generate,
    /// <summary>Leave the existing output untouched (up to date).</summary>
    Skip,
}

/// <summary>One resolved entry in the bake plan: absolute paths, hash, and the decided action.</summary>
public sealed record SfxPlanItem
{
    /// <summary>The source manifest entry.</summary>
    public required SfxEntry Entry { get; init; }
    /// <summary>Absolute output path (resolved against the manifest directory).</summary>
    public required string OutPath { get; init; }
    /// <summary>Absolute sidecar path (<see cref="OutPath"/> + <c>.sfxmeta</c>).</summary>
    public required string SidecarPath { get; init; }
    /// <summary>The idempotency hash for this entry's current inputs.</summary>
    public required string Hash { get; init; }
    /// <summary>Generate or skip.</summary>
    public required SfxAction Action { get; init; }
    /// <summary>Short human-readable reason for the action (e.g. "manifest changed").</summary>
    public required string Reason { get; init; }
    /// <summary>Estimated credits if generated (0 when skipping).</summary>
    public required int EstimatedCredits { get; init; }
}

/// <summary>Minimal filesystem probe so the planner is unit-testable without touching disk.</summary>
public interface ISfxFileProbe
{
    /// <summary>True if a file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);
    /// <summary>Reads the file text, or returns null if missing/unreadable.</summary>
    string? TryReadText(string path);
}

/// <summary>
/// Decides, per entry, whether to generate or skip - the idempotency + cost-control core. Default is
/// skip-existing-unchanged; <paramref name="force"/> regenerates everything.
/// </summary>
public static class SfxPlanner
{
    /// <summary>Builds the bake plan for a manifest.</summary>
    public static IReadOnlyList<SfxPlanItem> Plan(SfxManifest manifest, string manifestDir, bool force, ISfxFileProbe fs)
    {
        var items = new List<SfxPlanItem>(manifest.Sounds.Count);
        foreach (SfxEntry entry in manifest.Sounds)
        {
            string outPath = Path.GetFullPath(Path.Combine(manifestDir, entry.Out));
            string sidecarPath = outPath + SfxDefaults.SidecarSuffix;
            string hash = SfxHasher.Compute(entry, manifest.Model, manifest.SourceFormat);
            (SfxAction action, string reason) = Decide(force, fs, outPath, sidecarPath, hash);

            items.Add(new SfxPlanItem
            {
                Entry = entry,
                OutPath = outPath,
                SidecarPath = sidecarPath,
                Hash = hash,
                Action = action,
                Reason = reason,
                EstimatedCredits = action == SfxAction.Generate ? SfxCreditEstimator.Estimate(entry) : 0,
            });
        }
        return items;
    }

    static (SfxAction, string) Decide(bool force, ISfxFileProbe fs, string outPath, string sidecarPath, string hash)
    {
        if (force) return (SfxAction.Generate, "forced");
        if (!fs.FileExists(outPath)) return (SfxAction.Generate, "output missing");

        SfxSidecar? sidecar = SfxSidecar.TryParse(fs.TryReadText(sidecarPath));
        if (sidecar is null) return (SfxAction.Generate, "no/unreadable sidecar");
        if (sidecar.Hash != hash) return (SfxAction.Generate, "manifest changed");

        return (SfxAction.Skip, "up to date");
    }
}
