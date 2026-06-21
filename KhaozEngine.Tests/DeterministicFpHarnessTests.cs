using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Determinism;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Repro harness for the host-sim FP-determinism bug (SpaceGame, 2026-06-21): the same fixed-seed,
/// fixed-input sim produced different final state depending on uncontrolled per-thread FP register
/// state. These tests deliberately corrupt the calling thread's FP environment (rounding mode) and
/// prove that the mini-sim is FP-env-sensitive AND that <see cref="DeterministicFpScope"/> forces a
/// canonical environment regardless, making the result byte-identical across threads and runs.
/// </summary>
public class DeterministicFpHarnessTests
{
    private const ulong Seed = 99173;   // the SpaceGame repro seed
    private const int Ticks = 3600;

    [Fact]
    public void MiniSimIsSensitiveToRounding_AndScopeForcesCanonical()
    {
        // Baseline: canonical rounding (nearest).
        FpPoke.SetRoundToNearest();
        ulong canonical = MiniSim.RunHash(Seed, Ticks);

        // Corrupt: round toward zero. The sim accumulates many rounding-sensitive terms, so this
        // must change the result -- otherwise the harness would be vacuous.
        FpPoke.SetRoundTowardZero();
        ulong corrupted = MiniSim.RunHash(Seed, Ticks);
        Assert.NotEqual(canonical, corrupted);

        // With the scope active on the corrupted thread, the result must match the canonical baseline.
        ulong scoped;
        using (DeterministicFpScope.Enter())
            scoped = MiniSim.RunHash(Seed, Ticks);
        Assert.Equal(canonical, scoped);

        // And the scope must have restored the corrupted rounding on dispose.
        Assert.Equal(FpPoke.RoundTowardZero, FpPoke.GetRound());
        FpPoke.SetRoundToNearest();
    }

    [Fact]
    public void ByteIdenticalAcrossRepeatedRuns_WithScope()
    {
        FpPoke.SetRoundTowardZero();   // hostile ambient state
        ulong first = RunScoped();
        for (int i = 0; i < 16; i++)
            Assert.Equal(first, RunScoped());
        FpPoke.SetRoundToNearest();

        static ulong RunScoped()
        {
            using (DeterministicFpScope.Enter())
                return MiniSim.RunHash(Seed, Ticks);
        }
    }

    [Fact]
    public async Task ByteIdenticalAcrossThreads_WithScope()
    {
        // Each worker pre-corrupts its own thread's FP state differently, then runs under the scope.
        // The scope must neutralize the difference so every thread agrees -- the lockstep invariant.
        ulong onMain;
        FpPoke.SetRoundTowardZero();
        using (DeterministicFpScope.Enter())
            onMain = MiniSim.RunHash(Seed, Ticks);
        FpPoke.SetRoundToNearest();

        ulong onPool = await Task.Run(() =>
        {
            FpPoke.SetRoundTowardZero();
            using (DeterministicFpScope.Enter())
                return MiniSim.RunHash(Seed, Ticks);
        });

        ulong onDedicated = 0;
        var thread = new Thread(() =>
        {
            FpPoke.SetRoundToNearest();   // a different ambient state than the pool worker
            using (DeterministicFpScope.Enter())
                onDedicated = MiniSim.RunHash(Seed, Ticks);
        });
        thread.Start();
        thread.Join();

        Assert.Equal(onMain, onPool);
        Assert.Equal(onMain, onDedicated);
    }
}

/// <summary>
/// Fixed-seed, fixed-input mini host-sim mirroring SpaceGame's failure shape: it damps a velocity
/// toward zero each tick (driving near-denormal intermediates) and accumulates rounding-sensitive
/// terms, then returns a hash of the final state. Pure except for the ambient FP environment.
/// </summary>
internal static class MiniSim
{
    public static ulong RunHash(ulong seed, int ticks)
    {
        var rng = new DeterministicRng(seed);          // pure integer stream; scripted "input"
        float vx = 1.0f, vy = -1.0f;
        float health = 100.0f;
        float acc = 0.0f;
        for (int t = 0; t < ticks; t++)
        {
            float input = rng.NextFloat() - 0.5f;      // deterministic scripted perturbation
            vx = (vx + input * 0.01f) * 0.97f;         // damp toward zero
            vy = (vy - input * 0.01f) * 0.97f;
            acc += vx / 3.0f + vy / 7.0f;              // rounding-sensitive accumulation
            health -= (MathF.Abs(vx) + MathF.Abs(vy)) * 0.001f;
        }
        // Hash the raw bits so any low-bit difference flips the result.
        ulong h = 1469598103934665603UL;               // FNV-1a 64
        h = Mix(h, (uint)BitConverter.SingleToInt32Bits(health));
        h = Mix(h, (uint)BitConverter.SingleToInt32Bits(acc));
        h = Mix(h, (uint)BitConverter.SingleToInt32Bits(vx));
        h = Mix(h, (uint)BitConverter.SingleToInt32Bits(vy));
        return h;
    }

    private static ulong Mix(ulong h, uint v)
    {
        for (int i = 0; i < 4; i++)
        {
            h ^= (byte)(v >> (i * 8));
            h *= 1099511628211UL;
        }
        return h;
    }
}

/// <summary>
/// Test-only native poke at the FP rounding mode, used to simulate the hostile per-thread FP state the
/// scope must neutralize. Declares its own P/Invoke (test infrastructure, not a production seam).
/// </summary>
internal static class FpPoke
{
    private const string Lib = "ke_fppoke";

    // FE_TONEAREST is 0 everywhere. FE_TOWARDZERO differs by arch: 0xC00 on the x86 family, 0xC00000 on
    // ARM64 (the glibc/macOS pre-shifted FPCR encoding). NOTE: Bionic/Android arm64 uses an abstract
    // (1<<5) encoding instead - if this test ever runs there, this constant needs an OS branch.
    public const int RoundToNearest = 0;
    public static int RoundTowardZero =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0xC00000 : 0xC00;

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

    public static void SetRoundToNearest() => FeSetRound(RoundToNearest);
    public static void SetRoundTowardZero() => Assert.Equal(0, FeSetRound(RoundTowardZero));
    public static int GetRound() => FeGetRound();

    [DllImport(Lib, EntryPoint = "fesetround")]
    private static extern int FeSetRound(int round);

    [DllImport(Lib, EntryPoint = "fegetround")]
    private static extern int FeGetRound();
}
