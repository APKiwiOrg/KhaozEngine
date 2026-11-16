using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KhaozEngine.Tools.Coverage;

internal enum CoverageStatLookup { Stat, XStatOnly, Neither, LoadFailed }
internal enum CoverageStatBinding { Stat, XStat }

/// <summary>Answers whether two output spellings reach one destination by the identity the OS resolves, never by
/// comparing strings. Links in any component, a link whose target contains dot segments, system aliases such as
/// /tmp on macOS and hard links all resolve to the same identity.</summary>
internal static class CoveragePathIdentity
{
    /// <summary>An existing entry is its volume and file identity. An absent entry is its existing parent's
    /// identity plus its folded final name.</summary>
    internal readonly record struct Destination(ulong Volume, ulong FileHigh, ulong FileLow, string? AbsentName);

    internal static bool SameDestination(string left, string right) => Of(left) == Of(right);

    /// <summary>The identity of an input-side path, or null when the OS cannot identify it. Such an input reaches no
    /// output destination, and input admission reports it in its own refusal family.</summary>
    internal static Destination? TryOf(string path)
    {
        try { return Of(path); }
        catch (CoverageBuildValidationException) { return null; }
    }

    /// <summary>Chooses how stat is called from what the C library exports. stat itself on glibc 2.33 and later, on
    /// musl and on macOS. glibc's __xstat on older glibc. A library or entry point that cannot be bound makes every
    /// destination unidentifiable, which refuses with OutputAlias.</summary>
    internal static CoverageStatBinding SelectStatBinding(CoverageStatLookup lookup) => lookup switch
    {
        CoverageStatLookup.Stat => CoverageStatBinding.Stat,
        CoverageStatLookup.XStatOnly => CoverageStatBinding.XStat,
        _ => throw Unidentifiable(null),
    };

    /// <summary>Refuses with OutputAlias when the OS cannot identify an existing entry, such as a dangling or looping
    /// link, and with OutputIo when the parent of an absent entry does not exist.</summary>
    internal static Destination Of(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (TryIdentify(full, out var existing)) return existing;
        if (IsLink(full)) throw Unidentifiable(null);
        string? parent = Path.GetDirectoryName(full);
        if (parent == null || !TryIdentify(parent, out var folder))
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputIo, "An output parent directory does not exist.");
        return folder with { AbsentName = Fold(Path.GetFileName(full)) };
    }

    // A case-insensitive or normalization-insensitive volume may merge names that differ only in those ways, so
    // they are one destination everywhere. That can refuse two such names on a case-sensitive volume, never merge
    // two destinations unnoticed.
    static string Fold(string name) => name.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    static bool IsLink(string path)
    {
        try { return new FileInfo(path).LinkTarget != null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    static CoverageBuildValidationException Unidentifiable(int? error, Exception? inner = null) =>
        new(CoverageBuildValidationRefusal.OutputAlias, "The operating system cannot identify an output destination.",
            inner ?? (error is { } code ? new IOException("Identity lookup failed with system error " + code + ".") : null));

    /// <summary>False only when the entry does not exist. Every other lookup failure refuses, including a native
    /// library or entry point the runtime cannot bind, so no untyped interop exception escapes.</summary>
    static bool TryIdentify(string path, out Destination identity)
    {
        identity = default;
        try
        {
            if (OperatingSystem.IsWindows()) return Windows.TryIdentify(path, out identity);
            // The stat layouts below are stated for 64-bit x86 and Arm only.
            if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)
                || !(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())) throw Unidentifiable(null);
            return Unix.TryIdentify(path, out identity);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or MarshalDirectiveException)
        { throw Unidentifiable(null, error); }
    }

    /// <summary>stat(2) resolves every component, links included, as the kernel does for an open. On 64-bit Linux
    /// (x86_64 and aarch64) struct stat begins with 64-bit st_dev then 64-bit st_ino, for glibc and musl alike. On
    /// macOS the 64-bit inode struct stat begins with 32-bit st_dev, 16-bit st_mode, 16-bit st_nlink, then 64-bit
    /// st_ino at offset 8. Intel macOS exports that layout as stat$INODE64, Apple silicon as stat. The C library is
    /// bound once through the runtime's own name resolution for "libc" and its exports are looked up at run time,
    /// because glibc before 2.33 exports only __xstat.</summary>
    static class Unix
    {
        const int NoEntry = 2, NotDirectory = 20; // ENOENT and ENOTDIR share these values on Linux and macOS.
        // Larger than struct stat on every supported platform (144 bytes on x86_64 Linux and on macOS).
        const int StatBufferBytes = 256;
        static readonly Lazy<Binding> Bound = new(Bind);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        delegate int StatFunction([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        delegate int XStatFunction(int version, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

        sealed record Binding(CoverageStatLookup Lookup, StatFunction? Stat, XStatFunction? XStat);

        internal static bool TryIdentify(string path, out Destination identity)
        {
            identity = default;
            var bound = Bound.Value;
            byte[] buffer = new byte[StatBufferBytes];
            // glibc's _STAT_VER for this struct stat layout is 1 on x86_64 and 0 on aarch64.
            int result = SelectStatBinding(bound.Lookup) == CoverageStatBinding.Stat ? bound.Stat!(path, buffer)
                : bound.XStat!(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 1 : 0, path, buffer);
            if (result != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is NoEntry or NotDirectory) return false;
                throw Unidentifiable(error);
            }
            ulong device = OperatingSystem.IsLinux() ? MemoryMarshal.Read<ulong>(buffer) : MemoryMarshal.Read<uint>(buffer);
            identity = new(device, 0, MemoryMarshal.Read<ulong>(buffer.AsSpan(8)), null);
            return true;
        }

        static Binding Bind()
        {
            string statName = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "stat$INODE64" : "stat";
            try
            {
                if (!NativeLibrary.TryLoad("libc", typeof(CoveragePathIdentity).Assembly, null, out IntPtr library))
                    return new(CoverageStatLookup.LoadFailed, null, null);
                if (NativeLibrary.TryGetExport(library, statName, out IntPtr stat))
                    return new(CoverageStatLookup.Stat, Marshal.GetDelegateForFunctionPointer<StatFunction>(stat), null);
                if (OperatingSystem.IsLinux() && NativeLibrary.TryGetExport(library, "__xstat", out IntPtr xstat))
                    return new(CoverageStatLookup.XStatOnly, null, Marshal.GetDelegateForFunctionPointer<XStatFunction>(xstat));
                return new(CoverageStatLookup.Neither, null, null);
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or ArgumentException)
            { return new(CoverageStatLookup.LoadFailed, null, null); }
        }
    }

    /// <summary>CreateFileW with FILE_FLAG_BACKUP_SEMANTICS opens files and directories and follows reparse points.
    /// FILE_ID_INFO holds a 64-bit volume serial number then a 128-bit file id. Volumes without it, such as some
    /// FAT volumes, answer through BY_HANDLE_FILE_INFORMATION: dwVolumeSerialNumber at offset 28, nFileIndexHigh
    /// at 44 and nFileIndexLow at 48.</summary>
    static class Windows
    {
        const uint ReadAttributes = 0x80, ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;
        const int FileNotFound = 2, PathNotFound = 3, FileIdInfo = 18, FileIdInfoBytes = 24, HandleInfoBytes = 52;

        internal static bool TryIdentify(string path, out Destination identity)
        {
            identity = default;
            using var handle = CreateFileW(path, ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is FileNotFound or PathNotFound) return false;
                throw Unidentifiable(error);
            }
            byte[] id = new byte[FileIdInfoBytes];
            if (GetFileInformationByHandleEx(handle, FileIdInfo, id, FileIdInfoBytes))
            {
                identity = new(MemoryMarshal.Read<ulong>(id), MemoryMarshal.Read<ulong>(id.AsSpan(16)), MemoryMarshal.Read<ulong>(id.AsSpan(8)), null);
                return true;
            }
            byte[] info = new byte[HandleInfoBytes];
            if (!GetFileInformationByHandle(handle, info)) throw Unidentifiable(Marshal.GetLastPInvokeError());
            identity = new(MemoryMarshal.Read<uint>(info.AsSpan(28)), MemoryMarshal.Read<uint>(info.AsSpan(44)), MemoryMarshal.Read<uint>(info.AsSpan(48)), null);
            return true;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, byte[] info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetFileInformationByHandle(SafeFileHandle handle, byte[] info);
    }
}
