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
    RolledBack,

    /// <summary>
    /// The game was still running when the exit barrier expired, so nothing was touched: the install is
    /// fully intact and no marker was written. The old version keeps running and the update is deferred.
    /// </summary>
    AbortedGameStillRunning
}

/// <summary>Result of <see cref="UpdateApplier.Apply(ApplyUpdateConfig, IUpdaterEnvironment)"/>.</summary>
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
    // Generous lock-wait budget: a just-exited game's DLLs can stay locked for several seconds while
    // Windows tears down the process and antivirus scans the closed files. 40 x 500ms = 20s of retry
    // per contended file (on top of the 30s parent-exit wait) clears that window; an unlocked file
    // still succeeds on the first attempt with no delay, so the common path is unaffected.
    public const int MaxCopyRetries = 40;
    public const int RetryDelayMilliseconds = 500;
    public const int MaxParentWaitSeconds = 30;

    // After the parent-exit wait, confirm the game is truly gone before mutating anything: poll
    // IsProcessAlive up to this budget (60 x 500ms = 30s on top of the 30s wait). A process still alive
    // at the end means we must NOT swap files into it (the "patched while the window was up" crash), so
    // the apply aborts untouched. The common path (process already gone) returns on the first poll.
    public const int ParentGoneBarrierPolls = 60;
    public const int ParentGonePollDelayMilliseconds = 500;

    // Post-apply settle wait (the "Finishing" phase). After the new exe lands, Windows Defender scans it
    // and briefly holds an exclusive lock; relaunching mid-scan trips over the in-flight image
    // (STATUS_DLL_INIT_FAILED / STATUS_STACK_BUFFER_OVERRUN). We poll CanOpenExclusively until the scanner
    // releases the file, then relaunch. 60 x 500ms = 30s ceiling, then relaunch anyway as a last resort.
    // On non-Windows CanOpenExclusively returns true, so the first poll passes and the wait is a no-op.
    // This is the first-line pre-filter; the relaunch retry below is the definitive gate (see Relaunch).
    public const int SettleMaxPolls = 60;
    public const int SettlePollDelayMilliseconds = 500;

    // Relaunch retry (the definitive load-ready gate). The settle poll above proves the new exe is no
    // longer locked, but "openable for a handle" is necessary-but-insufficient: an antivirus minifilter
    // can still block the image from *executing* while a scan finishes, so the launch fails at image load
    // with a startup NTSTATUS (0xC0000142 / 0xC0000409 / ...). So we do not trust a single fire-and-forget
    // launch: TryRelaunch starts the game, watches it for a beat, and reports whether it survived; on a
    // fast startup failure or a launch error we back off and try again, up to the budget below. A failing
    // attempt returns as soon as the child dies, so only the settling case spends real time here. On
    // non-Windows TryRelaunch reports Running on the first try, so this is a single launch with no waiting.
    public const int RelaunchMaxAttempts = 8;
    public const int RelaunchWatchMilliseconds = 2500;
    public const int RelaunchRetryBaseDelayMilliseconds = 500;
    public const int RelaunchRetryMaxDelayMilliseconds = 2000;

    private const string RollbackDirName = ".ke-update-rollback";
    private const string ProgressMarkerName = "apply-in-progress.json";
    private const string RelocateDirName = "updater-relocate";
    private const string RelocatedFlag = "--relocated";

    /// <summary>
    /// CLI entry point for an updater shim. Expects <c>--apply &lt;apply-update.json&gt;</c> (with an
    /// optional <c>--relocated</c> flag), reads and parses the config, optionally relocates the updater
    /// out of the install dir (see <see cref="TryRelocate"/>), runs <see cref="Apply(ApplyUpdateConfig, IUpdaterEnvironment, IUpdaterUi)"/>, then deletes the
    /// config file. Returns a process exit code (0 on success).
    /// </summary>
    public static int Run(string[] args, IUpdaterEnvironment environment)
        => Run(args, environment, uiFactory: null);

    /// <summary>
    /// As <see cref="Run(string[], IUpdaterEnvironment)"/>, but with an optional factory for the progress
    /// window shown during the apply. The window is created only on the apply path (Stage 2 / in-place),
    /// never during the brief Stage 1 relocation hop, and is always closed before this returns. The shim
    /// passes a real per-OS factory; tests pass null (no window).
    /// </summary>
    public static int Run(string[] args, IUpdaterEnvironment environment, Func<IUpdaterUi>? uiFactory)
    {
        (string? configPath, bool relocated) = ParseArgs(args);
        if (configPath is null)
        {
            environment.Log("Usage: <updater> --apply <apply-update.json> [--relocated]");
            return 1;
        }

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

        // Stage 1: when the updater is running from inside the install dir (Windows, where a process
        // locks its own loaded .exe/.dll), copy ourselves to a scratch dir and re-launch from there so
        // the install-dir updater binaries are free to be overwritten. Hand off and exit.
        if (!relocated && TryRelocate(config, configPath, environment))
        {
            return 0;
        }

        // The progress window lives only for the apply itself: created here (never during the Stage 1
        // relocation hop above, which returns before this) and closed in the finally, so it can never
        // outlive the updater process. Apply also closes it right before it relaunches; the finally is
        // the backstop for an unexpected throw.
        IUpdaterUi ui = uiFactory?.Invoke() ?? NullUpdaterUi.Instance;
        try
        {
            ApplyResult result = Apply(config, environment, ui);

            try { environment.DeleteFile(configPath); }
            catch (Exception ex) { environment.Log($"Cleanup: could not delete apply config {configPath}: {ex.Message}"); }

            // Stage 2 ran from the scratch dir; schedule its removal now that the apply is done. The detached
            // one-shot fires after this process exits and unlocks the relocated binaries, so nothing is left
            // behind. (A boot-time sweep in UpdateService is the backstop if this machine dies first.) Guard:
            // never schedule deletion of anything inside the install dir, in case --relocated is ever misused
            // while running in place.
            if (relocated)
            {
                string selfBase = environment.GetSelfBaseDirectory();
                if (!string.IsNullOrEmpty(selfBase) && !IsDirInside(selfBase, config.InstallDir))
                {
                    try { environment.ScheduleDirectoryDeletion(selfBase); }
                    catch (Exception ex) { environment.Log($"Could not schedule relocate-dir cleanup: {ex.Message}"); }
                }
            }

            return result.ExitCode;
        }
        finally
        {
            ui.Close();
        }
    }

    private static (string? ConfigPath, bool Relocated) ParseArgs(string[] args)
    {
        string? configPath = null;
        bool relocated = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--apply" && i + 1 < args.Length)
            {
                configPath = args[i + 1];
                i++;
            }
            else if (args[i] == RelocatedFlag)
            {
                relocated = true;
            }
        }
        return (configPath, relocated);
    }

    /// <summary>
    /// Relocates the running updater out of the install dir so it can overwrite its own binaries. Copies
    /// just the updater's dependency closure (resolved from its <c>.deps.json</c>) into
    /// <c>&lt;AppDataDir&gt;/updater-relocate/&lt;version&gt;</c>, launches that copy with
    /// <c>--relocated</c>, and returns true (the caller exits). Returns false when relocation is not
    /// needed (the environment reports no self-exe, i.e. non-Windows; or the updater already runs outside
    /// the install dir) or could not be staged, in which case the caller applies in place.
    /// </summary>
    private static bool TryRelocate(ApplyUpdateConfig config, string configPath, IUpdaterEnvironment environment)
    {
        string? selfExe = environment.GetSelfExecutablePath();
        if (string.IsNullOrEmpty(selfExe))
        {
            return false; // no relocation needed (POSIX) or self-path undeterminable
        }

        string selfDir = environment.GetSelfBaseDirectory();
        if (string.IsNullOrEmpty(selfDir) || !IsDirInside(selfDir, config.InstallDir))
        {
            return false; // already running outside the install dir; safe to apply in place
        }

        string appDataDir = !string.IsNullOrEmpty(config.AppDataDir)
            ? config.AppDataDir
            : Path.GetDirectoryName(config.ManifestDestPath) ?? string.Empty;
        if (string.IsNullOrEmpty(appDataDir))
        {
            environment.Log("Cannot resolve a scratch dir for relocation; applying in place.");
            return false;
        }

        string apphostName = Path.GetFileName(selfExe);
        string depsPath = Path.Combine(selfDir, Path.GetFileNameWithoutExtension(apphostName) + ".deps.json");
        string depsJson = string.Empty;
        if (environment.FileExists(depsPath))
        {
            try { depsJson = environment.ReadAllText(depsPath); }
            catch (Exception ex) { environment.Log($"Could not read updater deps {depsPath}: {ex.Message}"); }
        }

        IReadOnlyList<string> closure = ResolveUpdaterClosure(depsJson, apphostName);

        string version = string.IsNullOrEmpty(config.TargetVersion) ? "pending" : config.TargetVersion;
        string relocateDir = Path.Combine(appDataDir, RelocateDirName, version);

        try
        {
            environment.DeleteDirectory(relocateDir);
            environment.CreateDirectory(relocateDir);

            foreach (string relative in closure)
            {
                string src = Path.Combine(selfDir, ToNative(relative));
                if (!environment.FileExists(src))
                {
                    continue; // closure entry not present on disk (e.g. framework asset); skip
                }
                string dst = Path.Combine(relocateDir, ToNative(relative));
                string? dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir))
                {
                    environment.CreateDirectory(dstDir);
                }
                environment.CopyFile(src, dst, overwrite: true);
            }

            string relocatedExe = Path.Combine(relocateDir, apphostName);
            if (!environment.FileExists(relocatedExe))
            {
                environment.Log("Relocation incomplete (updater exe not copied); applying in place.");
                return false;
            }

            environment.Log($"Relocated updater to {relocateDir}; launching staged apply.");
            environment.LaunchRelocatedUpdater(relocatedExe, configPath, relocateDir);
            return true;
        }
        catch (Exception ex)
        {
            environment.Log($"Relocation failed ({ex.Message}); applying in place.");
            return false;
        }
    }

    /// <summary>
    /// The set of files a framework-dependent updater needs to run, as bare filenames in its base
    /// directory: the host quartet (<c>&lt;app&gt;.exe</c>/<c>&lt;app&gt;.dll</c>/<c>.runtimeconfig.json</c>/
    /// <c>.deps.json</c>) plus every runtime/native/resource asset listed in the <c>.deps.json</c> targets
    /// (its managed dependency DLLs). The deps target keys are package-relative paths
    /// (<c>lib/&lt;tfm&gt;/Name.dll</c>); a published app flattens those to the app root, so each is reduced
    /// to its filename. The shared .NET framework is resolved from the machine, not copied. Pure, so it is
    /// unit-testable against a sample deps document. (Localized satellite assemblies in culture subdirs are
    /// not handled; the updater is not localized.)
    /// </summary>
    public static IReadOnlyList<string> ResolveUpdaterClosure(string depsJsonText, string apphostFileName)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string file)
        {
            string name = Path.GetFileName(file.Replace('\\', '/'));
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
            {
                result.Add(name);
            }
        }

        string baseName = Path.GetFileNameWithoutExtension(apphostFileName);
        Add(apphostFileName);
        Add(baseName + ".dll");
        Add(baseName + ".runtimeconfig.json");
        Add(baseName + ".deps.json");

        if (string.IsNullOrEmpty(depsJsonText))
        {
            return result;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(depsJsonText);
            if (doc.RootElement.TryGetProperty("targets", out JsonElement targets)
                && targets.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty target in targets.EnumerateObject())
                {
                    if (target.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (JsonProperty library in target.Value.EnumerateObject())
                    {
                        if (library.Value.ValueKind != JsonValueKind.Object) continue;

                        // "runtime"/"native"/"resources" and "runtimeTargets" all map asset-path keys
                        // (package-relative) -> metadata. The published file is the flattened filename.
                        foreach (string section in AssetSections)
                        {
                            if (library.Value.TryGetProperty(section, out JsonElement files)
                                && files.ValueKind == JsonValueKind.Object)
                            {
                                foreach (JsonProperty file in files.EnumerateObject())
                                {
                                    Add(file.Name);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Malformed deps.json: the host quartet is the best-effort closure.
        }

        return result;
    }

    private static readonly string[] AssetSections = { "runtime", "native", "resources", "runtimeTargets" };

    /// <summary>Applies a staged update described by <paramref name="config"/> using <paramref name="environment"/>.</summary>
    public static ApplyResult Apply(ApplyUpdateConfig config, IUpdaterEnvironment environment)
        => Apply(config, environment, NullUpdaterUi.Instance);

    /// <summary>
    /// As <see cref="Apply(ApplyUpdateConfig, IUpdaterEnvironment)"/>, but reporting progress to
    /// <paramref name="ui"/>: it shows the window, reports the Install phase with (files copied / total)
    /// as it copies, reports the Finishing phase during the settle wait, and closes the window right
    /// before it relaunches. All UI calls are best-effort (a broken window is a no-op) and never affect
    /// the apply outcome. Every return path relaunches through <see cref="Relaunch"/>, which closes the
    /// window first, so the window is always torn down before the game restarts.
    /// </summary>
    public static ApplyResult Apply(ApplyUpdateConfig config, IUpdaterEnvironment environment, IUpdaterUi ui)
    {
        UpdaterUiTheme theme = UpdaterUiTheme.FromConfig(config.Ui, config.InstallDir);
        ui.Show(theme);
        ui.SetPhase(UpdaterPhase.Install);
        ui.SetStatus(theme.InstallingText);

        environment.Log($"Applying v{config.TargetVersion}: {config.FilesToCopy.Count} to copy, {config.FilesToDelete.Count} to delete");

        foreach (string relativePath in config.FilesToCopy)
        {
            if (!IsSafeRelativePath(config.InstallDir, relativePath))
            {
                environment.Log($"Unsafe copy path, aborting untouched: {relativePath}");
                Relaunch(environment, ui, config);
                return new ApplyResult { Outcome = ApplyOutcome.AbortedUnsafePath, ExitCode = 1 };
            }
        }
        foreach (string relativePath in config.FilesToDelete)
        {
            if (!IsSafeRelativePath(config.InstallDir, relativePath))
            {
                environment.Log($"Unsafe delete path, aborting untouched: {relativePath}");
                Relaunch(environment, ui, config);
                return new ApplyResult { Outcome = ApplyOutcome.AbortedUnsafePath, ExitCode = 1 };
            }
        }

        if (config.ParentPid > 0)
        {
            environment.WaitForParentExit(config.ParentPid, MaxParentWaitSeconds * 1000);

            // Barrier: never mutate a single install file while the game is still alive. WaitForParentExit
            // blocks efficiently for the bulk of the shutdown. This poll confirms the process is actually
            // gone and rides out a late-dying one. Still alive at the end means abort UNTOUCHED - no marker,
            // no file changes, and no relaunch (the game is already running). Swapping a running process's
            // locked .exe/.dll is exactly the order-of-operations failure this fixes.
            if (!WaitForParentGone(environment, config.ParentPid))
            {
                environment.Log("Game still running after the exit barrier, deferring update (install untouched).");
                ui.Close();
                return new ApplyResult { Outcome = ApplyOutcome.AbortedGameStillRunning, ExitCode = 1 };
            }
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
                Relaunch(environment, ui, config);
                return new ApplyResult { Outcome = ApplyOutcome.AbortedStagingIncomplete, ExitCode = 1 };
            }
            if (environment.IsReparsePoint(source))
            {
                environment.Log($"Staged file is a reparse point, aborting: {relativePath}");
                Relaunch(environment, ui, config);
                return new ApplyResult { Outcome = ApplyOutcome.AbortedUnsafePath, ExitCode = 1 };
            }
        }

        // Marker survives an uncatchable interruption (power loss mid-swap). The next launch detects it and
        // warns. Written only now, once every precondition is met (game gone, staging complete), so any
        // earlier abort leaves nothing dangling. Derived from the manifest dest dir (the app data directory).
        string? markerPath = MarkerPath(config);
        if (markerPath is not null)
        {
            try { environment.WriteAllText(markerPath, "{}"); }
            catch (Exception ex) { environment.Log($"Could not write progress marker {markerPath}: {ex.Message}"); }
        }

        var backedUp = new List<string>();
        var copied = new List<string>();
        bool copyFailed = false;

        try
        {
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
                        Relaunch(environment, ui, config);
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

                if (!TryReplaceWithRetries(environment, source, dest, relativePath))
                {
                    copyFailed = true;
                    break;
                }
                copied.Add(relativePath);
                ui.SetProgress(copied.Count, config.FilesToCopy.Count);
            }

            if (copyFailed)
            {
                RestoreBackups(environment, config.InstallDir, rollbackDir, backedUp);
                try { environment.DeleteDirectory(rollbackDir); }
                catch (Exception ex) { environment.Log($"Cleanup: could not remove rollback dir {rollbackDir} after restore: {ex.Message}"); }
                ClearMarker(environment, markerPath);
                environment.Log("Update aborted and rolled back. Existing version left intact.");
                Relaunch(environment, ui, config);
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

            // Re-seal the bundle before verifying: on macOS the in-place swap above invalidated the .app's
            // sealed CodeResources hashes, so VerifyCodeSignature would ALWAYS fail without this and the
            // update could never complete. Fail closed - if the re-seal fails, roll back and relaunch the
            // old version, before the manifest is committed, so the old binaries + old manifest stay
            // consistent (same invariant as the verify gate below). No-op success off macOS.
            if (!environment.ResealCodeSignature(config.GameExePath))
            {
                environment.Log("Code signature re-seal FAILED after apply; rolling back.");
                RestoreBackups(environment, config.InstallDir, rollbackDir, backedUp);
                try { environment.DeleteDirectory(rollbackDir); }
                catch (Exception ex) { environment.Log($"Cleanup: could not remove rollback dir {rollbackDir} after restore: {ex.Message}"); }
                ClearMarker(environment, markerPath);
                Relaunch(environment, ui, config);
                return new ApplyResult { Outcome = ApplyOutcome.RolledBack, ExitCode = 1 };
            }

            // Fail closed: if the installed executable is not validly signed, roll back to the backups
            // (still present - we have not cleaned the rollback dir yet) and relaunch the old version.
            if (!environment.VerifyCodeSignature(config.GameExePath))
            {
                environment.Log("Code signature verification FAILED after apply; rolling back.");
                RestoreBackups(environment, config.InstallDir, rollbackDir, backedUp);
                try { environment.DeleteDirectory(rollbackDir); }
                catch (Exception ex) { environment.Log($"Cleanup: could not remove rollback dir {rollbackDir} after restore: {ex.Message}"); }
                ClearMarker(environment, markerPath);
                Relaunch(environment, ui, config);
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

            // Finishing phase: the new exe is in place, but on Windows the OS security scan may still hold it.
            // Wait for it to become launchable before relaunch, so we never start the game into a mid-scan
            // image (the STATUS_DLL_INIT_FAILED / STATUS_STACK_BUFFER_OVERRUN crash). No-op on non-Windows.
            ui.SetPhase(UpdaterPhase.Finishing);
            ui.SetStatus(theme.FinishingText);
            WaitForExeToSettle(environment, config.GameExePath);

            Relaunch(environment, ui, config);

            environment.Log(errors > 0 ? $"Update completed with {errors} error(s)." : "Update applied successfully!");
            return new ApplyResult
            {
                Outcome = ApplyOutcome.Succeeded,
                ExitCode = errors > 0 ? 1 : 0,
                CopiedFiles = copied,
                DeletedFiles = deleted
            };
        }
        catch (Exception ex)
        {
            // Backstop: any unexpected throw from the mutation phase must not crash the shim and orphan the
            // marker. Restore every file already overwritten, clear the marker, relaunch the old version,
            // and report a rollback (fail-closed, like the code-signature path).
            environment.Log($"Unexpected apply failure ({ex.Message}); rolling back.");
            RestoreBackups(environment, config.InstallDir, rollbackDir, backedUp);
            try { environment.DeleteDirectory(rollbackDir); }
            catch (Exception cleanup) { environment.Log($"Cleanup: could not remove rollback dir after restore: {cleanup.Message}"); }
            ClearMarker(environment, markerPath);
            Relaunch(environment, ui, config);
            return new ApplyResult { Outcome = ApplyOutcome.RolledBack, ExitCode = 1 };
        }
    }

    /// <summary>
    /// Relaunches the game (with the AV/image-race retry), keeping the progress window up across the whole
    /// retry wait so the user sees the "Finishing" marquee instead of a bare OS error dialog, then closes
    /// the window. Every relaunch site in <see cref="Apply(ApplyUpdateConfig, IUpdaterEnvironment, IUpdaterUi)"/>
    /// routes through here, so a rolled-back old-version relaunch gets the same resilience and logging.
    /// </summary>
    private static void Relaunch(IUpdaterEnvironment environment, IUpdaterUi ui, ApplyUpdateConfig config)
    {
        ResilientRelaunch(environment, config.GameExePath, config.InstallDir);
        ui.Close();
    }

    /// <summary>
    /// Launches the game and retries a Windows AV/image startup failure. Each attempt starts the process
    /// and watches it briefly (<see cref="IUpdaterEnvironment.TryRelaunch"/>); a process that survives the
    /// watch, or one that ran and exited on its own, is done. A fast startup failure (0xC0000142 etc.) or a
    /// launch error is retried after a capped, growing back-off, up to <see cref="RelaunchMaxAttempts"/>
    /// tries. If every attempt fails the update still stands - the new binaries are installed, only the
    /// auto-relaunch is abandoned (logged), so the next manual or on-launch start picks up the new version.
    /// On non-Windows the first attempt reports <see cref="RelaunchStartupOutcome.Running"/>, so this is a
    /// single launch with no retry or waiting.
    /// </summary>
    private static void ResilientRelaunch(IUpdaterEnvironment environment, string exePath, string workingDirectory)
    {
        for (int attempt = 1; attempt <= RelaunchMaxAttempts; attempt++)
        {
            environment.Log($"Relaunch attempt {attempt}/{RelaunchMaxAttempts}: {exePath}");
            RelaunchStartupOutcome outcome = environment.TryRelaunch(exePath, workingDirectory, RelaunchWatchMilliseconds);
            switch (outcome)
            {
                case RelaunchStartupOutcome.Running:
                    environment.Log($"Relaunch succeeded on attempt {attempt} (game is running).");
                    return;
                case RelaunchStartupOutcome.ExitedEarly:
                    environment.Log($"Relaunch on attempt {attempt}: the game ran and exited on its own (not a startup failure); done.");
                    return;
                case RelaunchStartupOutcome.StartupFailed:
                    environment.Log($"Relaunch attempt {attempt} hit a startup failure (antivirus/image race); will retry.");
                    break;
                case RelaunchStartupOutcome.LaunchError:
                    environment.Log($"Relaunch attempt {attempt} could not start the process; will retry.");
                    break;
            }

            if (attempt < RelaunchMaxAttempts)
            {
                int delay = Math.Min(RelaunchRetryBaseDelayMilliseconds * attempt, RelaunchRetryMaxDelayMilliseconds);
                environment.Log($"Waiting {delay}ms before relaunch retry {attempt + 1}...");
                environment.Sleep(delay);
            }
        }
        environment.Log($"Relaunch failed after {RelaunchMaxAttempts} attempts; giving up (the update is installed - the game will start on the next launch).");
    }

    /// <summary>
    /// Polls <see cref="IUpdaterEnvironment.CanOpenExclusively"/> until the just-written game exe is
    /// launchable (the OS security scan has released it) or the poll budget is exhausted, sleeping
    /// between polls. Returns as soon as it is openable, or after the timeout as a last resort (logged).
    /// On non-Windows the first poll passes (the real env returns true), so this is a no-op.
    /// </summary>
    private static void WaitForExeToSettle(IUpdaterEnvironment environment, string gameExePath)
    {
        if (string.IsNullOrEmpty(gameExePath))
        {
            return;
        }

        for (int attempt = 0; attempt < SettleMaxPolls; attempt++)
        {
            if (environment.CanOpenExclusively(gameExePath))
            {
                if (attempt > 0)
                {
                    environment.Log($"Game exe became launchable after {attempt} poll(s); relaunching.");
                }
                return;
            }
            environment.Log($"Waiting for security software to release the game exe ({attempt + 1}/{SettleMaxPolls})...");
            environment.Sleep(SettlePollDelayMilliseconds);
        }
        environment.Log("Timed out waiting for the game exe to become launchable; relaunching anyway.");
    }

    /// <summary>
    /// Polls <see cref="IUpdaterEnvironment.IsProcessAlive"/> until the parent process is gone or the
    /// barrier budget (<see cref="ParentGoneBarrierPolls"/>) is exhausted, sleeping between polls. Returns
    /// true once the process is gone, false if it is still alive at the end (the caller aborts untouched).
    /// Returns immediately when the process is already gone - the common path, since WaitForParentExit has
    /// already blocked for the shutdown.
    /// </summary>
    private static bool WaitForParentGone(IUpdaterEnvironment environment, int pid)
    {
        for (int attempt = 0; attempt < ParentGoneBarrierPolls; attempt++)
        {
            if (!environment.IsProcessAlive(pid))
            {
                return true;
            }
            environment.Log($"Waiting for the game to exit before applying ({attempt + 1}/{ParentGoneBarrierPolls})...");
            environment.Sleep(ParentGonePollDelayMilliseconds);
        }
        return !environment.IsProcessAlive(pid);
    }

    private static bool TryReplaceWithRetries(IUpdaterEnvironment environment, string source, string dest, string relativePath)
    {
        for (int attempt = 0; attempt < MaxCopyRetries; attempt++)
        {
            try
            {
                // Atomic swap (copy-to-temp + same-volume rename): the install file is only ever the
                // complete old or complete new content, so a concurrent scan or the relaunch never sees a
                // half-written image.
                environment.ReplaceFile(source, dest);
                environment.Log($"OK: {relativePath}");
                return true;
            }
            catch (IOException ex)
            {
                // A sharing violation (an AV scan still holds the old image): ride it out.
                LogRetry(environment, attempt, relativePath, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                // A locked running .exe/.dll and a denied delete-child both surface here as
                // ERROR_ACCESS_DENIED. Treat it like a sharing violation. A transient lock releases across
                // the retry budget. A permanent denial exhausts the loop and returns false to the caller's
                // rollback path instead of crashing the shim unhandled.
                LogRetry(environment, attempt, relativePath, ex);
            }
        }
        return false;
    }

    private static void LogRetry(IUpdaterEnvironment environment, int attempt, string relativePath, Exception ex)
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

    /// <summary>True when <paramref name="childDir"/> is <paramref name="parentDir"/> or nested under it.</summary>
    private static bool IsDirInside(string childDir, string parentDir)
    {
        if (string.IsNullOrEmpty(childDir) || string.IsNullOrEmpty(parentDir))
        {
            return false;
        }
        string child = Path.GetFullPath(childDir).TrimEnd(Path.DirectorySeparatorChar);
        string parent = Path.GetFullPath(parentDir).TrimEnd(Path.DirectorySeparatorChar);
        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(child, parent, cmp)
            || child.StartsWith(parent + Path.DirectorySeparatorChar, cmp);
    }

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
