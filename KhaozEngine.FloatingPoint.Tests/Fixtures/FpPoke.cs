using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Test-only native poke at the FP rounding mode, used to simulate the hostile per-thread FP state the
/// scope must neutralize. Declares its own P/Invoke (test infrastructure, not a production seam). It sets a
/// non-default control, so only KhaozEngine.FloatingPoint.Tests compiles it, and it is the one rounding poke there:
/// an assembly takes a single native library resolver, which its module initializer registers.
/// </summary>
internal static class FpPoke
{
    private const string Lib = "ke_fppoke";

    // FE_TONEAREST is 0 everywhere. FE_TOWARDZERO's encoding is the value the platform libc's fesetround
    // expects, and it differs by C runtime, not just arch:
    //   - Windows ucrt: 0x300 (_RC_CHOP) on every arch - ucrt abstracts the register write, so the abstract
    //     mode constant is the same on x64 and arm64.
    //   - glibc / musl / macOS x86 family: 0xC00 (the pre-shifted x87/SSE control-word bits).
    //   - glibc / macOS arm64: 0xC00000 (the pre-shifted FPCR RMode encoding).
    // NOTE: Bionic/Android arm64 uses an abstract (1<<5) encoding instead - if this test ever runs there,
    // this needs another branch.
    public const int RoundToNearest = 0;
    public static int RoundTowardZero =>
        OperatingSystem.IsWindows() ? 0x300
        : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0xC00000
        : 0xC00;

    [ModuleInitializer]
    internal static void Init() =>
        NativeLibrary.SetDllImportResolver(typeof(FpPoke).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        string[] candidates = OperatingSystem.IsMacOS()
            ? new[] { "libSystem.dylib", "libc" }
            : OperatingSystem.IsWindows()
                ? new[] { "ucrtbase.dll", "ucrtbase" }
                : new[] { "libm.so.6", "libc.so.6", "libm", "libc" };
        foreach (string c in candidates)
            if (NativeLibrary.TryLoad(c, out IntPtr h)) return h;
        return IntPtr.Zero;
    }

    public static void SetRound(int mode) => Assert.Equal(0, FeSetRound(mode));
    public static void SetRoundToNearest() => SetRound(RoundToNearest);
    public static void SetRoundTowardZero() => SetRound(RoundTowardZero);
    public static int GetRound() => FeGetRound();

    [DllImport(Lib, EntryPoint = "fesetround")]
    internal static extern int FeSetRound(int round);

    [DllImport(Lib, EntryPoint = "fegetround")]
    private static extern int FeGetRound();
}
