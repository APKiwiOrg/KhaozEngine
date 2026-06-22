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

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);

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
