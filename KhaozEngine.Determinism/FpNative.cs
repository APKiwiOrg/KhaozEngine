using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KhaozEngine.Determinism;

/// <summary>
/// P/Invoke into the platform C library's <c>&lt;fenv.h&gt;</c> floating-point environment functions.
/// A logical library name is resolved at load time to the right native library per OS, so there is no
/// per-RID native build asset: the engine stays pure-managed and packs through the existing pipeline.
/// </summary>
internal static class FpNative
{
    private const string Lib = "ke_fpenv";

    [ModuleInitializer]
    internal static void Init() =>
        NativeLibrary.SetDllImportResolver(typeof(FpNative).Assembly, Resolve);

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Lib) return IntPtr.Zero;
        foreach (string candidate in LibCandidates())
            if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                return handle;
        return IntPtr.Zero;
    }

    /// <summary>The native libraries to try, in order, for this OS (fenv lives in libc/libm/ucrt).</summary>
    internal static string[] LibCandidates()
    {
        if (OperatingSystem.IsMacOS())
            return new[] { "libSystem.dylib", "libc" };
        if (OperatingSystem.IsWindows())
            return new[] { "ucrtbase.dll", "ucrtbase" };
        // Linux / other Unix: fenv functions historically live in libm; modern glibc also in libc.
        return new[] { "libm.so.6", "libc.so.6", "libm", "libc" };
    }

    // int fegetenv(fenv_t *envp);  -- save the current environment into the inline buffer.
    [DllImport(Lib, EntryPoint = "fegetenv")]
    internal static extern int FeGetEnv(ref FpEnvBuffer envp);

    // int fesetenv(const fenv_t *envp);  -- install an environment from the inline buffer.
    [DllImport(Lib, EntryPoint = "fesetenv")]
    internal static extern int FeSetEnv(ref FpEnvBuffer envp);

    // int fesetenv(const fenv_t *envp);  -- install via a raw pointer (e.g. the FE_DFL_ENV sentinel).
    [DllImport(Lib, EntryPoint = "fesetenv")]
    internal static extern int FeSetEnvPtr(IntPtr envp);

    // int fesetround(int round);  -- belt-and-braces round-to-nearest (FE_TONEAREST == 0 everywhere).
    [DllImport(Lib, EntryPoint = "fesetround")]
    internal static extern int FeSetRound(int round);
}
