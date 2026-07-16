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
    public readonly HashSet<string> ThrowOnWriteTo = new(StringComparer.Ordinal);
    public readonly List<string> Log_ = new();

    // Clock for the post-update marker's AppliedAtUtc. Defaults to a fixed, obviously-synthetic instant so
    // a test that does not set it still gets a stable timestamp. A marker-focused test overrides it to
    // assert the exact value round-trips.
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
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

    // Pre-mutation exclusive-open gate modelling (UpdateApplier.WaitForExeExclusivelyOpenable), kept
    // separate from the settle-check counters above so a settle-focused test (which sets
    // OpenExclusiveFailCount without caring about the gate) is unaffected: CanOpenExclusively routes to
    // THIS counter until the first file has actually been swapped in (ReplacedDests is still empty), which
    // is exactly when the gate runs in real code - chronologically before any ReplaceFile call, and the
    // settle check only ever runs after every file is copied. Default 0 = the gate clears on the first
    // poll, so existing tests (which never touch this field) see no extra call or delay.
    public int GateOpenExclusiveFailCount;
    public int GateCanOpenExclusivelyCalls;

    // Forces the post-commit settle check (WaitForExeToSettle -> CanOpenExclusively) to throw, to prove
    // the backstop treats a post-commit failure as success (not a false rollback).
    public bool ThrowOnSettleCheck;

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

    public void WriteAllText(string path, string content)
    {
        if (ThrowOnWriteTo.Contains(path))
        {
            throw new IOException($"simulated write failure: {path}");
        }
        Files[path] = content;
    }

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

    // Forces ReplaceFile to throw a non-IO / non-UAE exception, to prove the applier's top-level backstop
    // rolls back and clears the marker rather than letting an unexpected throw crash the shim.
    public bool ThrowUnexpectedOnReplace;

    public void ReplaceFile(string source, string destination)
    {
        if (ThrowUnexpectedOnReplace)
        {
            throw new InvalidOperationException("simulated unexpected updater failure");
        }
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
        // Route to the gate counter until a file has actually been swapped in: that mirrors the real
        // chronology (the gate runs before any ReplaceFile call; the settle check only runs after every
        // copy is done), so a test that only sets OpenExclusiveFailCount for the settle phase still sees
        // the gate clear on its first (uncounted-by-it) poll before settle-check behaviour kicks in.
        if (ReplacedDests.Count == 0)
        {
            GateCanOpenExclusivelyCalls++;
            return GateCanOpenExclusivelyCalls > GateOpenExclusiveFailCount;
        }
        if (ThrowOnSettleCheck)
        {
            throw new InvalidOperationException("simulated post-commit settle failure");
        }
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

    // Elevation modelling. CanWriteToDirectory reports every dir writable unless added to NonWritableDirs
    // (a Program-Files-like protected install). TryElevate records the handoff and returns ElevateSucceeds
    // (default true). Set false to model a declined UAC prompt.
    public readonly HashSet<string> NonWritableDirs = new(StringComparer.Ordinal);
    public bool ElevateSucceeds = true;
    public bool ElevateCalled;
    public string? ElevatedExe;
    public string? ElevatedConfig;
    public string? ElevatedWorkdir;

    public int CanWriteToDirectoryCalls;

    public bool CanWriteToDirectory(string path)
    {
        CanWriteToDirectoryCalls++;
        return !NonWritableDirs.Contains(path);
    }

    public bool TryElevate(string updaterExePath, string applyConfigPath, string workingDirectory)
    {
        ElevateCalled = true;
        ElevatedExe = updaterExePath;
        ElevatedConfig = applyConfigPath;
        ElevatedWorkdir = workingDirectory;
        return ElevateSucceeds;
    }

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
