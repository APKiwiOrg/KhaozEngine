using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>
/// Real <see cref="IUpdaterEnvironment"/>: BCL file IO plus <c>Process</c> for parent-wait, relaunch,
/// and the macOS quarantine clear. Logging is routed to a caller-supplied sink (the shim writes to a
/// log file next to the apply config) so this stays free of any logging-framework dependency, keeping
/// the shim trim/AOT friendly.
/// </summary>
public sealed class SystemUpdaterEnvironment : IUpdaterEnvironment
{
    private const int CodesignTimeoutMs = 15000;

    // Suffix for the temp file ReplaceFile writes next to its destination before the atomic rename.
    private const string ReplaceTempSuffix = ".ke-stage";

    private readonly Action<string>? logSink;

    public SystemUpdaterEnvironment(Action<string>? logSink = null)
    {
        this.logSink = logSink;
    }

    public bool FileExists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string content) => File.WriteAllText(path, content);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);

    public void ReplaceFile(string source, string destination)
    {
        // Copy to a temp file in the destination's own directory (same volume), then rename it into place.
        // File.Move(overwrite) on the same volume is MoveFileEx with MOVEFILE_REPLACE_EXISTING, an atomic
        // metadata swap - so the destination is only ever the complete old or complete new file, never a
        // half-written image the post-apply relaunch could load. A sharing violation on the rename (a scan
        // still holding the old image) surfaces as IOException, which the applier's retry loop handles.
        string temp = destination + ReplaceTempSuffix;
        File.Copy(source, temp, overwrite: true);
        try
        {
            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            // Leave the destination untouched and drop the temp so a retry starts clean.
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort */ }
            throw;
        }
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);

    public bool CanOpenExclusively(string path)
    {
        // Non-Windows has no self-scan lock to wait out; treat the file as always launchable so the
        // settle wait is a no-op there.
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }
        try
        {
            using FileStream _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            // Sharing violation (the AV scan still holds the file) or transient IO: not yet launchable.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // A denial can be a transient scan-time lock too; keep waiting rather than relaunch into it.
            return false;
        }
    }

    public void WaitForParentExit(int pid, int timeoutMilliseconds)
    {
        try
        {
            using Process parent = Process.GetProcessById(pid);
            parent.WaitForExit(timeoutMilliseconds);
        }
        catch (ArgumentException)
        {
            // Process already gone - nothing to wait for.
        }
    }

    public bool IsProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process: already gone
        }
        catch (InvalidOperationException)
        {
            return false; // the process exited and its association is gone
        }
    }

    public void Relaunch(string executablePath, string workingDirectory)
    {
        if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
        {
            Log($"Game exe not found, cannot relaunch: {executablePath}");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            WorkingDirectory = workingDirectory
        });
    }

    public RelaunchStartupOutcome TryRelaunch(string executablePath, string workingDirectory, int watchMilliseconds)
    {
        if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
        {
            Log($"Game exe not found, cannot relaunch: {executablePath}");
            return RelaunchStartupOutcome.LaunchError;
        }

        Process? proc;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                WorkingDirectory = workingDirectory
            });
        }
        catch (Exception ex)
        {
            // Includes Win32Exception when the OS refuses to start the image (e.g. an antivirus filter
            // still blocking execution): a retryable launch error.
            Log($"Relaunch could not start the process: {ex.Message}");
            return RelaunchStartupOutcome.LaunchError;
        }

        if (proc is null)
        {
            Log("Relaunch did not yield a process handle to watch; assuming it started.");
            return RelaunchStartupOutcome.Running;
        }

        using (proc)
        {
            // Watch briefly for a fast startup failure. The AV/image race manifests as an almost-immediate
            // exit with a Windows startup NTSTATUS (see IsStartupFailureCode); a healthy game is still busy
            // booting well past this window. WaitForExit returns as soon as the child dies, so a failing
            // attempt costs only the child's fast-fail time, not the whole window.
            bool exited;
            try { exited = proc.WaitForExit(watchMilliseconds); }
            catch (Exception ex)
            {
                Log($"Could not observe the relaunched process ({ex.Message}); assuming it started.");
                return RelaunchStartupOutcome.Running;
            }

            if (!exited)
            {
                return RelaunchStartupOutcome.Running;
            }

            int code;
            try { code = proc.ExitCode; }
            catch (Exception ex)
            {
                Log($"Could not read the relaunched process exit code ({ex.Message}); assuming it started.");
                return RelaunchStartupOutcome.Running;
            }

            if (IsStartupFailureCode(code))
            {
                Log($"Relaunched process exited fast with startup-failure status 0x{unchecked((uint)code):X8}.");
                return RelaunchStartupOutcome.StartupFailed;
            }

            Log($"Relaunched process exited fast with code 0x{unchecked((uint)code):X8} (not a startup failure).");
            return RelaunchStartupOutcome.ExitedEarly;
        }
    }

    /// <summary>
    /// True when a process exit code is a Windows image-load / startup NTSTATUS that the antivirus/image
    /// race produces (DLL init failed, stack-buffer-overrun fast-fail, access violation, DLL-not-found,
    /// entry-point-not-found). These are worth retrying once the security scan releases the new image. A
    /// clean exit, an ordinary non-zero code, or a managed CLR crash (0xE0434352) is NOT one of these -
    /// retrying would not help - so it reads as a genuine early run instead.
    /// </summary>
    internal static bool IsStartupFailureCode(int exitCode)
    {
        uint code = unchecked((uint)exitCode);
        return code == 0xC0000142  // STATUS_DLL_INIT_FAILED  ("unable to start correctly (0xc0000142)")
            || code == 0xC0000409  // STATUS_STACK_BUFFER_OVERRUN (ucrtbase fast-fail)
            || code == 0xC0000005  // STATUS_ACCESS_VIOLATION
            || code == 0xC0000135  // STATUS_DLL_NOT_FOUND
            || code == 0xC0000139; // STATUS_ENTRYPOINT_NOT_FOUND
    }

    // Windows is the only OS where a running process locks its own loaded .exe/.dll, so it is the only
    // OS where the updater must relocate out of the install dir to overwrite its own binaries. Returning
    // null elsewhere makes the applier skip relocation and apply in place (POSIX replaces the file's inode).
    public string? GetSelfExecutablePath()
        => OperatingSystem.IsWindows() ? Environment.ProcessPath : null;

    public string GetSelfBaseDirectory() => AppContext.BaseDirectory;

    public void LaunchRelocatedUpdater(string updaterExePath, string applyConfigPath, string workingDirectory)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = updaterExePath,
            ArgumentList = { "--apply", applyConfigPath, "--relocated" },
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        });
    }

    public void ScheduleDirectoryDeletion(string directory)
    {
        // Detached OS one-shot: wait a few seconds for THIS process to exit (releasing the locks on the
        // relocated binaries), then delete the scratch dir. Best-effort; the game's boot-time sweep is the
        // backstop if the machine dies before this runs.
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout /t 3 /nobreak > nul & rmdir /s /q \"{directory}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    ArgumentList = { "-c", $"sleep 3; rm -rf '{directory}'" },
                    UseShellExecute = false
                });
            }
        }
        catch (Exception ex)
        {
            Log($"Could not schedule deletion of {directory}: {ex.Message}");
        }
    }

    public void ClearQuarantine(string installDir)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xattr",
                Arguments = $"-dr com.apple.quarantine \"{installDir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })?.WaitForExit(5000);
        }
        catch
        {
            // Best-effort; failure to clear quarantine is not fatal.
        }
    }

    public bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    public bool ResealCodeSignature(string executablePath)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return true; // no bundle seal to rebuild off macOS
        }

        // Only a .app has a CodeResources seal to invalidate; a bare-executable install has nothing to
        // re-seal, so leave it to VerifyCodeSignature.
        string? appBundle = EnclosingAppBundle(executablePath);
        if (appBundle is null)
        {
            return true;
        }

        try
        {
            // Re-sign ad-hoc, inner-to-outer. Apple requires nested code be signed before the container
            // that seals it, and deprecates `codesign --deep` for signing, so we walk the bundle by hand
            // instead of using --deep. The end-user Mac has no Developer ID private key, so ad-hoc
            // (--sign -) is the only key available; it rebuilds the internal seal so VerifyCodeSignature
            // passes again, at the cost of Developer ID / notarization (acceptable post-first-launch - see
            // IUpdaterEnvironment.ResealCodeSignature). Any codesign failure returns false so the applier
            // rolls the update back (fail-closed).

            // 1) Nested code (Mach-O libraries + framework/helper bundles), deepest path first so inner
            //    items are signed before any container that references them.
            foreach (string nested in NestedSignables(appBundle))
            {
                if (!SignAdHoc(nested, preserveMetadata: false))
                {
                    return false;
                }
            }

            // 2) The top-level .app last: this signs the main executable and rebuilds CodeResources.
            //    Preserve the app's entitlements and signing flags (e.g. hardened runtime) so behaviour
            //    that depends on them survives the re-seal.
            return SignAdHoc(appBundle, preserveMetadata: true);
        }
        catch (Exception ex)
        {
            Log($"codesign re-seal error: {ex.Message}");
            return false;
        }
    }

    public bool VerifyCodeSignature(string executablePath)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return true; // no OS signature enforcement to re-check here
        }

        try
        {
            // Verify the .app bundle that contains the executable, not the inner Mach-O.
            string target = EnclosingAppBundle(executablePath) ?? executablePath;

            // We don't redirect codesign's output: we never read it, and draining a redirected pipe
            // (needed to avoid a full-buffer stall) would complicate the timed WaitForExit below.
            using Process? proc = Process.Start(new ProcessStartInfo
            {
                FileName = "codesign",
                ArgumentList = { "--verify", "--deep", "--strict", target },
                UseShellExecute = false
            });
            if (proc is null)
            {
                Log("codesign could not be started; treating as unverified.");
                return false;
            }
            proc.WaitForExit(CodesignTimeoutMs);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log($"codesign verification error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The enclosing <c>.app</c> bundle path for <paramref name="executablePath"/> (an exe at
    /// <c>Foo.app/Contents/MacOS/Foo</c> yields <c>Foo.app</c>), or null when the path is not inside a
    /// bundle. Shared by the re-seal and verify steps so both target the same bundle.
    /// </summary>
    internal static string? EnclosingAppBundle(string executablePath)
    {
        int appIndex = executablePath.IndexOf(".app/", StringComparison.Ordinal);
        return appIndex >= 0 ? executablePath[..(appIndex + 4)] : null;
    }

    // Signable items nested inside the bundle: Mach-O libraries (*.dylib / *.so) and nested code bundles
    // (*.framework / *.app / *.bundle / *.xpc directories), the .app itself excluded. Ordered deepest
    // path first so a container is always signed after everything it contains (inner-to-outer). Bare
    // Mach-O helper executables with no extension are not detected here - KE game bundles are a main
    // executable plus dylibs, and the top-level codesign signs that main executable.
    private static List<string> NestedSignables(string appBundle)
    {
        var items = new List<string>();
        foreach (string file in Directory.EnumerateFiles(appBundle, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".dylib", StringComparison.Ordinal) || file.EndsWith(".so", StringComparison.Ordinal))
            {
                items.Add(file);
            }
        }
        foreach (string dir in Directory.EnumerateDirectories(appBundle, "*", SearchOption.AllDirectories))
        {
            if (IsCodeBundleDir(dir))
            {
                items.Add(dir);
            }
        }
        items.Sort(static (a, b) => PathDepth(b).CompareTo(PathDepth(a)));
        return items;
    }

    private static bool IsCodeBundleDir(string dir)
        => dir.EndsWith(".framework", StringComparison.Ordinal)
        || dir.EndsWith(".app", StringComparison.Ordinal)
        || dir.EndsWith(".bundle", StringComparison.Ordinal)
        || dir.EndsWith(".xpc", StringComparison.Ordinal);

    private static int PathDepth(string path)
    {
        int depth = 0;
        foreach (char c in path)
        {
            if (c == '/')
            {
                depth++;
            }
        }
        return depth;
    }

    private bool SignAdHoc(string target, bool preserveMetadata)
    {
        var psi = new ProcessStartInfo { FileName = "codesign", UseShellExecute = false };
        psi.ArgumentList.Add("--force");
        psi.ArgumentList.Add("--sign");
        psi.ArgumentList.Add("-"); // ad-hoc: the only identity available on an end-user Mac
        if (preserveMetadata)
        {
            psi.ArgumentList.Add("--preserve-metadata=entitlements,flags");
        }
        psi.ArgumentList.Add(target);

        using Process? proc = Process.Start(psi);
        if (proc is null)
        {
            Log($"codesign could not be started to re-seal {target}.");
            return false;
        }
        proc.WaitForExit(CodesignTimeoutMs);
        if (proc.ExitCode != 0)
        {
            Log($"codesign re-seal failed for {target} (exit {proc.ExitCode}).");
            return false;
        }
        return true;
    }

    public void Log(string message) => logSink?.Invoke(message);
}
