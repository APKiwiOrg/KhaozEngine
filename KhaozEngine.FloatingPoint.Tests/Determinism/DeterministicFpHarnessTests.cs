using System;
using System.Threading;
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

    // IMPORTANT: every test here corrupts the calling thread's FP rounding mode. It MUST be restored in
    // a finally on every thread touched, or the corrupted rounding leaks into a reused thread and breaks
    // unrelated FP-sensitive tests (xUnit shares threads). For the same reason these tests never corrupt a
    // ThreadPool thread (which is reused) - cross-thread coverage uses dedicated threads that die on join.

    [Fact]
    public void MiniSimIsSensitiveToRounding_AndScopeForcesCanonical()
    {
        try
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
        }
        finally
        {
            FpPoke.SetRoundToNearest();
        }
    }

    [Fact]
    public void ByteIdenticalAcrossRepeatedRuns_WithScope()
    {
        try
        {
            FpPoke.SetRoundTowardZero();   // hostile ambient state
            ulong first = RunScoped();
            for (int i = 0; i < 16; i++)
                Assert.Equal(first, RunScoped());
        }
        finally
        {
            FpPoke.SetRoundToNearest();
        }

        static ulong RunScoped()
        {
            using (DeterministicFpScope.Enter())
                return MiniSim.RunHash(Seed, Ticks);
        }
    }

    [Fact]
    public void ByteIdenticalAcrossThreads_WithScope()
    {
        // Each worker pre-corrupts its own thread's FP state, runs under the scope, and restores nearest
        // in a finally. The scope must neutralize the ambient difference so every thread agrees -- the
        // lockstep invariant. Dedicated threads only (never the ThreadPool): they die on Join, so a
        // corrupted state cannot leak into a reused worker that later runs another test.
        ulong onMain = RunWorker(FpPoke.RoundTowardZero);
        ulong onThreadA = RunOnDedicatedThread(FpPoke.RoundTowardZero);
        ulong onThreadB = RunOnDedicatedThread(FpPoke.RoundToNearest);

        Assert.Equal(onMain, onThreadA);
        Assert.Equal(onMain, onThreadB);

        static ulong RunWorker(int ambientRounding)
        {
            try
            {
                FpPoke.SetRound(ambientRounding);
                using (DeterministicFpScope.Enter())
                    return MiniSim.RunHash(Seed, Ticks);
            }
            finally
            {
                FpPoke.SetRoundToNearest();
            }
        }

        static ulong RunOnDedicatedThread(int ambientRounding)
        {
            ulong result = 0;
            var thread = new Thread(() => result = RunWorker(ambientRounding));
            thread.Start();
            thread.Join();
            return result;
        }
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
