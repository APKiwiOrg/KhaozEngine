using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KhaozEngine.MapDoc;

/// <summary>Best-effort directory fsync via a direct P/Invoke into <c>open(2)</c>/<c>fsync(2)</c>/<c>close(2)</c>.
/// This exists because <see cref="System.IO.File"/> and <see cref="System.IO.Directory"/> both refuse to open
/// a directory as a file at all (<see cref="UnauthorizedAccessException"/>) even though the underlying
/// syscalls do not care, so there is no way to reach this durability primitive through managed .NET IO. Linux
/// and macOS only, resolved at load time so the engine still ships one pure-managed assembly with no
/// per-RID native asset (mirrors <c>KhaozEngine.Determinism.FpNative</c>'s resolver pattern). Windows has no
/// directory-fsync primitive at all. Every call site checks <see cref="OperatingSystem.IsWindows"/> first and
/// this type is never invoked there.</summary>
internal static class UnixDirectorySync
{
    const string Lib = "ke_unix_libc";
    const int ORdOnly = 0;

    // CA2255: deliberate. This ModuleInitializer registers the native-library resolver for Lib before any
    // P/Invoke below runs, the same pattern KhaozEngine.Determinism.FpNative uses for libc/libm.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Init() => NativeLibrary.SetDllImportResolver(typeof(UnixDirectorySync).Assembly, Resolve);
#pragma warning restore CA2255

    static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Lib) return IntPtr.Zero;
        foreach (string candidate in LibCandidates())
            if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                return handle;
        return IntPtr.Zero;
    }

    /// <summary>The native libraries to try, in order. macOS has no standalone <c>libc.so</c>, the C library
    /// lives in <c>libSystem</c>. Linux's runtime-only <c>libc.so.6</c> is what is actually present outside a
    /// <c>-dev</c> package, plain <c>libc</c> is a dev-only symlink and rarely resolves at runtime.</summary>
    static string[] LibCandidates() => OperatingSystem.IsMacOS()
        ? new[] { "libSystem.dylib", "libc" }
        : new[] { "libc.so.6", "libc" };

    [DllImport(Lib, EntryPoint = "open", SetLastError = true)]
    static extern int Open(string pathname, int flags);

    [DllImport(Lib, EntryPoint = "fsync", SetLastError = true)]
    static extern int Fsync(int fd);

    [DllImport(Lib, EntryPoint = "close", SetLastError = true)]
    static extern int Close(int fd);

    /// <summary>Opens <paramref name="directory"/> <c>O_RDONLY</c> and fsyncs the descriptor, so a rename
    /// inside it is durable and not only ordered. Returns whether it actually succeeded (kept internal and
    /// returned, rather than void, so a test can tell a genuine fsync apart from a silently-doing-nothing
    /// no-op, which is exactly the bug this type replaces). The caller treats every outcome the same way:
    /// this is already a best-effort layer on top of the per-file <c>FileStream.Flush(flushToDisk: true)</c>
    /// that runs unconditionally, never the only thing standing between a save and data loss, so a missing
    /// directory, a permission error, a failed resolve of <see cref="Lib"/>, or the syscall itself returning
    /// an error all degrade to a no-op rather than throwing.</summary>
    internal static bool Flush(string directory)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            int fd = Open(directory, ORdOnly);
            if (fd < 0) return false;
            try { return Fsync(fd) == 0; }
            finally { Close(fd); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}
