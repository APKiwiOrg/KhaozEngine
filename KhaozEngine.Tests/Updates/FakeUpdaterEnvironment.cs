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

    // Settle-check (CanOpenExclusively) modelling: the first OpenExclusiveFailCount calls report the exe
    // as still locked (a running AV scan), then it becomes openable. Default 0 = always openable, so the
    // settle wait completes on the first poll and existing tests see no extra delay.
    public int OpenExclusiveFailCount;
    public int CanOpenExclusivelyCalls;
    // Snapshot of CanOpenExclusivelyCalls captured when Relaunch fires, so a test can prove the relaunch
    // happened only after the exe became openable (i.e. after the fail window, not during it).
    public int OpenCallsAtRelaunch = -1;

    // Destinations written through the atomic ReplaceFile path, in order. A test asserts the freshly
    // installed binaries (the exe/dlls) went through the atomic swap rather than a plain CopyFile.
    public readonly List<string> ReplacedDests = new();

    // Relaunch modelling: TryRelaunch returns outcomes from this queue in order; when it drains it
    // reports Running (the healthy-boot default), so existing tests that never enqueue see a normal launch.
    // Every call is counted so a test can assert the retry loop tried the expected number of times.
    public readonly Queue<RelaunchStartupOutcome> RelaunchOutcomes = new();
    public int RelaunchAttempts;

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

    // Atomic replace: in-memory it is a single dictionary assignment (already atomic), but it honours the
    // same simulated-lock switch as CopyFile so the rollback-on-copy-failure tests still trigger, and it
    // records the destination so a test can prove the applier routes install copies through the atomic path.
    // ReplaceFile throws UnauthorizedAccessException this many times first (a locked running image or a
    // denied delete-child), then behaves normally. Models both a transient denial (small count) and a
    // permanent one (count > MaxCopyRetries).
    public int UnauthorizedReplaceThrows;

    public void ReplaceFile(string source, string destination)
    {
        if (UnauthorizedReplaceThrows > 0)
        {
            UnauthorizedReplaceThrows--;
            throw new UnauthorizedAccessException($"simulated permission denial: {destination}");
        }
        if (ThrowOnCopyFrom.Contains(source))
        {
            throw new IOException($"simulated lock: {source}");
        }
        if (!Files.TryGetValue(source, out string? content))
        {
            throw new FileNotFoundException(source);
        }
        ReplacedDests.Add(destination);
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

    public bool CanOpenExclusively(string path)
    {
        CanOpenExclusivelyCalls++;
        return CanOpenExclusivelyCalls > OpenExclusiveFailCount;
    }

    public void WaitForParentExit(int pid, int timeoutMilliseconds) => ParentWaits++;

    // Barrier modelling: IsProcessAlive reports the parent alive for the first ParentAlivePolls calls, then
    // gone. Default 0 = gone on the first check, so existing tests cross the barrier with no extra sleeps.
    // Set above the barrier poll budget to model a game that never exits.
    public int ParentAlivePolls;
    public int IsProcessAliveCalls;

    public bool IsProcessAlive(int pid)
    {
        IsProcessAliveCalls++;
        return IsProcessAliveCalls <= ParentAlivePolls;
    }

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

    public void Relaunch(string executablePath, string workingDirectory)
    {
        RelaunchedExe = executablePath;
        OpenCallsAtRelaunch = CanOpenExclusivelyCalls;
    }

    public RelaunchStartupOutcome TryRelaunch(string executablePath, string workingDirectory, int watchMilliseconds)
    {
        RelaunchAttempts++;
        RelaunchStartupOutcome outcome = RelaunchOutcomes.Count > 0 ? RelaunchOutcomes.Dequeue() : RelaunchStartupOutcome.Running;
        // Record the launched exe only when the process actually got to run (Running / ExitedEarly), the
        // same states the applier treats as "done", so RelaunchedExe reflects the launch that stuck. Snapshot
        // the settle-poll count on the first such launch (mirrors the old Relaunch bookkeeping).
        if (outcome is RelaunchStartupOutcome.Running or RelaunchStartupOutcome.ExitedEarly)
        {
            RelaunchedExe = executablePath;
            if (OpenCallsAtRelaunch < 0)
            {
                OpenCallsAtRelaunch = CanOpenExclusivelyCalls;
            }
        }
        return outcome;
    }

    public void ClearQuarantine(string installDir) { }

    public readonly HashSet<string> ReparsePoints = new(StringComparer.Ordinal);

    public bool IsReparsePoint(string path) => Files.ContainsKey(path) && ReparsePoints.Contains(path);

    // Re-seal modelling: ResealSucceeds toggles the macOS bundle re-seal result (default true, so existing
    // tests see a healthy re-seal). ResealCalls counts invocations; VerifyCalledAfterReseals snapshots the
    // re-seal count when VerifyCodeSignature fires, so a test can prove the applier re-seals BEFORE it
    // verifies (mirrors the OpenCallsAtRelaunch ordering-proof pattern).
    public bool ResealSucceeds = true;
    public int ResealCalls;
    public int VerifyCalls;
    public int VerifyCalledAfterReseals = -1;

    public bool ResealCodeSignature(string executablePath)
    {
        ResealCalls++;
        return ResealSucceeds;
    }

    public bool CodeSignatureValid = true;

    public bool VerifyCodeSignature(string executablePath)
    {
        VerifyCalls++;
        if (VerifyCalledAfterReseals < 0)
        {
            VerifyCalledAfterReseals = ResealCalls;
        }
        return CodeSignatureValid;
    }

    public void Log(string message) => Log_.Add(message);
}
