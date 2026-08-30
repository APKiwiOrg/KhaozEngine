using System;
using System.IO;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>
/// The one traversal guard for manifest-declared file paths, shared by <see cref="UpdateService"/> (download
/// staging) and <see cref="UpdateApplier"/> (apply). It lived apply-side only, which meant a hostile path was
/// caught after the file had already landed on disk during download. Manifests are signed, so this is defence
/// in depth rather than an attacker-reachable hole: a manifest-generator bug (a bad glob, a symlink) or a
/// compromised signing key can still declare a validly-signed path that writes anywhere the process can reach.
/// </summary>
internal static class UpdatePathSafety
{
    /// <summary>Turns a manifest's forward-slash relative path into a native one. Manifest paths are always
    /// forward-slash, whatever platform the manifest was generated on.</summary>
    internal static string ToNative(string relativePath) => relativePath.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// True when <paramref name="relativePath"/> is a plain forward-slash relative path that stays
    /// under <paramref name="rootDir"/>: not rooted, no drive letter, no <c>..</c> segment, no null
    /// byte, and resolving it against the root does not escape it. The root is the install directory
    /// apply-side and the staging directory download-side.
    /// </summary>
    internal static bool IsSafeRelativePath(string rootDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\0'))
        {
            return false;
        }
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
        {
            return false;
        }
        string[] segments = relativePath.Split('/', '\\');
        foreach (string segment in segments)
        {
            if (segment == "..")
            {
                return false;
            }
        }

        string fullRoot = Path.GetFullPath(rootDir);
        string combined = Path.GetFullPath(Path.Combine(fullRoot, ToNative(relativePath)));
        string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return combined.StartsWith(prefix, StringComparison.Ordinal);
    }
}
