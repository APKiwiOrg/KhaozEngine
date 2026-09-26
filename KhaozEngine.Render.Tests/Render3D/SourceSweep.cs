using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// Reads the engine's own source at test time for the frame view sweeps. It is located through
/// <see cref="CallerFilePathAttribute"/>, like <c>MetalCopyBufferCallSiteTests</c> and the shader byte-equality
/// tables, so it depends on neither the working directory nor the build output layout.
/// </summary>
internal static class SourceSweep
{
    /// <summary>The <c>KhaozEngine.Render3D</c> package directory.</summary>
    internal static string Render3DRoot() => Path.Combine(RepositoryRoot(), "KhaozEngine.Render3D");

    /// <summary>Every <c>.cs</c> file under <c>KhaozEngine.Render3D</c> with its comments blanked, in ordinal path order,
    /// build output excluded.</summary>
    internal static IEnumerable<(string Path, string Text)> Render3DSources()
    {
        string root = Render3DRoot();
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Segments(root, path).Any(segment => segment is "bin" or "obj"))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (path, WithoutComments(File.ReadAllText(path))));
    }

    /// <summary>One file of the package by its path relative to the package root, comments blanked.</summary>
    internal static string Render3DSource(string relativePath)
        => WithoutComments(File.ReadAllText(Path.Combine(Render3DRoot(), relativePath)));

    internal static string[] Segments(string root, string path)
        => Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    internal static int Line(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index; i++) if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>Comments blanked in place, length and newlines preserved, so an index still maps to its line. String
    /// literals are left alone: nothing in this repository writes a receiver, a dot and a member name inside one, and a
    /// blanker that tracked verbatim, interpolated and raw strings could desync and hide a real call site.
    /// <c>MetalCopyBufferCallSiteTests</c> reads its sources through this one.</summary>
    internal static string WithoutComments(string text)
    {
        char[] chars = text.ToCharArray();
        for (int i = 0; i < chars.Length - 1; i++)
        {
            if (chars[i] != '/') continue;
            if (chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
            }
            else if (chars[i + 1] == '*')
            {
                while (i < chars.Length && !(chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/'))
                {
                    if (chars[i] != '\n') chars[i] = ' ';
                    i++;
                }
                if (i < chars.Length) chars[i] = ' ';
                if (i + 1 < chars.Length) chars[i + 1] = ' ';
                i++;
            }
        }
        return new string(chars);
    }

    /// <summary>The repository root, found by walking up from this source file to the solution.</summary>
    internal static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        DirectoryInfo? directory = new FileInfo(thisFile).Directory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KhaozEngine.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("The source sweep could not find KhaozEngine.slnx above " + thisFile
            + ". It reads the repository's own source at test time, so it needs the checked-out tree.");
    }
}
