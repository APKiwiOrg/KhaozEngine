using System;
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

    public bool VerifyCodeSignature(string executablePath)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return true; // no OS signature enforcement to re-check here
        }

        try
        {
            // Verify the .app bundle that contains the executable, not the inner Mach-O.
            string target = executablePath;
            int appIndex = executablePath.IndexOf(".app/", StringComparison.Ordinal);
            if (appIndex >= 0)
            {
                target = executablePath[..(appIndex + 4)];
            }

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

    public void Log(string message) => logSink?.Invoke(message);
}
