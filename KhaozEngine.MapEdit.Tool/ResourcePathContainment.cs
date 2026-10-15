using System;
using System.IO;

namespace KhaozEngine.MapEdit;

/// <summary>Decides whether a path relative to a resource root stays inside that root. Both <c>/</c> and <c>\</c>
/// count as separators, and drive and UNC forms count as rooted, on every platform, so the rule is the same
/// wherever it runs.</summary>
internal static class ResourcePathContainment
{
    /// <summary>True when <paramref name="relativeFromRoot"/>, as returned by
    /// <see cref="Path.GetRelativePath(string, string)"/> for a resolved target, names the root or a descendant.
    /// A rooted path, an empty path or any <c>..</c> segment is outside. A name that only starts with dots is not a
    /// parent segment.</summary>
    internal static bool IsInsideRoot(string relativeFromRoot)
    {
        ArgumentNullException.ThrowIfNull(relativeFromRoot);
        if (relativeFromRoot.Length == 0 || IsRooted(relativeFromRoot)) return false;
        foreach (string segment in relativeFromRoot.Split('/', '\\'))
            if (segment == "..") return false;
        return true;
    }

    static bool IsRooted(string path) =>
        Path.IsPathRooted(path) || path[0] is '/' or '\\' ||
        (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]));
}
