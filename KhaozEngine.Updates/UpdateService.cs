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
public sealed class UpdateService : IDisposable
{
    private readonly UpdateServiceOptions options;
    private readonly IUpdateSource source;
    private readonly string platform;
    private readonly string currentVersion;
    private readonly string installDir;
    private readonly string appDataDir;
    private readonly string localManifestPath;
    private readonly int maxRetries;
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
    private bool required;

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
        localManifestPath = Path.Combine(appDataDir, "update-manifest.json");

        DetectInterruptedApply();
        CleanStaleStagingDirs();
    }

    /// <summary>Checks for a newer build and computes the download plan (with resume).</summary>
    public async Task CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (state is UpdateState.Downloading or UpdateState.Applying)
        {
            return;
        }

        SetState(UpdateState.Checking);

        try
        {
            LatestVersionInfo? latest = await source.CheckLatestVersionAsync(platform, cancellationToken);
            if (latest is null)
            {
                SetState(UpdateState.Idle);
                return;
            }

            if (!UpdateVersion.IsNewer(currentVersion, latest.Version))
            {
                log.Info($"Current version {currentVersion} is up to date (latest: {latest.Version})");
                SetState(UpdateState.Idle);
                return;
            }

            log.Info($"Update available: {currentVersion} -> {latest.Version}");

            UpdateManifest? remoteManifest = await source.DownloadManifestAsync(latest.ManifestUrl, cancellationToken);
            if (remoteManifest is null)
            {
                SetState(UpdateState.Idle);
                return;
            }

            UpdateManifest localManifest = LoadOrGenerateLocalManifest();
            ManifestDiff diff = UpdateManifest.ComputeDiff(localManifest, remoteManifest);

            // Deliberately NOT pooled: this list is retained in the `pendingDownloads` field below and
            // lives across the whole check -> download -> apply lifecycle, so a pooled buffer would alias.
            // It is also a cold one-shot path (per update check, not per frame) with no alloc pressure.
            var downloads = new List<ManifestFileEntry>(diff.FilesToDownload);

            // Resume support: drop files already staged with a matching SHA256.
            string stagingDir = GetStagingDir(latest.Version);
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
            pendingDownloads = downloads;
            pendingDeletes = diff.FilesToDelete;
            remoteVersion = latest.Version;
            required = latest.Required;
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

                    success = await source.DownloadFileAsync(fileUrl, destPath, progress, cancellationToken);

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

            // Persist the manifest we already downloaded during the check so the shim can install it.
            if (pendingRemoteManifest is not null)
            {
                try
                {
                    File.WriteAllText(Path.Combine(stagingDir, "manifest.json"), pendingRemoteManifest.Serialize());
                }
                catch (Exception ex)
                {
                    // Non-fatal: the shim can regenerate from staging if needed.
                    log.Info($"Could not write staged manifest: {ex.Message}");
                }
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
                ManifestDestPath = localManifestPath
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

    private void SetState(UpdateState newState)
    {
        state = newState;
        StateChanged?.Invoke();
    }

    private void SetError(string message)
    {
        errorMessage = message;
        SetState(UpdateState.Failed);
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
