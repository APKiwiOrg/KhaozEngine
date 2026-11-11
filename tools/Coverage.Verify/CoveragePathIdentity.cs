using System;
using System.IO;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Resolves file and directory links before comparing input and output destinations.</summary>
internal static class CoveragePathIdentity
{
    internal static string Resolve(string path) => Resolve(Path.GetFullPath(path), 0);

    static string Resolve(string path, int followedLinks)
    {
        string root = Path.GetPathRoot(path)!;
        string[] parts = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        string resolved = root;
        for (int i = 0; i < parts.Length; i++)
        {
            resolved = Path.Combine(resolved, parts[i]);
            FileSystemInfo info = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
            if (info.LinkTarget == null) continue;
            if (followedLinks >= (OperatingSystem.IsWindows() ? 63 : 40)) throw new IOException("Too many filesystem links.");
            string target = info.ResolveLinkTarget(returnFinalTarget: false)!.FullName;
            for (int j = i + 1; j < parts.Length; j++) target = Path.Combine(target, parts[j]);
            // Restart at the target's root so links in its ancestors are resolved too.
            return Resolve(Path.GetFullPath(target), followedLinks + 1);
        }
        // Missing suffixes remain attached to the resolved deepest existing ancestor.
        return resolved;
    }
}
