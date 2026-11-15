using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
                current = ResolveDeclaringDirectoryLink(info, current, depth);
            else current = info.FullName;
            if ((File.GetAttributes(current) & FileAttributes.Directory) == 0)
                throw new IOException("Declaring path component is not a directory.");
        }
        return current;
    }

    static string ResolveDeclaringDirectoryLink(DirectoryInfo link, string parent, int depth)
    {
        if (OperatingSystem.IsWindows())
        {
            // Open the target, without OPEN_REPARSE_POINT, so an unreachable alias always fails closed.
            string nativeLink = link.FullName;
            if (!nativeLink.StartsWith(@"\\?\", StringComparison.Ordinal) && !nativeLink.StartsWith(@"\\.\", StringComparison.Ordinal))
                nativeLink = nativeLink.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + nativeLink[2..] : @"\\?\" + nativeLink;
            using SafeFileHandle handle = OpenWindowsDirectory(nativeLink, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new IOException("Declaring directory link cannot be opened.", new Win32Exception(Marshal.GetLastPInvokeError()));
            var path = new StringBuilder(32768);
            uint length = GetFinalWindowsPath(handle, path, (uint)path.Capacity, 0);
            if (length == 0 && Marshal.GetLastPInvokeError() == 3)
            {
                // A volume without a drive letter still has a usable volume-GUID path.
                path.Clear();
                length = GetFinalWindowsPath(handle, path, (uint)path.Capacity, 1);
            }
            if (length == 0)
                throw new IOException("Declaring directory final path cannot be resolved.", new Win32Exception(Marshal.GetLastPInvokeError()));
            if (length >= path.Capacity)
                throw new IOException("Declaring directory final path exceeds the Windows path bound.");
            string resolved = path.ToString();
            if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + resolved[8..];
            if (resolved.Length >= 7 && resolved.StartsWith(@"\\?\", StringComparison.Ordinal) && resolved[5] == ':' && resolved[6] == '\\')
                return resolved[4..];
            return resolved;
        }

        // Keep raw target components so nested links resolve before any following parent segment.
        string target = link.LinkTarget ?? throw new IOException("Declaring directory link cannot be resolved.");
        return ResolveBase(Path.IsPathFullyQualified(target) ? target : Path.Combine(parent, target), depth + 1);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern SafeFileHandle OpenWindowsDirectory(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern uint GetFinalWindowsPath(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    static CoverageBuildDataException Error(CoverageBuildInputRefusal reason, CoverageInputLocation location, Exception? inner = null) =>
        new(reason, "Packed path does not satisfy confined input admission.", inner)
        { Path = location.Path, Identity = location.Identity, ChunkIndex = location.ChunkIndex, RecordIndex = location.RecordIndex };
}
