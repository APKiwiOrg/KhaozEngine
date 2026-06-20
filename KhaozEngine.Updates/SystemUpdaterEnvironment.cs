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
    private readonly Action<string>? logSink;

    public SystemUpdaterEnvironment(Action<string>? logSink = null)
    {
        this.logSink = logSink;
    }

    public bool FileExists(string path) => File.Exists(path);

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

            using Process? proc = Process.Start(new ProcessStartInfo
            {
                FileName = "codesign",
                ArgumentList = { "--verify", "--deep", "--strict", target },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc is null)
            {
                Log("codesign could not be started; treating as unverified.");
                return false;
            }
            proc.WaitForExit(15000);
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
