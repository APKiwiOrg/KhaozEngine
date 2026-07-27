using System;
using System.Collections.Generic;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>The terminal decision of <see cref="UpdateService.VerifyAndRepairAsync"/>: the one value a caller
/// branches on after an integrity pass over the install.</summary>
public enum UpdateRepairOutcome
{
    /// <summary>Every file the signed manifest describes is present on disk with the right content. Nothing was
    /// downloaded and nothing needs doing. A game can say "all N files verified" off this.</summary>
    Verified,

    /// <summary>Damage was found, the replacement files were staged, and the applier was launched: the process
    /// is exiting into the repaired install. In production this outcome is not observed, because the process
    /// exits. A caller that does see it (e.g. a test with a non-exiting <c>ExitProcess</c> hook) must NOT
    /// carry on.</summary>
    Repairing,

    /// <summary>Damage was found and the replacement files are staged, but the apply was deferred to the caller
    /// (<c>applyRepair: false</c>). The service rests at <see cref="UpdateState.ReadyToApply"/>, so the caller
    /// finishes with <see cref="UpdateService.ApplyUpdate"/> at a safe moment. A relaunch is still needed:
    /// nothing on disk has been repaired yet.</summary>
    RepairStaged,

    /// <summary>The feed could not be reached, so NOTHING was verified. Distinct from
    /// <see cref="Verified"/> on purpose: an unreachable feed must never read as a clean install.</summary>
    FeedUnreachable,

    /// <summary>The pass ran but could not complete: a rejected (unsigned / tampered / unreadable) manifest, a
    /// failed download, or an apply that could not start. <see cref="UpdateRepairResult.Error"/> carries the
    /// detail, and the mismatch lists are still populated when the verify itself got far enough.</summary>
    Failed,
}

/// <summary>The phase a <see cref="UpdateRepairProgress"/> report describes.</summary>
public enum UpdateRepairPhase
{
    /// <summary>Fetching the signed manifest and hashing the installed files against it. The long phase.</summary>
    Verifying,
    /// <summary>Downloading the replacement files for whatever did not match.</summary>
    Downloading,
    /// <summary>Handing off to the updater shim to swap the repaired files in, then relaunching.</summary>
    Applying,
}

/// <summary>
/// A progress tick from <see cref="UpdateService.VerifyAndRepairAsync"/>, for a "Verifying game files" screen.
/// The counters always describe the CURRENT <see cref="Phase"/>: files and bytes hashed out of the install
/// total while <see cref="UpdateRepairPhase.Verifying"/>, then files and bytes fetched out of the repair
/// download total while <see cref="UpdateRepairPhase.Downloading"/>.
/// </summary>
public readonly struct UpdateRepairProgress
{
    /// <summary>What the counters below are measuring.</summary>
    public UpdateRepairPhase Phase { get; }
    /// <summary>Files done in this phase.</summary>
    public int FilesDone { get; }
    /// <summary>Files this phase must get through.</summary>
    public int TotalFiles { get; }
    /// <summary>Bytes done in this phase.</summary>
    public long BytesDone { get; }
    /// <summary>Bytes this phase must get through.</summary>
    public long TotalBytes { get; }

    public UpdateRepairProgress(UpdateRepairPhase phase, int filesDone, int totalFiles, long bytesDone, long totalBytes)
    {
        Phase = phase;
        FilesDone = filesDone;
        TotalFiles = totalFiles;
        BytesDone = bytesDone;
        TotalBytes = totalBytes;
    }
}

/// <summary>
/// What <see cref="UpdateService.VerifyAndRepairAsync"/> found and did. Structured rather than a bare bool so
/// a game can show the player the difference between "all 117 files verified, nothing wrong" and "3 files were
/// damaged and have been repaired", which is the diagnostically useful distinction even when nothing is broken.
/// </summary>
public sealed class UpdateRepairResult
{
    /// <summary>The decision to branch on.</summary>
    public required UpdateRepairOutcome Outcome { get; init; }

    /// <summary>The version of the signed manifest the install was verified against, or null when the pass
    /// never got a manifest (<see cref="UpdateRepairOutcome.FeedUnreachable"/> or an early failure).</summary>
    public string? Version { get; init; }

    /// <summary>How many files the signed manifest describes, i.e. how many were checked. Zero when no manifest
    /// was obtained.</summary>
    public int FilesChecked { get; init; }

    /// <summary>Forward-slash relative paths that exist on disk but whose content does not match the manifest.
    /// Normally the damaged-install case (a truncated or NUL-corrupted file from a bad in-place update). When
    /// the feed has moved past the installed build it also covers files that build legitimately changed.</summary>
    public IReadOnlyList<string> MismatchedFiles { get; init; } = Array.Empty<string>();

    /// <summary>Forward-slash relative paths the manifest describes that are absent from the install.</summary>
    public IReadOnlyList<string> MissingFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Forward-slash relative paths present in the install but absent from the manifest. REPORTED ONLY: the
    /// repair never deletes them. A fresh scan cannot tell a leftover from a superseded release apart from the
    /// player's own screenshot, log, config, or mod, and an extra file cannot break a content handshake.
    /// </summary>
    public IReadOnlyList<string> ExtraneousFiles { get; init; } = Array.Empty<string>();

    /// <summary>Failure detail for <see cref="UpdateRepairOutcome.Failed"/>, otherwise null.</summary>
    public string? Error { get; init; }

    /// <summary>Files the repair has to restore: <see cref="MismatchedFiles"/> plus <see cref="MissingFiles"/>.
    /// Zero on a clean install, which is what <see cref="UpdateRepairOutcome.Verified"/> means, so a game shows
    /// "all <see cref="FilesChecked"/> files verified" against zero and "repaired N files" against N.</summary>
    public int FilesNeedingRepair => MismatchedFiles.Count + MissingFiles.Count;

    /// <summary>True when files are staged but not yet swapped in, so the install is still damaged until the
    /// caller applies and the game relaunches. Only <see cref="UpdateRepairOutcome.RepairStaged"/> sets it:
    /// <see cref="UpdateRepairOutcome.Repairing"/> has already launched the applier.</summary>
    public bool RelaunchRequired => Outcome == UpdateRepairOutcome.RepairStaged;
}
