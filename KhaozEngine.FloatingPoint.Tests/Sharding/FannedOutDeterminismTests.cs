using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

/// <summary>
/// The fan-out half of the determinism story (issue #197): <c>DeterministicFp</c> pins the floating-point control
/// register on the CALLING THREAD only, and both the shard host's per-cell tick and
/// <see cref="ThreadPoolJobScheduler"/> hand sim work to threads that are neither the caller's nor a dedicated sim
/// thread. Without a scope at the scheduling boundary the register is whatever the pool last left it at, and two
/// servers - or two runs on one machine - can silently diverge in the low bits over thousands of ticks.
/// <para>These tests observe the rounding mode from INSIDE the fanned-out body, which is the only place the claim
/// can actually be checked, and on the pool afterwards. They never set a control on a thread-pool thread themselves:
/// the corruption is always on the calling thread and always restored in a finally, mirroring the discipline in
/// <c>DeterministicFpHarnessTests</c>. The scheduler makes its calling thread canonical for the length of the
/// fan-out, so a pool thread created for it does not start with the caller's rounding, which on Linux arm64 it
/// otherwise did and kept after the caller was restored. The shard host test runs engine tick code inline under the
/// changed mode. Both are why this class runs in KhaozEngine.FloatingPoint.Tests, whose guard fails any test that
/// starts or ends with a changed control.</para>
/// </summary>
public class FannedOutDeterminismTests
{
    [Fact]
    public void ThreadPoolJobScheduler_runs_every_worker_body_in_the_canonical_FP_environment()
    {
        if (!IsFpObservable) return;   // an unsupported platform makes the scope a documented no-op

        var scheduler = new ThreadPoolJobScheduler();
        try
        {
            FpRounding.SetTowardZero();   // hostile ambient state on the CALLING thread

            // The single-job path runs inline on that corrupted thread, so it is the sharpest case: if the scope were
            // missing here the body would observe the caller's rounding.
            int inline = -1;
            scheduler.For(1, _ => inline = FpRounding.Get());
            Assert.Equal(FpRounding.ToNearest, inline);

            // And the real fan-out: every slice, on whatever pool thread it landed on.
            var observed = new int[8];
            scheduler.For(observed.Length, i => observed[i] = FpRounding.Get());
            Assert.All(observed, r => Assert.Equal(FpRounding.ToNearest, r));

            // The scope restores what it found, so the caller's own (deliberately hostile) state survives the call.
            Assert.Equal(FpRounding.TowardZero, FpRounding.Get());
        }
        finally
        {
            FpRounding.SetToNearest();
        }
    }

    // Every body waits until all have started, so the fan-out needs a thread per job and the pool grows while the
    // caller rounds toward zero. Then plain pool work, outside any scope, reads the rounding on every pool thread.
    // macOS threads did not inherit their creator's controls, so this passes there with or without the scheduler's
    // outer scope.
    [Fact]
    public async Task ThreadPoolJobScheduler_leaves_the_pool_threads_it_creates_at_the_default_rounding()
    {
        if (!IsFpObservable) return;

        int jobs = Environment.ProcessorCount + 2;
        try
        {
            FpRounding.SetTowardZero();
            using var started = new CountdownEvent(jobs);
            new ThreadPoolJobScheduler().For(jobs, _ => { started.Signal(); started.Wait(); });
        }
        finally
        {
            FpRounding.SetToNearest();
        }

        // One blocking item per pool thread, so each idle pool thread, including those just created, takes one.
        int probes = ThreadPool.ThreadCount;
        var observed = new ConcurrentDictionary<int, int>();
        using (var all = new CountdownEvent(probes))
        {
            Task[] work = new Task[probes];
            for (int i = 0; i < probes; i++)
                work[i] = Task.Run(() =>
                {
                    observed[Environment.CurrentManagedThreadId] = FpRounding.Get();
                    all.Signal();
                    all.Wait();
                });
            await Task.WhenAll(work);
        }
        Assert.NotEmpty(observed);
        Assert.All(observed.Values, r => Assert.Equal(FpRounding.ToNearest, r));
    }

    [Fact]
    public void ShardHost_ticks_every_cell_in_the_canonical_FP_environment()
    {
        if (!IsFpObservable) return;

        var host = new ShardHost(cellSize: 100f, tickSeconds: 0.1f, registry: new ReplicationRegistry())
        {
            // A scheduler that deliberately hands the body a thread with a corrupted rounding mode, INLINE, so the
            // corruption dies with this test rather than leaking into a reused pool worker.
            Scheduler = new HostileInlineScheduler(),
        };
        var probe = new RoundingProbeSystem();
        foreach (CellCoord coord in new[] { new CellCoord(0, 0), new CellCoord(1, 0), new CellCoord(0, 1) })
            host.EnsureCell(coord).World.AddSystem(probe);

        try
        {
            host.Tick(0.1f, maxTicksPerFrame: 1);
        }
        finally
        {
            FpRounding.SetToNearest();
        }

        Assert.Equal(3, probe.Observed.Count);
        Assert.All(probe.Observed, r => Assert.Equal(FpRounding.ToNearest, r));
    }

    // fegetround/fesetround are only wired for the platforms DeterministicFp itself supports. Elsewhere the scope is
    // a documented no-op and there is nothing to assert.
    private static bool IsFpObservable =>
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    private sealed class HostileInlineScheduler : IJobScheduler
    {
        public void For(int count, Action<int> body)
        {
            for (int i = 0; i < count; i++)
            {
                FpRounding.SetTowardZero();   // whatever the pool last left the register at
                body(i);
            }
        }
    }

    private sealed class RoundingProbeSystem : ISystem
    {
        public List<int> Observed { get; } = new();
        public void Update(World world, float dt) => Observed.Add(FpRounding.Get());
    }
}

/// <summary>
/// The rounding-mode reads and writes these tests make, so a test can observe the environment from inside a
/// fanned-out worker. It goes through <see cref="FpPoke"/>, the assembly's one rounding poke, because an assembly
/// takes a single native library resolver. Writes ignore the native status, as they always have.
/// </summary>
internal static class FpRounding
{
    /// <summary>FE_TONEAREST, which is 0 on glibc, musl, macOS and ucrt alike.</summary>
    public const int ToNearest = FpPoke.RoundToNearest;

    /// <summary>FE_TOWARDZERO, whose encoding differs by C runtime rather than only by architecture: ucrt abstracts
    /// the register write (0x300 on every arch), while glibc/musl/macOS pass the pre-shifted control bits.</summary>
    public static int TowardZero => FpPoke.RoundTowardZero;

    public static int Get() => FpPoke.GetRound();
    public static void SetToNearest() => FpPoke.FeSetRound(ToNearest);
    public static void SetTowardZero() => FpPoke.FeSetRound(TowardZero);
}
