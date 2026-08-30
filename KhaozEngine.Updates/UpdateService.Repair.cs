using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace KhaozEngine.Updates;

public sealed partial class UpdateService
{
    /// <summary>
    /// Verifies the installed files against the authoritative signed manifest by HASHING WHAT IS ACTUALLY ON
    /// DISK, and re-downloads plus re-applies anything that does not match. This is the path for a silently
    /// damaged install: a file corrupted after an update (a bad in-place swap, a half-written copy, a NUL-byte
    /// truncation) leaves the reported version correct, so
    /// <see cref="CheckForUpdateAsync"/> short-circuits at the version gate and never looks at a single byte,
    /// and even past that gate its local picture is the CACHED <c>update-manifest.json</c>, which records the
    /// hash the file is supposed to have. Both together are why a damaged install runs forever reporting "up
    /// to date" while the server's content handshake rejects it. This call is the way out, and the reason it
    /// is separate: hashing an install (~117 files including an 88 MB executable for Ruinborne) is far too
    /// expensive to put on the launch path, so a game triggers it deliberately (a "Verify game files" button,
    /// or a targeted recovery after a handshake rejection).
    ///
    /// It is not a second download pipeline: the manifest is fetched and signature-verified exactly as the
    /// normal check does, and the diff, staging, retrying download loop, per-file SHA256 verify, and applier
    /// handoff are all the same code the update path uses.
    /// </summary>
    /// <param name="progress">Optional sink for a "Verifying game files" screen: phase plus file/byte counters
    /// for that phase. Reported synchronously.</param>
    /// <param name="applyRepair">
    /// True (the default) launches the updater shim as soon as the replacement files are staged, which exits
    /// the process and relaunches into the repaired install, exactly like an update apply. False stages the
    /// repair and returns <see cref="UpdateRepairOutcome.RepairStaged"/> with the service resting at
    /// <see cref="UpdateState.ReadyToApply"/>, so a caller that is mid-session can apply at a safe moment with
    /// <see cref="ApplyUpdate"/>.
    /// </param>
    /// <param name="cancellationToken">Cancelling throws <see cref="OperationCanceledException"/> rather than
    /// returning a result, matching <see cref="EnsureUpToDateAsync"/>.</param>
    public async Task<UpdateRepairResult> VerifyAndRepairAsync(
        IProgress<UpdateRepairProgress>? progress = null,
        bool applyRepair = true,
        CancellationToken cancellationToken = default)
    {
        if (state is UpdateState.Checking or UpdateState.Verifying or UpdateState.Downloading or UpdateState.Applying)
        {
            return new UpdateRepairResult
            {
                Outcome = UpdateRepairOutcome.Failed,
                Error = $"Cannot verify while an update is in flight (state {state}).",
            };
        }

        // A verify is activity: restart the periodic recheck clock so a Tick-driven check does not fire the
        // moment this finishes. Same reasoning as CheckForUpdateAsync.
        recheckAccumulator = 0.0;

        void OnStateChanged() => progress!.Report(new UpdateRepairProgress(
            MapRepairPhase(state), filesDownloaded, TotalFilesToDownload, BytesDownloaded, totalDownloadBytes));

        if (progress is not null) StateChanged += OnStateChanged;
        try
        {
            SetState(UpdateState.Verifying);

            LatestVersionInfo? latest = await source.CheckLatestVersionAsync(platform, cancellationToken).ConfigureAwait(false);
            if (latest is null)
            {
                log.Info("Verify: feed unreachable, nothing was checked.");
                SetState(UpdateState.Idle);
                return new UpdateRepairResult { Outcome = UpdateRepairOutcome.FeedUnreachable };
            }

            byte[]? manifestBytes = await source.DownloadBytesAsync(latest.ManifestUrl, maxManifestBytes, cancellationToken).ConfigureAwait(false);
            byte[]? signature = await source.DownloadBytesAsync(latest.ManifestUrl + ".sig", maxManifestBytes, cancellationToken).ConfigureAwait(false);
            if (manifestBytes is null || signature is null)
            {
                log.Info("Verify: manifest or its signature could not be fetched, nothing was checked.");
                SetState(UpdateState.Idle);
                return new UpdateRepairResult { Outcome = UpdateRepairOutcome.FeedUnreachable };
            }

            // Repair is NOT a way around signing. A manifest that fails here is refused exactly as it is on the
            // update path, and the caller is told so rather than being handed a reassuring "verified".
            if (!ManifestVerifier.Verify(manifestBytes, signature, trustedKeys))
            {
                log.Warn("Verify: manifest signature INVALID; refusing to repair from it.");
                SetState(UpdateState.Idle);
                return Failure(null, "The update manifest signature could not be verified.");
            }

            UpdateManifest? remote = UpdateManifest.Deserialize(Encoding.UTF8.GetString(manifestBytes));
            if (remote is null)
            {
                SetState(UpdateState.Idle);
                return Failure(null, "The update manifest could not be read.");
            }

            // The feed's newest signed build is the repair target. Normally that IS the installed version (the
            // whole point of this path is that the version gate says up to date), and when the feed has moved
            // on, repairing forward to it fixes the damage and updates in one pass. A feed BEHIND the install
            // is refused, so a repair can never silently downgrade a player.
            if (UpdateVersion.IsNewer(remote.Version, currentVersion))
            {
                SetState(UpdateState.Idle);
                return Failure(remote.Version,
                    $"The newest published build {remote.Version} is older than the installed {currentVersion}.");
            }

            if (!ManifestWithinSizeCaps(remote))
            {
                SetState(UpdateState.Idle);
                return Failure(remote.Version, "The update manifest exceeds the configured size caps.");
            }

            // THE load-bearing line. The local picture is a fresh hash of every installed file, never
            // LoadOrGenerateLocalManifest: that prefers the cached manifest whenever its recorded version
            // matches, so it describes what SHOULD be on disk and a file corrupted after the update it was
            // written for still reports its recorded, correct hash. Diffing against that is precisely the bug
            // this method exists to fix, so it must never be reintroduced here.
            UpdateManifest onDisk = UpdateManifest.GenerateFromDirectory(installDir, currentVersion, platform,
                progress is null ? null : new HashProgressRelay(progress));
            cancellationToken.ThrowIfCancellationRequested();

            ManifestDiff diff = UpdateManifest.ComputeDiff(onDisk, remote);
            (List<string> mismatched, List<string> missing) = Partition(onDisk, diff);
            int filesChecked = remote.Files.Count;

            // FilesToDelete is every installed file the manifest does not describe. Reported, never deleted:
            // see UpdateRepairResult.ExtraneousFiles.
            IReadOnlyList<string> extraneous = diff.FilesToDelete;

            UpdateRepairResult Result(UpdateRepairOutcome outcome, string? error = null) => new()
            {
                Outcome = outcome,
                Version = remote.Version,
                FilesChecked = filesChecked,
                MismatchedFiles = mismatched,
                MissingFiles = missing,
                ExtraneousFiles = extraneous,
                Error = error,
            };

            if (diff.FilesToDownload.Count == 0)
            {
                log.Info($"Verify: all {filesChecked} file(s) match {remote.Version} ({extraneous.Count} extra file(s) present, left alone).");
                SetState(UpdateState.Idle);
                return Result(UpdateRepairOutcome.Verified);
            }

            log.Warn($"Verify: {mismatched.Count} mismatched and {missing.Count} missing file(s) against {remote.Version}; repairing.");
            StageRepairPlan(latest, remote, manifestBytes, diff);

            await DownloadPendingAsync(cancellationToken).ConfigureAwait(false);
            if (state != UpdateState.ReadyToApply)
            {
                return Result(UpdateRepairOutcome.Failed, errorMessage ?? "The repair download did not complete.");
            }

            if (!applyRepair)
            {
                log.Info("Repair staged; the caller applies it.");
                return Result(UpdateRepairOutcome.RepairStaged);
            }

            return ApplyUpdate()
                ? Result(UpdateRepairOutcome.Repairing)
                : Result(UpdateRepairOutcome.Failed, errorMessage ?? "The repair could not be applied.");
        }
        catch (OperationCanceledException)
        {
            SetState(UpdateState.Idle);
            throw;
        }
        catch (Exception ex)
        {
            log.Warn($"Verify/repair failed: {ex.Message}");
            SetState(UpdateState.Idle);
            return Failure(null, $"Verification failed: {ex.Message}");
        }
        finally
        {
            if (progress is not null) StateChanged -= OnStateChanged;
        }
    }

    private static UpdateRepairResult Failure(string? version, string error)
        => new() { Outcome = UpdateRepairOutcome.Failed, Version = version, Error = error };

    /// <summary>
    /// Splits the diff's download set into files that are on disk with the wrong content and files that are
    /// not there at all, off the fresh scan rather than a second round of filesystem probes (the scan just
    /// listed everything that exists, so it is the authority).
    /// </summary>
    private static (List<string> Mismatched, List<string> Missing) Partition(UpdateManifest onDisk, ManifestDiff diff)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < onDisk.Files.Count; i++)
        {
            present.Add(onDisk.Files[i].Path);
        }

        var mismatched = new List<string>();
        var missing = new List<string>();
        for (int i = 0; i < diff.FilesToDownload.Count; i++)
        {
            string path = diff.FilesToDownload[i].Path;
            (present.Contains(path) ? mismatched : missing).Add(path);
        }

        return (mismatched, missing);
    }

    /// <summary>
    /// Populates the same pending* download plan the check phase builds, so <see cref="DownloadPendingAsync"/>
    /// and <see cref="ApplyUpdate"/> run unchanged. Skips anything already staged intact (the resume rule), and
    /// deliberately leaves the delete list EMPTY: see <see cref="UpdateRepairResult.ExtraneousFiles"/>.
    /// </summary>
    private void StageRepairPlan(LatestVersionInfo latest, UpdateManifest remote, byte[] manifestBytes, ManifestDiff diff)
    {
        var downloads = new List<ManifestFileEntry>(diff.FilesToDownload);
        string stagingDir = GetStagingDir(remote.Version);
        for (int i = downloads.Count - 1; i >= 0; i--)
        {
            string relative = downloads[i].Path;
            if (!UpdatePathSafety.IsSafeRelativePath(stagingDir, relative))
            {
                // Never combine an escaping path, not even to stat it. Left in the plan deliberately, so the
                // download loop's guard turns it into one clean failure instead of a silent skip here.
                continue;
            }
            string staged = Path.Combine(stagingDir, UpdatePathSafety.ToNative(relative));
            if (File.Exists(staged) && VerifyFileHash(staged, downloads[i].Sha256))
            {
                downloads.RemoveAt(i);
            }
        }

        pendingLatest = latest;
        pendingRemoteManifest = remote;
        pendingManifestBytes = manifestBytes;
        pendingDownloads = downloads;
        pendingDeletes = Array.Empty<string>();
        remoteVersion = remote.Version;
        // `required` is deliberately left alone. It describes an OFFERED UPDATE, and a repair is not one, so
        // setting it here would let a game-loop UpdateOverlayActions.AutoAdvanceRequired call apply a repair
        // the caller explicitly deferred with applyRepair: false.

        totalDownloadBytes = 0;
        for (int i = 0; i < downloads.Count; i++)
        {
            totalDownloadBytes += downloads[i].Size;
        }
    }

    private static UpdateRepairPhase MapRepairPhase(UpdateState s) => s switch
    {
        UpdateState.UpdateAvailable or UpdateState.Downloading => UpdateRepairPhase.Downloading,
        UpdateState.ReadyToApply or UpdateState.Applying => UpdateRepairPhase.Applying,
        _ => UpdateRepairPhase.Verifying,
    };

    /// <summary>
    /// Forwards the manifest hashing ticks onto the repair progress sink. A hand-rolled
    /// <see cref="IProgress{T}"/> rather than <see cref="Progress{T}"/> on purpose: the BCL one posts to the
    /// captured synchronization context, which would reorder these against the state-driven reports and
    /// deliver some of them after the call has already returned.
    /// </summary>
    private sealed class HashProgressRelay : IProgress<ManifestHashProgress>
    {
        private readonly IProgress<UpdateRepairProgress> sink;

        public HashProgressRelay(IProgress<UpdateRepairProgress> sink) => this.sink = sink;

        public void Report(ManifestHashProgress value) => sink.Report(new UpdateRepairProgress(
            UpdateRepairPhase.Verifying, value.FilesHashed, value.TotalFiles, value.BytesHashed, value.TotalBytes));
    }
}
