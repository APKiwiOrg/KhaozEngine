using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>How an apply finished.</summary>
public enum ApplyOutcome
{
    /// <summary>All files copied (possibly with non-fatal manifest/relaunch errors; see exit code).</summary>
    Succeeded,

    /// <summary>A staged source file was missing; aborted before touching the install (old version intact).</summary>
    AbortedStagingIncomplete,

    /// <summary>A manifest path was unsafe (absolute/traversal) or a reparse point; aborted untouched.</summary>
    AbortedUnsafePath,

    /// <summary>
    /// A copy failed mid-apply, or the post-apply code-signature check failed; every overwritten file was
    /// restored and the old version relaunched. The new manifest is never installed on this path, so the old
    /// binaries relaunch against the old manifest (a consistent state). The one exception to the restore: an
    /// intentionally-removed destination symlink (suspect at a managed install path) is not recreated, since
    /// its target is unknown.
    /// </summary>
    RolledBack
}

/// <summary>Result of <see cref="UpdateApplier.Apply"/>.</summary>
public sealed class ApplyResult
{
    public required ApplyOutcome Outcome { get; init; }
    public required int ExitCode { get; init; }
    public IReadOnlyList<string> CopiedFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DeletedFiles { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The cross-platform staged-apply core for an external updater shim: waits for the game to exit,
/// backs up each install file before overwriting it, copies staged files in (with retries for locked
/// files), rolls everything back on any failure (the one exception being an intentionally-removed suspect
/// destination symlink, which is discarded and not restored), deletes removed files, installs the new
/// manifest, and relaunches. All side effects go through <see cref="IUpdaterEnvironment"/> so the logic is
/// headless-testable; a game's shim is just <c>UpdateApplier.Run(args, new SystemUpdaterEnvironment())</c>.
/// </summary>
public static class UpdateApplier
{
    public const int MaxCopyRetries = 10;
    public const int RetryDelayMilliseconds = 500;
    public const int MaxParentWaitSeconds = 15;
    private const string RollbackDirName = ".ke-update-rollback";
    private const string ProgressMarkerName = "apply-in-progress.json";

    /// <summary>
    /// CLI entry point for an updater shim. Expects <c>--apply &lt;apply-update.json&gt;</c>, reads and
    /// parses the config, runs <see cref="Apply"/>, then deletes the config file. Returns a process exit
    /// code (0 on success).
    /// </summary>
    public static int Run(string[] args, IUpdaterEnvironment environment)
    {
        if (args.Length < 2 || args[0] != "--apply")
        {
            environment.Log("Usage: <updater> --apply <apply-update.json>");
            return 1;
        }

        string configPath = args[1];
        ApplyUpdateConfig? config;
        try
        {
            string json = File.ReadAllText(configPath);
            config = JsonSerializer.Deserialize(json, UpdatesJsonContext.Default.ApplyUpdateConfig);
            if (config is null)
            {
                environment.Log("Failed to parse config.");
                return 1;
            }
        }
        catch (Exception ex)
        {
            environment.Log($"Failed to read config: {ex.Message}");
            return 1;
        }

        ApplyResult result = Apply(config, environment);

        try { environment.DeleteFile(configPath); }
        catch (Exception ex) { environment.Log($"Cleanup: could not delete apply config {configPath}: {ex.Message}"); }

        return result.ExitCode;
    }

    /// <summary>Applies a staged update described by <paramref name="config"/> using <paramref name="environment"/>.</summary>
    public static ApplyResult Apply(ApplyUpdateConfig config, IUpdaterEnvironment environment)
    {
        environment.Log($"Applying v{config.TargetVersion}: {config.FilesToCopy.Count} to copy, {config.FilesToDelete.Count} to delete");

        foreach (string relativePath in config.FilesToCopy)
        {
            if (!IsSafeRelativePath(config.InstallDir, relativePath))
            {
                environment.Log($"Unsafe copy path, aborting untouched: {relativePath}");
                environment.Relaunch(config.GameExePath, config.InstallDir);
                return new ApplyResult { Outcome = ApplyOutcome.AbortedUnsafePath, ExitCode = 1 };
            }
        }
        foreach (string relativePath in config.FilesToDelete)
        {
            if (!IsSafeRelativePath(config.InstallDir, relativePath))
            {
                environment.Log($"Unsafe delete path, aborting untouched: {relativePath}");
                environment.Relaunch(config.GameExePath, config.InstallDir);
                return new ApplyResult { Outcome = ApplyOutcome.AbortedUnsafePath, ExitCode = 1 };
            }
        }

        if (config.ParentPid > 0)
        {
            environment.WaitForParentExit(config.ParentPid, MaxParentWaitSeconds * 1000);
        }

        // Marker survives an uncatchable interruption (power loss mid-copy); the next game launch
        // detects it and warns. Derived from the manifest dest dir (the app data directory).
        string? markerPath = MarkerPath(config);
        if (markerPath is not null)
        {
            try { environment.WriteAllText(markerPath, "{}"); }
            catch (Exception ex) { environment.Log($"Could not write progress marker {markerPath}: {ex.Message}"); }
        }

        string rollbackDir = Path.Combine(config.InstallDir, RollbackDirName);
        try { environment.DeleteDirectory(rollbackDir); }
        catch (Exception ex) { environment.Log($"Cleanup: could not clear stale rollback dir {rollbackDir}: {ex.Message}"); }

        // Pre-flight: every staged source must exist before we change anything. A missing source means
        // staging is incomplete; applying it would mix old and new binaries. Abort with the install
        // fully intact (manifest unchanged, so the game retries later) and relaunch the old version.
        foreach (string relativePath in config.FilesToCopy)
        {
            string source = Path.Combine(config.StagingDir, ToNative(relativePath));
            if (!environment.FileExists(source))
            {
                environment.Log($"Staged file missing, aborting before any changes: {relativePath}");
                ClearMarker(environment, markerPath);
                environment.Relaunch(config.GameExePath, config.InstallDir);
                return new ApplyResult { Outcome = ApplyOutcome.AbortedStagingIncomplete, ExitCode = 1 };
            }
            if (environment.IsReparsePoint(source))
            {
                environment.Log($"Staged file is a reparse point, aborting: {relativePath}");
                ClearMarker(environment, markerPath);
                environment.Relaunch(config.GameExePath, config.InstallDir);
                return new ApplyResult { Outcome = ApplyOutcome.AbortedUnsafePath, ExitCode = 1 };
            }
        }

        var backedUp = new List<string>();
        var copied = new List<string>();
        bool copyFailed = false;

        foreach (string relativePath in config.FilesToCopy)
        {
            string source = Path.Combine(config.StagingDir, ToNative(relativePath));
            string dest = Path.Combine(config.InstallDir, ToNative(relativePath));

            if (environment.FileExists(dest) && environment.IsReparsePoint(dest))
            {
                // A removed reparse-point link is intentionally discarded: it is NOT recreated on a later
                // rollback (we do not have its target, and a symlink at a managed install path is suspect).
                environment.Log($"Destination is a reparse point, removing link before copy: {relativePath}");
                bool linkRemoved;
                try { environment.DeleteFile(dest); linkRemoved = true; }
                catch (Exception ex) { environment.Log($"Could not remove link {relativePath}: {ex.Message}"); linkRemoved = false; }

                if (!linkRemoved)
                {
                    // Fail closed: never copy through a symlink (it would write outside the install dir).
                    // Roll back anything already applied so the install stays consistent, then relaunch old.
                    RestoreBackups(environment, config.InstallDir, rollbackDir, backedUp);
                    try { environment.DeleteDirectory(rollbackDir); }
                    catch (Exception ex) { environment.Log($"Cleanup: could not remove rollback dir {rollbackDir} after restore: {ex.Message}"); }
                    ClearMarker(environment, markerPath);
                    environment.Relaunch(config.GameExePath, config.InstallDir);
                    return new ApplyResult { Outcome = ApplyOutcome.AbortedUnsafePath, ExitCode = 1 };
                }
            }

            string? destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
            {
                environment.CreateDirectory(destDir);
            }

            if (environment.FileExists(dest))
            {
                string backup = Path.Combine(rollbackDir, ToNative(relativePath));
                string? backupDir = Path.GetDirectoryName(backup);
                if (!string.IsNullOrEmpty(backupDir))
                {
                    environment.CreateDirectory(backupDir);
                }

                try
                {
                    environment.CopyFile(dest, backup, overwrite: true);
                    backedUp.Add(relativePath);
                }
                catch (Exception ex)
                {
                    environment.Log($"BACKUP FAILED: {relativePath} - {ex.Message}");
                    copyFailed = true;
                    break;
                }
            }

            if (!TryCopyWithRetries(environment, source, dest, relativePath))
            {
                copyFailed = true;
                break;
            }
            copied.Add(relativePath);
        }

        if (copyFailed)
        {
            RestoreBackups(environment, config.InstallDir, rollbackDir, backedUp);
            try { environment.DeleteDirectory(rollbackDir); }
            catch (Exception ex) { environment.Log($"Cleanup: could not remove rollback dir {rollbackDir} after restore: {ex.Message}"); }
            ClearMarker(environment, markerPath);
            environment.Log("Update aborted and rolled back. Existing version left intact.");
            environment.Relaunch(config.GameExePath, config.InstallDir);
            return new ApplyResult { Outcome = ApplyOutcome.RolledBack, ExitCode = 1 };
        }

        var deleted = new List<string>();
        foreach (string relativePath in config.FilesToDelete)
        {
            string dest = Path.Combine(config.InstallDir, ToNative(relativePath));
            try
            {
                if (environment.FileExists(dest))
                {
                    environment.DeleteFile(dest);
                    deleted.Add(relativePath);
                }
            }
            catch (Exception ex)
            {
                environment.Log($"DEL FAILED: {relativePath} - {ex.Message}");
            }
        }

        int errors = 0;

        // Clear quarantine first so the signature check sees the file as the OS will at launch.
        environment.ClearQuarantine(config.InstallDir);

        // Fail closed: if the installed executable is not validly signed, roll back to the backups
        // (still present - we have not cleaned the rollback dir yet) and relaunch the old version.
        if (!environment.VerifyCodeSignature(config.GameExePath))
        {
            environment.Log("Code signature verification FAILED after apply; rolling back.");
            RestoreBackups(environment, config.InstallDir, rollbackDir, backedUp);
            try { environment.DeleteDirectory(rollbackDir); }
            catch (Exception ex) { environment.Log($"Cleanup: could not remove rollback dir {rollbackDir} after restore: {ex.Message}"); }
            ClearMarker(environment, markerPath);
            environment.Relaunch(config.GameExePath, config.InstallDir);
            return new ApplyResult { Outcome = ApplyOutcome.RolledBack, ExitCode = 1 };
        }

        // The manifest is the "commit record" of the update: install it only now, once the new binaries
        // are verified. On a codesign-fail rollback above we never reach here, so the old binaries are
        // relaunched against the old manifest (consistent state). The staged source must still exist,
        // so this runs before the staging-dir cleanup below.
        if (!string.IsNullOrEmpty(config.ManifestDestPath))
        {
            string stagedManifest = Path.Combine(config.StagingDir, "manifest.json");
            if (environment.FileExists(stagedManifest))
            {
                try
                {
                    string? manifestDir = Path.GetDirectoryName(config.ManifestDestPath);
                    if (!string.IsNullOrEmpty(manifestDir))
                    {
                        environment.CreateDirectory(manifestDir);
                    }
                    environment.CopyFile(stagedManifest, config.ManifestDestPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    environment.Log($"Manifest copy failed: {ex.Message}");
                    errors++;
                }
            }
        }

        try { environment.DeleteDirectory(config.StagingDir); }
        catch (Exception ex) { environment.Log($"Cleanup: could not remove staging dir {config.StagingDir}: {ex.Message}"); }
        try { environment.DeleteDirectory(rollbackDir); }
        catch (Exception ex) { environment.Log($"Cleanup: could not remove rollback dir {rollbackDir}: {ex.Message}"); }
        ClearMarker(environment, markerPath);

        environment.Relaunch(config.GameExePath, config.InstallDir);

        environment.Log(errors > 0 ? $"Update completed with {errors} error(s)." : "Update applied successfully!");
        return new ApplyResult
        {
            Outcome = ApplyOutcome.Succeeded,
            ExitCode = errors > 0 ? 1 : 0,
            CopiedFiles = copied,
            DeletedFiles = deleted
        };
    }

    private static bool TryCopyWithRetries(IUpdaterEnvironment environment, string source, string dest, string relativePath)
    {
        for (int attempt = 0; attempt < MaxCopyRetries; attempt++)
        {
            try
            {
                environment.CopyFile(source, dest, overwrite: true);
                environment.Log($"OK: {relativePath}");
                return true;
            }
            catch (IOException ex)
            {
                if (attempt < MaxCopyRetries - 1)
                {
                    environment.Log($"RETRY ({attempt + 1}/{MaxCopyRetries}): {relativePath} - {ex.Message}");
                    environment.Sleep(RetryDelayMilliseconds);
                }
                else
                {
                    environment.Log($"FAILED: {relativePath} - {ex.Message}");
                }
            }
        }
        return false;
    }

    private static void RestoreBackups(IUpdaterEnvironment environment, string installDir, string rollbackDir, List<string> backedUp)
    {
        environment.Log("Rolling back partial update...");
        foreach (string relativePath in backedUp)
        {
            string backup = Path.Combine(rollbackDir, ToNative(relativePath));
            string dest = Path.Combine(installDir, ToNative(relativePath));
            try
            {
                environment.CopyFile(backup, dest, overwrite: true);
                environment.Log($"RESTORED: {relativePath}");
            }
            catch (Exception ex)
            {
                environment.Log($"RESTORE FAILED: {relativePath} - {ex.Message}");
            }
        }
    }

    private static string? MarkerPath(ApplyUpdateConfig config)
    {
        if (string.IsNullOrEmpty(config.ManifestDestPath))
        {
            return null;
        }
        string? dir = Path.GetDirectoryName(config.ManifestDestPath);
        return string.IsNullOrEmpty(dir) ? ProgressMarkerName : Path.Combine(dir, ProgressMarkerName);
    }

    private static void ClearMarker(IUpdaterEnvironment environment, string? markerPath)
    {
        if (markerPath is not null)
        {
            try { environment.DeleteFile(markerPath); }
            catch (Exception ex) { environment.Log($"Cleanup: could not clear progress marker {markerPath}: {ex.Message}"); }
        }
    }

    private static string ToNative(string relativePath) => relativePath.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// True when <paramref name="relativePath"/> is a plain forward-slash relative path that stays
    /// under <paramref name="installDir"/>: not rooted, no drive letter, no <c>..</c> segment, no null
    /// byte, and resolving it against the install dir does not escape it.
    /// </summary>
    private static bool IsSafeRelativePath(string installDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\0'))
        {
            return false;
        }
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
        {
            return false;
        }
        string[] segments = relativePath.Split('/', '\\');
        foreach (string segment in segments)
        {
            if (segment == "..")
            {
                return false;
            }
        }

        string fullInstall = Path.GetFullPath(installDir);
        string combined = Path.GetFullPath(Path.Combine(fullInstall, ToNative(relativePath)));
        string prefix = fullInstall.EndsWith(Path.DirectorySeparatorChar)
            ? fullInstall
            : fullInstall + Path.DirectorySeparatorChar;
        return combined.StartsWith(prefix, StringComparison.Ordinal);
    }
}
