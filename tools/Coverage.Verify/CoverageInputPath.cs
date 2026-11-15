using System;
using System.IO;

namespace KhaozEngine.Tools.Coverage;

internal static class CoverageInputPath
{
    internal static string ResolveContained(string baseDirectory, string relativePath, CoverageInputLocation location)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath.IndexOfAny(['\\', ':', '\0']) >= 0 || Path.IsPathRooted(relativePath))
            throw Error(CoverageBuildInputRefusal.PackedPathEscape, location);
        string[] components = relativePath.Split('/');
        foreach (string component in components)
            if (component is "" or "." or "..") throw Error(CoverageBuildInputRefusal.PackedPathEscape, location);
        try
        {
            string current = ResolveBase(baseDirectory, 0);
            foreach (string component in components)
            {
                current = Path.Combine(current, component);
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw Error(CoverageBuildInputRefusal.PackedPathLink, location);
            }
            return current;
        }
        catch (FileNotFoundException error) { throw Error(CoverageBuildInputRefusal.PackedChunkMissing, location, error); }
        catch (DirectoryNotFoundException error) { throw Error(CoverageBuildInputRefusal.PackedChunkMissing, location, error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw Error(CoverageBuildInputRefusal.InputIo, location, error); }
    }

    // Resolve aliases only in the declaring directory. Referenced components never follow a link.
    static string ResolveBase(string directory, int depth)
    {
        if (depth > 64) throw new IOException("Declaring directory has too many link components.");
        string absolute = Path.IsPathFullyQualified(directory) ? directory : Path.Combine(Directory.GetCurrentDirectory(), directory);
        string root = Path.GetPathRoot(absolute)!;
        string current = root;
        var components = absolute[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (string component in components)
        {
            if (component == ".") continue;
            if (component == "..") { current = Directory.GetParent(current)?.FullName ?? root; continue; }
            var info = new DirectoryInfo(Path.Combine(current, component));
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Keep raw target components so nested links resolve before any following parent segment.
                string target = info.LinkTarget ?? throw new IOException("Declaring directory link cannot be resolved.");
                current = ResolveBase(Path.IsPathFullyQualified(target) ? target : Path.Combine(current, target), depth + 1);
            }
            else current = info.FullName;
            if ((File.GetAttributes(current) & FileAttributes.Directory) == 0)
                throw new IOException("Declaring path component is not a directory.");
        }
        return current;
    }

    static CoverageBuildDataException Error(CoverageBuildInputRefusal reason, CoverageInputLocation location, Exception? inner = null) =>
        new(reason, "Packed path does not satisfy confined input admission.", inner)
        { Path = location.Path, Identity = location.Identity, ChunkIndex = location.ChunkIndex, RecordIndex = location.RecordIndex };
}
