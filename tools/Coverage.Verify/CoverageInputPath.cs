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
            // Match the runtime's lexical manifest path. The OS resolves any aliases in this base.
            string current = Path.GetFullPath(baseDirectory);
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

    static CoverageBuildDataException Error(CoverageBuildInputRefusal reason, CoverageInputLocation location, Exception? inner = null) =>
        new(reason, "Packed path does not satisfy confined input admission.", inner)
        { Path = location.Path, Identity = location.Identity, ChunkIndex = location.ChunkIndex, RecordIndex = location.RecordIndex };
}
