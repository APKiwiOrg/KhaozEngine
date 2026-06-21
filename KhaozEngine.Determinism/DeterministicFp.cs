using System;
using System.Runtime.InteropServices;

namespace KhaozEngine.Determinism;

/// <summary>
/// Opaque token holding a previously captured floating-point environment, returned by
/// <see cref="DeterministicFp.SetCanonical"/> and consumed by <see cref="DeterministicFp.Restore"/>.
/// Stored inline (no heap allocation).
/// </summary>
public readonly struct FpEnvToken
{
    internal readonly FpEnvBuffer Saved;
    internal readonly bool Captured;
    internal FpEnvToken(FpEnvBuffer saved, bool captured)
    {
        Saved = saved;
        Captured = captured;
    }
}

/// <summary>
/// Forces a canonical floating-point environment for deterministic fixed-tick / lockstep sims.
/// The canonical state is the IEEE default: round-to-nearest-even, flush-to-zero / denormals-are-zero
/// OFF, and FP exception traps disabled (masked). This pins the one piece of per-thread CPU state that
/// otherwise makes the same sim drift across threads, machines, and process runs.
/// </summary>
/// <remarks>
/// Controls the FP control register only (ARM64 FPCR, x86 MXCSR). It does NOT remove floating-point
/// non-determinism caused by JIT codegen variance (FMA contraction, auto-vectorization); see the
/// "Deterministic floating point" guidance in docs/USING-KHAOZENGINE.md.
/// </remarks>
public static class DeterministicFp
{
    private const int FE_TONEAREST = 0;   // FE_TONEAREST is 0 on glibc, musl, macOS, and ucrt.

    // Static-init order matters: probe support first, then resolve the per-platform default-env pointer,
    // then capture the process's startup environment as a last-resort canonical template.
    private static readonly bool _supported = Probe();
    private static readonly IntPtr _feDflEnv = ResolveFeDflEnv();
    private static readonly FpEnvBuffer _capturedDefault = CaptureDefault();

    /// <summary>
    /// True if FP control register access is wired up for this platform/architecture. When false,
    /// <see cref="SetCanonical"/> and <see cref="DeterministicFpScope.Enter"/> are safe no-ops whose
    /// restore is also a no-op (they never corrupt FP state).
    /// </summary>
    public static bool IsSupported => _supported;

    /// <summary>
    /// Applies the canonical FP environment to the calling thread and returns a token holding the prior
    /// environment, for an explicit <see cref="Restore"/> later (e.g. on sim-thread teardown). For
    /// scoped per-tick use prefer <see cref="DeterministicFpScope.Enter"/>.
    /// </summary>
    public static FpEnvToken SetCanonical()
    {
        if (!_supported)
            return default;   // Captured == false -> Restore is a no-op
        FpEnvBuffer saved = default;
        FpNative.FeGetEnv(ref saved);
        ApplyCanonical();
        return new FpEnvToken(saved, captured: true);
    }

    /// <summary>Restores a FP environment previously captured by <see cref="SetCanonical"/>.</summary>
    public static void Restore(in FpEnvToken token)
    {
        if (!token.Captured)
            return;
        FpEnvBuffer saved = token.Saved;
        FpNative.FeSetEnv(ref saved);
    }

    /// <summary>Installs the canonical environment on the calling thread (no save).</summary>
    private static void ApplyCanonical()
    {
        // On macOS/Linux arm64 the platform fenv_t holds the FPCR/FPSR pair, and a zeroed fenv_t writes
        // FPCR=0. That is canonical *by the ARM architecture itself* (RMode=0 -> round-to-nearest, FZ=0 ->
        // FTZ off, the trap-enable bits 0 -> all FP exceptions masked) - not by any OS default we'd have
        // to trust. So this is the most direct expression of "force IEEE-canonical", and because it is an
        // all-zero write it is also independent of the struct's field order/size (which differs: macOS is
        // 16 bytes {fpsr,fpcr} as u64, glibc arm64 is 8 bytes {fpcr,fpsr}). No symbol resolution needed.
        bool rawFpcrArm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                          && (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux());
        if (rawFpcrArm)
        {
            FpEnvBuffer zero = default;
            FpNative.FeSetEnv(ref zero);
        }
        else if (_feDflEnv != IntPtr.Zero)
        {
            FpNative.FeSetEnvPtr(_feDflEnv);   // FE_DFL_ENV: restores the platform default env
        }
        else
        {
            FpEnvBuffer canon = _capturedDefault;   // fallback: the startup environment
            FpNative.FeSetEnv(ref canon);
        }

        // Belt-and-braces: pin round-to-nearest on every path (FE_TONEAREST == 0 on glibc/musl/macOS/ucrt).
        // Harmless where fesetenv already set it; guarantees RN even on the captured-default fallback path
        // (where the startup env, in the unlikely event it was non-nearest, would otherwise leak through).
        FpNative.FeSetRound(FE_TONEAREST);
    }

    private static bool Probe()
    {
        Architecture arch = RuntimeInformation.ProcessArchitecture;
        if (arch != Architecture.X64 && arch != Architecture.Arm64)
            return false;
        try
        {
            FpEnvBuffer tmp = default;
            return FpNative.FeGetEnv(ref tmp) == 0;   // throws if the library/symbol is unavailable
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr ResolveFeDflEnv()
    {
        if (!_supported)
            return IntPtr.Zero;
        // glibc and musl define FE_DFL_ENV as the sentinel (fenv_t*)-1 on every architecture.
        if (OperatingSystem.IsLinux())
            return (IntPtr)(-1);
        // macOS / Windows expose a real _FE_DFL_ENV symbol; FE_DFL_ENV is its address.
        try
        {
            foreach (string lib in FpNative.LibCandidates())
            {
                if (!NativeLibrary.TryLoad(lib, out IntPtr handle))
                    continue;
                if (NativeLibrary.TryGetExport(handle, "_FE_DFL_ENV", out IntPtr p))
                    return p;
                NativeLibrary.Free(handle);   // loaded but no symbol here: don't leak the handle
            }
        }
        catch
        {
            // fall through to the captured-default template
        }
        return IntPtr.Zero;
    }

    private static FpEnvBuffer CaptureDefault()
    {
        FpEnvBuffer b = default;
        if (_supported)
            FpNative.FeGetEnv(ref b);
        return b;
    }
}

/// <summary>
/// RAII scope: on <see cref="Enter"/> saves the current FP environment and applies the canonical one;
/// on <see cref="Dispose"/> restores the saved environment. Allocation-free (a <c>readonly struct</c>).
/// Wrap a sim tick or whole sim run:
/// <code>using (DeterministicFpScope.Enter()) { sim.Tick(dt); }</code>
/// </summary>
public readonly struct DeterministicFpScope : IDisposable
{
    private readonly FpEnvToken _token;
    private DeterministicFpScope(FpEnvToken token) => _token = token;

    /// <summary>Saves the current FP environment and applies the canonical one.</summary>
    public static DeterministicFpScope Enter() => new(DeterministicFp.SetCanonical());

    /// <summary>Restores the FP environment captured at <see cref="Enter"/>.</summary>
    public void Dispose() => DeterministicFp.Restore(_token);
}
