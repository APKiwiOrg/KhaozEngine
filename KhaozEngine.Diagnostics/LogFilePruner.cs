using System;
using System.Collections.Generic;
using System.IO;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Keeps a per-launch file directory bounded: the retention half shared by <see cref="SessionLog"/>'s
/// <c>session-*.log</c> files and <see cref="CrashReport"/>'s crash files. Both write one timestamped file per
/// event into a caller-owned directory, so both need the same "keep the newest N" sweep, and one copy of it is
/// one thing to be right about.
/// <para>
/// THE TWO CALLERS DIFFER ON WHEN THEY SWEEP, WHICH IS WHY THE COUNT IS A PARAMETER RATHER THAN A CONVENTION.
/// <see cref="SessionLog"/> prunes BEFORE opening its own file, so it asks for one fewer than it wants to end
/// up with. <see cref="CrashReport"/> prunes AFTER its file exists, so it asks for exactly the number it wants
/// to keep: a crash that could not be written must not be able to delete a report that was.
/// </para>
/// </summary>
internal static class LogFilePruner
{
    /// <summary>
    /// Keeps at most <paramref name="maxRetained"/> - 1 EXISTING <c>{prefix}-*.log</c> files (newest by
    /// last-write time), so the directory holds at most <paramref name="maxRetained"/> once the caller opens
    /// its own. The match is prefix-open by design here: one session-log directory belongs to one process
    /// label.
    /// </summary>
    internal static void KeepNewest(string directory, int maxRetained, string prefix)
        => KeepNewest(directory, maxRetained - 1,
            name => name.StartsWith(prefix + "-", StringComparison.Ordinal)
                && name.EndsWith(".log", StringComparison.Ordinal));

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> files whose NAME satisfies <paramref name="matches"/>, deleting
    /// the rest. Best-effort: any I/O failure (locked file, missing or unreadable directory) is swallowed,
    /// matching the sinks' "logging never throws" contract, and this also runs on the crash path where a throw
    /// would be catastrophic.
    /// </summary>
    internal static void KeepNewest(string directory, int keep, Func<string, bool> matches)
    {
        if (keep < 0) keep = 0;
        try
        {
            if (!Directory.Exists(directory)) return;

            var files = new List<FileInfo>();
            foreach (FileInfo file in new DirectoryInfo(directory).GetFiles())
            {
                if (matches(file.Name)) files.Add(file);
            }
            if (files.Count <= keep) return;

            files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = keep; i < files.Count; i++)
            {
                try { files[i].Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
