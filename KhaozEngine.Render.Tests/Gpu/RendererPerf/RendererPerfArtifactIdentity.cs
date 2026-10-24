using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal readonly record struct RendererPerfFileId(ulong Volume, ulong Low, ulong High);
internal readonly record struct RendererPerfFileStatus(RendererPerfFileId Id, bool Directory);

// Only the artifact reader uses this platform boundary. IDs come from the opened handle and a
// no-follow directory-entry query, never path text, timestamps, length or advisory lock success.
internal interface IRendererPerfArtifactIdentity
{
    SafeFileHandle Open(SafeFileHandle? parent, string path, bool directory);
    RendererPerfFileStatus Handle(SafeFileHandle handle);
    RendererPerfFileStatus Entry(SafeFileHandle? parent, string path);
}

internal sealed class RendererPerfArtifactIdentity : IRendererPerfArtifactIdentity
{
    internal static readonly IRendererPerfArtifactIdentity Platform = new RendererPerfArtifactIdentity();
    static bool Mac => OperatingSystem.IsMacOS();
    static bool Windows => OperatingSystem.IsWindows();
    static bool X64 => RuntimeInformation.ProcessArchitecture == Architecture.X64;
    static void Supported()
    {
        if (!(Mac && (X64 || RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            || OperatingSystem.IsLinux() && X64 || Windows))
            throw new PlatformNotSupportedException("Artifact file identity is unavailable on this platform.");
    }
    public SafeFileHandle Open(SafeFileHandle? parent, string path, bool directory)
    {
        Supported();
        SafeFileHandle handle;
        if (Windows)
        {
            // OPEN_REPARSE_POINT makes the leaf observable rather than traversing it. Parent
            // directory identities are retained and checked separately by the reader.
            handle = CreateFile(path, directory ? 0x80u : 0x80000000u, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        }
        else
        {
            int flags = Mac ? 0x100 | 0x1000000 | 4 : 0x20000 | 0x80000 | 0x800;
            if (directory) flags |= Mac ? 0x100000 : 0x10000;
            int fd = OpenAt(parent is null ? (Mac ? -2 : -100) : parent.DangerousGetHandle().ToInt32(),
                parent is null ? path : Path.GetFileName(path), flags);
            if (fd < 0) throw Error("Artifact no-follow open failed");
            handle = new SafeFileHandle((IntPtr)fd, true);
        }
        if (handle.IsInvalid) { handle.Dispose(); throw Error("Artifact handle open failed"); }
        try
        {
            if (Handle(handle).Directory != directory) throw new IOException("Artifact has the wrong regular-file/directory kind.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    public RendererPerfFileStatus Handle(SafeFileHandle handle)
    {
        Supported();
        try
        {
            if (Windows) return WindowsStatus(handle);
            if (!Mac) return LinuxStatus(handle.DangerousGetHandle().ToInt32(), "", 0x1000);
            DarwinStat stat;
            int result = X64 ? FStat64(handle, out stat) : FStat(handle, out stat);
            if (result != 0) throw Error("Artifact handle identity unavailable");
            return DarwinStatus(stat);
        }
        catch (EntryPointNotFoundException error) { throw new PlatformNotSupportedException("Artifact handle identity unavailable.", error); }
    }
    public RendererPerfFileStatus Entry(SafeFileHandle? parent, string path)
    {
        Supported();
        try
        {
            if (Windows)
            {
                using var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) throw Error("Artifact entry identity unavailable");
                return WindowsStatus(handle);
            }
            int fd = parent is null ? (Mac ? -2 : -100) : parent.DangerousGetHandle().ToInt32();
            string name = parent is null ? path : Path.GetFileName(path);
            if (!Mac) return LinuxStatus(fd, name, 0x100);
            DarwinStat stat;
            int result = X64 ? FStatAt64(fd, name, out stat, 0x20) : FStatAt(fd, name, out stat, 0x20);
            if (result != 0) throw Error("Artifact entry identity unavailable");
            return DarwinStatus(stat);
        }
        catch (EntryPointNotFoundException error) { throw new PlatformNotSupportedException("Artifact entry identity unavailable.", error); }
    }
    static RendererPerfFileStatus DarwinStatus(DarwinStat stat)
        => Status(new(unchecked((uint)stat.Device), stat.Inode, 0), stat.Mode);
    static RendererPerfFileStatus LinuxStatus(int fd, string path, int flags)
    {
        const uint required = 0x101; // STATX_TYPE | STATX_INO. Device IDs are always returned.
        if (StatX(fd, path, flags, required, out var stat) != 0 || (stat.Mask & required) != required)
            throw Error("Artifact statx identity unavailable");
        return Status(new(((ulong)stat.DeviceMajor << 32) | stat.DeviceMinor, stat.Inode, 0), stat.Mode);
    }
    static RendererPerfFileStatus Status(RendererPerfFileId id, ushort mode)
    {
        int kind = mode & 0xf000;
        if (kind is not (0x8000 or 0x4000)) throw new IOException("Artifact is not a regular file/directory, or traverses a symbolic link.");
        return new(id, kind == 0x4000);
    }
    static RendererPerfFileStatus WindowsStatus(SafeFileHandle handle)
    {
        if (GetFileType(handle) != 1 || !FileTag(handle, 9, out var tag, 8) || (tag.Attributes & 0x440) != 0
            || !FileId(handle, 18, out var id, 24))
            throw Error("Artifact disk-file identity unavailable or reparse/device traversal refused");
        return new(new(id.Volume, id.Low, id.High), (tag.Attributes & 0x10) != 0);
    }
    static IOException Error(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    // Darwin LP64 stat ABI. x86_64 uses the INODE64 symbol, arm64 uses the sole 64-bit ABI.
    // apple-oss-distributions/xnu bsd/sys/stat.h (__DARWIN_STRUCT_STAT64), sys/fcntl.h.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    struct DarwinStat
    {
        [FieldOffset(0)] internal int Device;
        [FieldOffset(4)] internal ushort Mode;
        [FieldOffset(8)] internal ulong Inode;
    }
    // Linux UAPI statx is a fixed 256-byte ABI, independent of libc's struct stat.
    // torvalds/linux include/uapi/linux/stat.h, include/uapi/asm-generic/fcntl.h (x86_64).
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    struct LinuxStatX
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }
    // Windows SDK FILE_ID_INFO and FILE_ATTRIBUTE_TAG_INFO, GetFileInformationByHandleEx.
    [StructLayout(LayoutKind.Sequential)] struct WindowsId { internal ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] struct WindowsTag { internal uint Attributes, ReparseTag; }
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    static extern int OpenAt(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    static extern int FStat(SafeFileHandle handle, out DarwinStat stat);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    static extern int FStat64(SafeFileHandle handle, out DarwinStat stat);
    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    static extern int FStatAt(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out DarwinStat stat, int flags);
    [DllImport("libc", EntryPoint = "fstatat$INODE64", SetLastError = true)]
    static extern int FStatAt64(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out DarwinStat stat, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    static extern int StatX(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatX stat);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FileId(SafeFileHandle handle, int kind, out WindowsId info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FileTag(SafeFileHandle handle, int kind, out WindowsTag info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetFileType(SafeFileHandle handle);
}
