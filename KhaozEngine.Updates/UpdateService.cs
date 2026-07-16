using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>
/// Drives the delta auto-update lifecycle: check the configured <see cref="IUpdateSource"/> for a
/// newer build, diff manifests, download only changed files into a resumable staging area, then hand
/// off to an external updater shim that swaps files while the game is stopped. Determinism-neutral:
/// it never touches game simulation or RNG. Offline-safe: check failures fall back to <see
/// cref="UpdateState.Idle"/>.
/// </summary>
public sealed partial class UpdateService : IDisposable, IUpdateStatus
{
    private readonly UpdateServiceOptions options;
    private readonly IUpdateSource source;
    private readonly string platform;
    private readonly string currentVersion;
    private readonly string installDir;
    private readonly string appDataDir;
    private readonly string localManifestPath;
    private readonly int maxRetries;
    private readonly IReadOnlyList<string> trustedKeys;
    private readonly long maxFileBytes;
    private readonly long maxTotalDownloadBytes;
    private readonly long maxManifestBytes;
    private byte[]? pendingManifestBytes;
    private readonly ILogger log = Log.For<UpdateService>();

    private volatile UpdateState state = UpdateState.Idle;
    private LatestVersionInfo? pendingLatest;
    private UpdateManifest? pendingRemoteManifest;
    private List<ManifestFileEntry> pendingDownloads = new();
    private IReadOnlyList<string> pendingDeletes = Array.Empty<string>();
    private string? remoteVersion;
    private int filesDownloaded;
    private long bytesDownloaded;
    private long totalDownloadBytes;
    private string? errorMessage;
    // volatile: set on the background check thread (CheckForUpdateAsync) and read on the game-loop thread
    // (the overlay each frame, and AutoAdvanceRequired). Paired with the volatile `state` write/read this
    // gives the reader a consistent view of the offered update's required-ness.
    private volatile bool required;
    // True once the most recent CheckForUpdateAsync reached the feed and got version info (vs. a down/slow/
    // unreachable feed, which leaves it false). Read by EnsureUpToDateAsync to tell "up to date" from "couldn't
    // check" - both otherwise rest at Idle. Only meaningful immediately after a check.
    private bool lastCheckReachedFeed;

    public UpdateState State => state;
    public string? RemoteVersion => remoteVersion;
    public int FilesDownloaded => filesDownloaded;
    public int TotalFilesToDownload => pendingDownloads.Count;
    public long BytesDownloaded => Interlocked.Read(ref bytesDownloaded);
    public long TotalDownloadBytes => totalDownloadBytes;
    public string? ErrorMessage => errorMessage;
    public bool IsRequired => required;

    /// <summary>
    /// True when a prior apply was interrupted before the shim finished (e.g. power loss mid-copy),
    /// so the install may be partial and the player should re-download.
    /// </summary>
    public bool PreviousUpdateInterrupted { get; private set; }

    /// <summary>Raised on every state transition and download-progress tick.</summary>
    public event Action? StateChanged;

    public UpdateService(UpdateServiceOptions options)
    {
        this.options = options;
        source = options.Source;
        platform = options.Platform;
        currentVersion = options.CurrentVersion;
        installDir = options.InstallDir;
        appDataDir = options.AppDataDir;
        maxRetries = Math.Max(1, options.MaxDownloadRetries);
        trustedKeys = options.TrustedPublicKeys;
        if (trustedKeys is null || trustedKeys.Count == 0)
        {
            throw new ArgumentException(
                "UpdateServiceOptions.TrustedPublicKeys must contain at least one RSA public key; " +
                "unsigned updates are not supported.", nameof(options));
        }
        maxFileBytes = options.MaxFileBytes;
        maxTotalDownloadBytes = options.MaxTotalDownloadBytes;
        maxManifestBytes = options.MaxManifestBytes;
        localManifestPath = Path.Combine(appDataDir, "update-manifest.json");
        recheckIntervalSeconds = options.RecheckInterval is { TotalSeconds: > 0 } ri ? ri.TotalSeconds : 0.0;

        DetectInterruptedApply();
        CleanStaleStagingDirs();
        CleanStaleRelocateDirs();
    }

    /// <summary>Checks for a newer build and computes the download plan (with resume).</summary>
    public async Task CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        // Restart the periodic recheck clock: a manual (or gate-driven) check counts as "just checked",
        // so a Tick-driven recheck does not fire moments later. Cheap and harmless in every state.
        recheckAccumulator = 0.0;

        if (state is UpdateState.Downloading or UpdateState.Applying)
        {
            return;
        }

        SetState(UpdateState.Checking);
        lastCheckReachedFeed = false;

        try
        {
            LatestVersionInfo? latest = await source.CheckLatestVersionAsync(platform, cancellationToken);
            if (latest is null)
            {
                SetState(UpdateState.Idle);
                return;
            }
            lastCheckReachedFeed = true;   // reached the feed and got version info (vs. a down/slow feed)

            if (!UpdateVersion.IsNewer(currentVersion, latest.Version))
            {
                log.Info($"Current version {currentVersion} is up to date (latest: {latest.Version})");
                SetState(UpdateState.Idle);
                return;
            }

            log.Info($"Update available: {currentVersion} -> {latest.Version}");

            byte[]? manifestBytes = await source.DownloadBytesAsync(latest.ManifestUrl, maxManifestBytes, cancellationToken);
            byte[]? signature = await source.DownloadBytesAsync(latest.ManifestUrl + ".sig", maxManifestBytes, cancellationToken);
            if (manifestBytes is null || signature is null)
            {
                SetState(UpdateState.Idle);
                return;
            }

            if (!ManifestVerifier.Verify(manifestBytes, signature, trustedKeys))
            {
                log.Warn($"Manifest signature INVALID for {latest.Version}; refusing update.");
                SetState(UpdateState.Idle);
                return;
            }

            UpdateManifest? remoteManifest = UpdateManifest.Deserialize(System.Text.Encoding.UTF8.GetString(manifestBytes));
            if (remoteManifest is null)
            {
                SetState(UpdateState.Idle);
                return;
            }

            // Trust only signed fields for security decisions: re-check the downgrade gate against the
            // signed version (not the unsigned `latest`), and take Required from the signed manifest.
            if (!UpdateVersion.IsNewer(currentVersion, remoteManifest.Version))
            {
                log.Info($"Signed manifest version {remoteManifest.Version} not newer than {currentVersion}; ignoring.");
                SetState(UpdateState.Idle);
                return;
            }

            // Reject a hostile/oversized manifest before doing any work.
            long declaredTotal = 0;
            for (int i = 0; i < remoteManifest.Files.Count; i++)
            {
                long size = remoteManifest.Files[i].Size;
                if (size < 0 || size > maxFileBytes)
                {
                    log.Warn($"Manifest file {remoteManifest.Files[i].Path} size {size} exceeds cap {maxFileBytes}; refusing.");
                    SetState(UpdateState.Idle);
                    return;
                }
                declaredTotal += size;
            }
            if (declaredTotal > maxTotalDownloadBytes)
            {
                log.Warn($"Manifest total {declaredTotal} exceeds cap {maxTotalDownloadBytes}; refusing.");
                SetState(UpdateState.Idle);
                return;
            }

            UpdateManifest localManifest = LoadOrGenerateLocalManifest();
            ManifestDiff diff = UpdateManifest.ComputeDiff(localManifest, remoteManifest);

            // Deliberately NOT pooled: this list is retained in the `pendingDownloads` field below and
            // lives across the whole check -> download -> apply lifecycle, so a pooled buffer would alias.
            // It is also a cold one-shot path (per update check, not per frame) with no alloc pressure.
            var downloads = new List<ManifestFileEntry>(diff.FilesToDownload);

            // Resume support: drop files already staged with a matching SHA256. Use the signed
            // manifest version (authoritative) so this matches the staging dir used at download/apply.
            string stagingDir = GetStagingDir(remoteManifest.Version);
            int alreadyStaged = 0;
            for (int i = downloads.Count - 1; i >= 0; i--)
            {
                ManifestFileEntry file = downloads[i];
                string stagedPath = Path.Combine(stagingDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(stagedPath) && VerifyFileHash(stagedPath, file.Sha256))
                {
                    alreadyStaged++;
                    downloads.RemoveAt(i);
                }
            }

            if (alreadyStaged > 0)
            {
                log.Info($"{alreadyStaged} file(s) already staged, {downloads.Count} remaining");
            }

            pendingLatest = latest;
            pendingRemoteManifest = remoteManifest;
            pendingManifestBytes = manifestBytes;
            pendingDownloads = downloads;
            pendingDeletes = diff.FilesToDelete;
            remoteVersion = remoteManifest.Version;
            required = remoteManifest.Required;
            totalDownloadBytes = 0;
            for (int i = 0; i < downloads.Count; i++)
            {
                totalDownloadBytes += downloads[i].Size;
            }

            if (downloads.Count == 0 && diff.FilesToDelete.Count == 0)
            {
                log.Info("All files already staged. Ready to apply.");
                SetState(UpdateState.ReadyToApply);
                return;
            }

            log.Info($"{downloads.Count} file(s) to download ({totalDownloadBytes / 1024 / 1024} MB), {diff.FilesToDelete.Count} to delete");
            SetState(UpdateState.UpdateAvailable);
        }
        catch (Exception ex)
        {
            // Offline-safe: a failed check never surfaces an error, it just returns to Idle.
            log.Info($"Check failed: {ex.Message}");
            SetState(UpdateState.Idle);
        }
    }

    /// <summary>Downloads staged files and transitions to <see cref="UpdateState.ReadyToApply"/>.</summary>
    public async Task StartDownloadAsync(CancellationToken cancellationToken = default)
    {
        if (state != UpdateState.UpdateAvailable || pendingLatest is null || remoteVersion is null)
        {
            return;
        }

        SetState(UpdateState.Downloading);

        try
        {
            string stagingDir = GetStagingDir(remoteVersion);
            Directory.CreateDirectory(stagingDir);

            if (!HasEnoughFreeSpace(stagingDir, totalDownloadBytes))
            {
                SetError("Not enough free disk space to download the update.");
                return;
            }

            filesDownloaded = 0;
            Interlocked.Exchange(ref bytesDownloaded, 0);

            for (int i = 0; i < pendingDownloads.Count; i++)
            {
                ManifestFileEntry file = pendingDownloads[i];
                string destPath = Path.Combine(stagingDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                string fileUrl = source.ResolveFileUrl(pendingLatest, file.Path);

                bool success = false;
                for (int attempt = 0; attempt < maxRetries && !success; attempt++)
                {
                    long fileBytes = 0;
                    var progress = new Progress<long>(b =>
                    {
                        long delta = b - fileBytes;
                        fileBytes = b;
                        Interlocked.Add(ref bytesDownloaded, delta);
                    });

                    success = await source.DownloadFileAsync(fileUrl, destPath, maxFileBytes, progress, cancellationToken);

                    if (success && !VerifyFileHash(destPath, file.Sha256))
                    {
                        log.Info($"SHA256 mismatch for {file.Path}, retrying...");
                        try { File.Delete(destPath); }
                        catch (Exception ex) { log.Debug($"Could not delete mismatched download {destPath}: {ex.Message}"); }
                        Interlocked.Add(ref bytesDownloaded, -fileBytes);
                        success = false;
                    }
                }

                if (!success)
                {
                    SetError($"Failed to download: {file.Path}");
                    return;
                }

                filesDownloaded = i + 1;
                StateChanged?.Invoke();
            }

            // Persist the exact signed manifest bytes so the installed local manifest matches what was
            // verified (falls back to re-serialization if bytes are unavailable).
            try
            {
                string stagedManifestPath = Path.Combine(stagingDir, "manifest.json");
                if (pendingManifestBytes is not null)
                {
                    File.WriteAllBytes(stagedManifestPath, pendingManifestBytes);
                }
                else if (pendingRemoteManifest is not null)
                {
                    File.WriteAllText(stagedManifestPath, pendingRemoteManifest.Serialize());
                }
            }
            catch (Exception ex)
            {
                log.Info($"Could not write staged manifest: {ex.Message}");
            }

            log.Info($"Download complete. {filesDownloaded} file(s) staged.");
            SetState(UpdateState.ReadyToApply);
        }
        catch (Exception ex)
        {
            log.Info($"Download failed: {ex.Message}");
            SetError($"Download failed: {ex.Message}");
        }
    }

    /// <summary>Writes the apply config, launches the shim, and exits. Returns false if it could not start.</summary>
    public bool ApplyUpdate()
    {
        if (state != UpdateState.ReadyToApply || remoteVersion is null)
        {
            return false;
        }

        SetState(UpdateState.Applying);

        try
        {
            string stagingDir = GetStagingDir(remoteVersion);

            if (string.IsNullOrEmpty(options.UpdaterExecutableName))
            {
                SetError("No updater configured.");
                return false;
            }

            string updaterName = OperatingSystem.IsWindows()
                ? options.UpdaterExecutableName + ".exe"
                : options.UpdaterExecutableName;
            string updaterPath = Path.Combine(installDir, updaterName);

            if (!File.Exists(updaterPath))
            {
                log.Info($"Updater shim not found: {updaterPath}");
                SetError("Updater not found. Please re-download the game.");
                return false;
            }

            // Enumerate ALL files in staging (minus the manifest) rather than pendingDownloads, which
            // has already-staged files removed during the check phase.
            var filesToCopy = new List<string>();
            if (Directory.Exists(stagingDir))
            {
                string normalizedStaging = stagingDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (string fullPath in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
                {
                    string relativePath = fullPath[normalizedStaging.Length..].Replace(Path.DirectorySeparatorChar, '/');
                    if (!string.Equals(relativePath, "manifest.json", StringComparison.OrdinalIgnoreCase))
                    {
                        filesToCopy.Add(relativePath);
                    }
                }
            }

            log.Info($"Applying: {filesToCopy.Count} file(s) to copy, {pendingDeletes.Count} to delete");

            var applyConfig = new ApplyUpdateConfig
            {
                TargetVersion = remoteVersion,
                InstallDir = installDir,
                StagingDir = stagingDir,
                FilesToCopy = filesToCopy,
                FilesToDelete = new List<string>(pendingDeletes),
                GameExePath = Environment.ProcessPath ?? Path.Combine(installDir, options.UpdaterExecutableName.Replace("Updater", "")),
                ParentPid = Environment.ProcessId,
                ManifestDestPath = localManifestPath,
                AppDataDir = appDataDir,
                Ui = BuildUiConfig(options.UpdaterUi)
            };

            string applyConfigPath = Path.Combine(appDataDir, "apply-update.json");
            Directory.CreateDirectory(appDataDir);
            File.WriteAllText(applyConfigPath, JsonSerializer.Serialize(applyConfig, UpdatesJsonContext.Default.ApplyUpdateConfig));

            // Ensure a manifest is present in staging for the shim to install.
            string stagedManifest = Path.Combine(stagingDir, "manifest.json");
            if (!File.Exists(stagedManifest) && Directory.Exists(stagingDir))
            {
                UpdateManifest newManifest = pendingRemoteManifest
                    ?? UpdateManifest.GenerateFromDirectory(stagingDir, remoteVersion, platform);
                File.WriteAllText(stagedManifest, newManifest.Serialize());
            }

            log.Info($"Launching updater: {updaterPath}");
            bool launched = (options.LaunchUpdater ?? DefaultLaunchUpdater)(updaterPath, applyConfigPath);
            if (!launched)
            {
                SetError("Updater failed to start.");
                return false;
            }

            log.Info("Updater started. Exiting game.");
            options.OnBeforeForcedExit?.Invoke();
            Log.Flush();
            (options.ExitProcess ?? DefaultExitProcess)();
            return true;
        }
        catch (Exception ex)
        {
            log.Info($"Failed to launch updater: {ex.Message}");
            SetError($"Failed to start update: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        disposed = true;
        if (options.DisposeSource && source is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static bool DefaultLaunchUpdater(string updaterPath, string applyConfigPath)
    {
        Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = updaterPath,
            Arguments = $"--apply \"{applyConfigPath}\"",
            UseShellExecute = false
        });
        return process is not null;
    }

    /// <summary>Maps the consumer's progress-window options onto the apply-config wire block, or null when unset.</summary>
    private static ApplyUpdateUiConfig? BuildUiConfig(UpdaterUiOptions? ui)
    {
        if (ui is null)
        {
            return null;
        }
        return new ApplyUpdateUiConfig
        {
            WindowTitle = ui.WindowTitle,
            Heading = ui.Heading,
            Accent = ToColor(ui.AccentColor),
            Background = ToColor(ui.BackgroundColor),
            Text = ToColor(ui.TextColor),
            LogoPath = ui.LogoPath,
            InstallingText = ui.InstallingText,
            FinishingText = ui.FinishingText,
            DownloadingText = ui.DownloadingText,
        };
    }

    private static UpdaterUiColor? ToColor((byte R, byte G, byte B)? c)
        => c is null ? null : new UpdaterUiColor { R = c.Value.R, G = c.Value.G, B = c.Value.B };

    private static void DefaultExitProcess() => Environment.Exit(0);

    private void DetectInterruptedApply()
    {
        try
        {
            string marker = Path.Combine(appDataDir, "apply-in-progress.json");
            if (!File.Exists(marker))
            {
                return;
            }

            PreviousUpdateInterrupted = true;
            log.Warn("Found apply-in-progress marker - a previous update did not finish. " +
                "Install may be incomplete; re-download recommended.");
            File.Delete(marker);
        }
        catch
        {
            // Diagnostic only; never block startup on this.
        }
    }

    private UpdateManifest LoadOrGenerateLocalManifest()
    {
        if (File.Exists(localManifestPath))
        {
            try
            {
                UpdateManifest? manifest = UpdateManifest.Deserialize(File.ReadAllText(localManifestPath));
                if (manifest is not null && string.Equals(manifest.Version, currentVersion, StringComparison.Ordinal))
                {
                    return manifest;
                }
            }
            catch (Exception ex)
            {
                log.Info($"Failed to read local manifest: {ex.Message}");
            }
        }

        log.Info("Generating local manifest from install directory...");
        UpdateManifest generated = UpdateManifest.GenerateFromDirectory(installDir, currentVersion, platform);

        try
        {
            Directory.CreateDirectory(appDataDir);
            File.WriteAllText(localManifestPath, generated.Serialize());
        }
        catch (Exception ex)
        {
            log.Info($"Failed to save local manifest: {ex.Message}");
        }

        return generated;
    }

    private string GetStagingDir(string version) => Path.Combine(appDataDir, "update-staging", version);

    /// <summary>Deletes staging dirs for versions other than the current one (boot hygiene).</summary>
    private void CleanStaleStagingDirs()
    {
        try
        {
            string stagingRoot = Path.Combine(appDataDir, "update-staging");
            if (!Directory.Exists(stagingRoot))
            {
                return;
            }

            int cleaned = 0;
            foreach (string dir in Directory.GetDirectories(stagingRoot))
            {
                if (!string.Equals(Path.GetFileName(dir), currentVersion, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        cleaned++;
                    }
                    catch
                    {
                        // Best-effort: locked files, permissions, etc.
                    }
                }
            }

            if (cleaned > 0)
            {
                log.Info($"Cleaned {cleaned} stale staging dir(s)");
            }
        }
        catch
        {
            // Non-fatal.
        }
    }

    /// <summary>
    /// Backstop sweep for the updater's self-relocation scratch dirs. The relocated updater deletes its own
    /// scratch dir via a detached one-shot after it exits; this removes anything left if that did not run
    /// (e.g. the machine died mid-update). By the time the game is running, no relocation is in flight, so
    /// the whole <c>updater-relocate</c> tree is safe to clear best-effort (locked files just stay for the
    /// next sweep).
    /// </summary>
    private void CleanStaleRelocateDirs()
    {
        try
        {
            string relocateRoot = Path.Combine(appDataDir, "updater-relocate");
            if (Directory.Exists(relocateRoot))
            {
                Directory.Delete(relocateRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort: a binary still locked by an exiting relocated updater stays for the next sweep.
        }
    }

    private void SetState(UpdateState newState)
    {
        state = newState;
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // A consumer's StateChanged handler must never break the state machine. The state field is
            // already written above, so a throwing subscriber is logged and swallowed rather than wedging
            // the service (e.g. stuck in Checking with every recheck suppressed) or faulting a Tick-driven
            // fire-and-forget check. This also keeps the recovery transition back to Idle safe when the
            // same handler throws on every invocation.
            log.Warn($"StateChanged subscriber threw: {ex.Message}");
        }
    }

    private void SetError(string message)
    {
        errorMessage = message;
        SetState(UpdateState.Failed);
    }

    private bool HasEnoughFreeSpace(string stagingDir, long needed)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(stagingDir));
            if (string.IsNullOrEmpty(root))
            {
                return true; // cannot determine; do not block
            }
            long available = new DriveInfo(root).AvailableFreeSpace;
            return available >= needed + (needed / 10); // 10% headroom
        }
        catch
        {
            return true; // never block an update on a disk-probe failure
        }
    }

    private static bool VerifyFileHash(string filePath, string expectedSha256)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
