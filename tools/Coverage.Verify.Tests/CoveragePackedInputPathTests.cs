using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using KhaozEngine.Tools.Coverage;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoveragePackedInputPathTests
{
    static readonly CoverageInputLocation Location = new("$.poseChunks[0].path", "synthetic.kepp", 0);

    [Theory]
    [InlineData("../outside.kepp")]
    [InlineData("/outside.kepp")]
    [InlineData("C:/outside.kepp")]
    [InlineData("C:outside.kepp")]
    [InlineData("//server/share/outside.kepp")]
    [InlineData("\\\\server\\share\\outside.kepp")]
    [InlineData("\\\\?\\C:\\outside.kepp")]
    [InlineData("poses\\000000.kepp")]
    [InlineData("poses/name:stream")]
    [InlineData("")]
    [InlineData("poses//000000.kepp")]
    [InlineData("poses/./000000.kepp")]
    [InlineData("poses/../000000.kepp")]
    [InlineData("poses/")]
    [InlineData("poses/\0name.kepp")]
    public void Packed_paths_stay_below_the_declaring_directory(string path)
    {
        using var files = new PathFiles();
        var error = Assert.Throws<CoverageBuildDataException>(() => CoverageInputPath.ResolveContained(files.Root, path, Location));
        Assert.Equal(CoverageBuildInputRefusal.PackedPathEscape, error.Reason);
        Assert.Equal(Location.Path, error.Path);
        Assert.Equal(0, error.ChunkIndex);
        AssertControl(files);
    }

    [DirectoryLinkTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Packed_paths_refuse_linked_components_even_with_an_inside_target(bool leaf, bool inside)
    {
        using var files = new PathFiles();
        string target = inside ? Path.Combine(files.Root, "poses") : files.Outside;
        string link = Path.Combine(files.Root, leaf ? "linked.kepp" : "linked");
        if (leaf) File.CreateSymbolicLink(link, Path.Combine(target, "000000.kepp"));
        else Directory.CreateSymbolicLink(link, target);
        try
        {
            string relative = leaf ? "linked.kepp" : "linked/000000.kepp";
            var error = Assert.Throws<CoverageBuildDataException>(() => CoverageInputPath.ResolveContained(files.Root, relative, Location));
            Assert.Equal(CoverageBuildInputRefusal.PackedPathLink, error.Reason);
            Assert.Equal(Location.Path, error.Path);
            AssertControl(files);
        }
        finally { if (leaf) File.Delete(link); else Directory.Delete(link); }
    }

    [DirectoryLinkTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Packed_paths_allow_a_base_directory_alias(int aliasKind)
    {
        using var files = new PathFiles();
        string link = Path.Combine(files.Folder, "alias");
        string redirect = Path.Combine(files.Folder, "redirect");
        string actualDirectory = files.Root;
        if (aliasKind >= 3)
        {
            // Regression #1814: the parent segment inside the alias target follows the nested redirect.
            actualDirectory = Path.Combine(files.Folder, "actual", "input");
            Directory.CreateDirectory(Path.Combine(actualDirectory, "child"));
            Directory.CreateDirectory(Path.Combine(actualDirectory, "poses"));
            File.WriteAllBytes(Path.Combine(actualDirectory, "poses", "000000.kepp"), CoveragePackedFixture.ChunkBytes());
            Directory.CreateSymbolicLink(redirect, Path.Combine(actualDirectory, "child"));
            if (aliasKind == 3)
            {
                Directory.CreateDirectory(Path.Combine(files.Folder, "poses"));
                File.WriteAllBytes(Path.Combine(files.Folder, "poses", "000000.kepp"), [0xff]);
            }
        }
        string target = aliasKind switch
        {
            1 => files.Folder,
            2 => Path.Combine(files.Root, "poses"),
            3 or 4 => Path.Combine("redirect", ".."),
            _ => files.Root
        };
        Directory.CreateSymbolicLink(link, target);
        try
        {
            string baseDirectory = aliasKind == 1 ? Path.Combine(link, "input") : aliasKind == 2 ? Path.Combine(link, "..") : link;
            string resolved = CoverageInputPath.ResolveContained(baseDirectory, "poses/000000.kepp", Location);
            Assert.Equal(CoveragePackedFixture.ChunkBytes(), File.ReadAllBytes(resolved));
            Assert.Equal(CoverageInputPath.ResolveContained(actualDirectory, "poses/000000.kepp", Location), resolved);
        }
        finally
        {
            Directory.Delete(link);
            if (aliasKind >= 3) Directory.Delete(redirect);
        }
    }

    [DirectoryLinkTheory]
    [InlineData("missing.kepp")]
    [InlineData("cycle.kepp")]
    public void Packed_paths_fail_closed_for_dangling_or_cyclic_links(string target)
    {
        using var files = new PathFiles();
        string link = Path.Combine(files.Root, "cycle.kepp");
        File.CreateSymbolicLink(link, target);
        try
        {
            var error = Assert.Throws<CoverageBuildDataException>(() => CoverageInputPath.ResolveContained(files.Root, "cycle.kepp", Location));
            Assert.Equal(CoverageBuildInputRefusal.PackedPathLink, error.Reason);
            AssertControl(files);
        }
        finally { File.Delete(link); }
    }

    [Fact]
    public void Packed_paths_report_a_missing_chunk_with_its_manifest_location()
    {
        using var files = new PathFiles();
        var error = Assert.Throws<CoverageBuildDataException>(() => CoverageInputPath.ResolveContained(files.Root, "poses/missing.kepp", Location));
        Assert.Equal(CoverageBuildInputRefusal.PackedChunkMissing, error.Reason);
        Assert.Equal(Location.Path, error.Path);
        AssertControl(files);
    }

    [WindowsVolumeJunctionTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Packed_paths_resolve_a_volume_guid_junction_with_a_drive_path_control(bool volumeGuid, bool unreachable, bool longPath)
    {
        using var files = new PathFiles();
        string parent = longPath ? Path.Combine(files.Folder, new string('a', 128), new string('b', 128)) : files.Folder;
        Directory.CreateDirectory(parent);
        string link = Path.Combine(parent, "junction");
        // MAX_PATH is the Win32 boundary bypassed by extended path spelling, independently of host policy.
        if (longPath) Assert.True(link.Length > 260);
        WindowsVolumeJunction.Create(link, unreachable ? Path.Combine(files.Folder, "missing") : files.Root, volumeGuid);
        try
        {
            // Regression #1814: LinkTarget strips the NT prefix from a volume-GUID mount target.
            string target = new DirectoryInfo(link).LinkTarget ?? throw new IOException("Junction has no target metadata.");
            Assert.Equal(!volumeGuid, Path.IsPathFullyQualified(target));
            if (volumeGuid)
            {
                string wrongDirectory = Path.Combine(parent, target, "poses");
                Directory.CreateDirectory(wrongDirectory);
                File.WriteAllBytes(Path.Combine(wrongDirectory, "000000.kepp"), [0xff]);
            }
            if (unreachable)
            {
                var error = Assert.Throws<CoverageBuildDataException>(() => CoverageInputPath.ResolveContained(link, "poses/000000.kepp", Location));
                Assert.Equal(CoverageBuildInputRefusal.InputIo, error.Reason);
                Assert.Equal(Location.Path, error.Path);
            }
            else
            {
                string resolved = CoverageInputPath.ResolveContained(link, "poses/000000.kepp", Location);
                Assert.Equal(CoveragePackedFixture.ChunkBytes(), File.ReadAllBytes(resolved));
            }
            AssertControl(files);
        }
        finally { Directory.Delete(link); }
    }

    static void AssertControl(PathFiles files)
    {
        string resolved = CoverageInputPath.ResolveContained(files.Root, "poses/000000.kepp", Location);
        Assert.Equal(CoveragePackedFixture.ChunkBytes(), File.ReadAllBytes(resolved));
    }

    sealed class PathFiles : IDisposable
    {
        internal string Folder { get; } = Path.Combine(Path.GetTempPath(), "khaozengine-coverage-packed-input", "task-1", Guid.NewGuid().ToString("N"));
        internal string Root => Path.Combine(Folder, "input");
        internal string Outside => Path.Combine(Folder, "outside");
        internal PathFiles()
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Folder);
            else Directory.CreateDirectory(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.Combine(Root, "poses"));
            Directory.CreateDirectory(Outside);
            File.WriteAllBytes(Path.Combine(Root, "poses", "000000.kepp"), CoveragePackedFixture.ChunkBytes());
            File.WriteAllBytes(Path.Combine(Outside, "000000.kepp"), CoveragePackedFixture.ChunkBytes());
        }
        public void Dispose() => Directory.Delete(Folder, true);
    }
}

public sealed class WindowsVolumeJunctionTheoryAttribute : TheoryAttribute
{
    static readonly Lazy<string?> UnavailableReason = new(Probe);
    public WindowsVolumeJunctionTheoryAttribute() => Skip = UnavailableReason.Value;

    static string? Probe()
    {
        if (!OperatingSystem.IsWindows()) return "Volume-GUID junction regression requires Windows.";
        string? linkReason = new DirectoryLinkTheoryAttribute().Skip;
        if (linkReason is not null) return linkReason;
        string folder = Path.Combine(Path.GetTempPath(), "khaozengine-coverage-packed-input", "task-1", "volume-probe-" + Guid.NewGuid().ToString("N"));
        string link = Path.Combine(folder, "junction");
        string target = Path.Combine(folder, "input");
        Directory.CreateDirectory(target);
        try
        {
            WindowsVolumeJunction.Create(link, target, volumeGuid: true);
            return null;
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 1 or 5 or 50 or 1314)
        { return $"Windows volume junction creation is unavailable without filesystem support or permission (error {error.NativeErrorCode})."; }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(folder, true);
        }
    }
}

internal static class WindowsVolumeJunction
{
    internal static void Create(string link, string targetDirectory, bool volumeGuid)
    {
        string target = targetDirectory;
        if (volumeGuid)
        {
            var root = new StringBuilder(32768);
            if (!GetVolumePathName(targetDirectory, root, (uint)root.Capacity))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var volume = new StringBuilder(50);
            if (!GetVolumeNameForVolumeMountPoint(root.ToString(), volume, (uint)volume.Capacity))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            target = volume.ToString() + Path.GetRelativePath(root.ToString(), targetDirectory);
        }
        string substitute = @"\??\" + (target.StartsWith(@"\\?\", StringComparison.Ordinal) ? target[4..] : target);
        byte[] substituteBytes = Encoding.Unicode.GetBytes(substitute);
        byte[] printBytes = Encoding.Unicode.GetBytes(target);
        byte[] buffer = new byte[20 + substituteBytes.Length + printBytes.Length];
        // MOUNT_POINT_REPARSE_BUFFER has an eight-byte common header and eight-byte name descriptor.
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xa0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substituteBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substituteBytes.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)printBytes.Length));
        substituteBytes.CopyTo(buffer, 16);
        printBytes.CopyTo(buffer, 18 + substituteBytes.Length);
        Directory.CreateDirectory(link);
        string nativeLink = link;
        if (!nativeLink.StartsWith(@"\\?\", StringComparison.Ordinal) && !nativeLink.StartsWith(@"\\.\", StringComparison.Ordinal))
            nativeLink = nativeLink.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + nativeLink[2..] : @"\\?\" + nativeLink;
        using SafeFileHandle handle = CreateFile(nativeLink, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!DeviceIoControl(handle, 0x000900a4, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetVolumePathName(string path, StringBuilder root, uint capacity);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder volume, uint capacity);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputLength,
        IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
}
