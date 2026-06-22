using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Updates;

namespace KhaozEngine.Tests.Updates;

/// <summary>
/// In-memory <see cref="IUpdaterEnvironment"/> modelling a virtual filesystem so the staged-apply
/// core can be tested without touching disk or spawning processes. Copy failures are simulated by
/// adding the source path to <see cref="ThrowOnCopyFrom"/>.
/// </summary>
internal sealed class FakeUpdaterEnvironment : IUpdaterEnvironment
{
    public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);
    public readonly HashSet<string> Directories = new(StringComparer.Ordinal);
    public readonly HashSet<string> ThrowOnCopyFrom = new(StringComparer.Ordinal);
    public readonly HashSet<string> ThrowOnDeleteOf = new(StringComparer.Ordinal);
    public readonly List<string> Log_ = new();
    public string? RelaunchedExe;
    public int SleepCalls;
    public int ParentWaits;

    public bool FileExists(string path) => Files.ContainsKey(path);

    public string ReadAllText(string path)
        => Files.TryGetValue(path, out string? content) ? content : throw new FileNotFoundException(path);

    public void WriteAllText(string path, string content) => Files[path] = content;

    public void CreateDirectory(string path) => Directories.Add(path);

    public void CopyFile(string source, string destination, bool overwrite)
    {
        if (ThrowOnCopyFrom.Contains(source))
        {
            throw new IOException($"simulated lock: {source}");
        }
        if (!Files.TryGetValue(source, out string? content))
        {
            throw new FileNotFoundException(source);
        }
        Files[destination] = content;
    }

    public void DeleteFile(string path)
    {
        if (ThrowOnDeleteOf.Contains(path))
        {
            throw new IOException($"simulated delete failure: {path}");
        }
        Files.Remove(path);
    }

    public void DeleteDirectory(string path)
    {
        string prefix = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var toRemove = new List<string>();
        foreach (string key in Files.Keys)
        {
            if (key == path || key.StartsWith(prefix, StringComparison.Ordinal))
            {
                toRemove.Add(key);
            }
        }
        foreach (string key in toRemove)
        {
            Files.Remove(key);
        }
    }

    public void Sleep(int milliseconds) => SleepCalls++;

    public void WaitForParentExit(int pid, int timeoutMilliseconds) => ParentWaits++;

    // Relocation: by default SelfExePath is null (the "POSIX / no relocation" signal), so Run applies in
    // place. Tests that exercise relocation set SelfExePath + SelfBaseDir to a dir inside the install.
    public string? SelfExePath;
    public string SelfBaseDir = "/elsewhere";
    public string? RelocatedExe;
    public string? RelocatedConfig;
    public string? RelocatedWorkdir;
    public readonly List<string> ScheduledDeletions = new();

    public string? GetSelfExecutablePath() => SelfExePath;

    public string GetSelfBaseDirectory() => SelfBaseDir;

    public void LaunchRelocatedUpdater(string updaterExePath, string applyConfigPath, string workingDirectory)
    {
        RelocatedExe = updaterExePath;
        RelocatedConfig = applyConfigPath;
        RelocatedWorkdir = workingDirectory;
    }

    public void ScheduleDirectoryDeletion(string directory) => ScheduledDeletions.Add(directory);

    public void Relaunch(string executablePath, string workingDirectory) => RelaunchedExe = executablePath;

    public void ClearQuarantine(string installDir) { }

    public readonly HashSet<string> ReparsePoints = new(StringComparer.Ordinal);

    public bool IsReparsePoint(string path) => Files.ContainsKey(path) && ReparsePoints.Contains(path);

    public bool CodeSignatureValid = true;

    public bool VerifyCodeSignature(string executablePath) => CodeSignatureValid;

    public void Log(string message) => Log_.Add(message);
}
