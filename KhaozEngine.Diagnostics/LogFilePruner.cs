using System.Collections.Generic;
using System.IO;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Keeps a per-launch file directory bounded: the retention half shared by <see cref="SessionLog"/>'s
/// <c>session-*.log</c> files and <see cref="CrashReport"/>'s crash files. Both write one timestamped file per
/// event into a caller-owned directory, so both need the same "keep the newest N" sweep, and one copy of it is
/// one thing to be right about.
/// </summary>
internal static class LogFilePruner
{
    /// <summary>
    /// Keeps at most <paramref name="maxRetained"/> - 1 EXISTING <c>{prefix}-*.log</c> files (newest by
    /// last-write time), so the directory holds at most <paramref name="maxRetained"/> once the caller opens
    /// its own. Best-effort: any I/O failure (locked file, missing or unreadable directory) is swallowed,
    /// matching the sinks' "logging never throws" contract, and this also runs on the crash path where a throw
    /// would be catastrophic.
    /// </summary>
    internal static void KeepNewest(string directory, int maxRetained, string prefix)
    {
        if (maxRetained < 1) maxRetained = 1;
        try
        {
            if (!Directory.Exists(directory)) return;
            var files = new List<FileInfo>(new DirectoryInfo(directory).GetFiles($"{prefix}-*.log"));
            if (files.Count < maxRetained) return;

            files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = maxRetained - 1; i < files.Count; i++)
            {
                try { files[i].Delete(); }
                catch (IOException) { }
                catch (System.UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (System.UnauthorizedAccessException) { }
    }
}
