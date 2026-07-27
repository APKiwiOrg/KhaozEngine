using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// can actually be checked. They never corrupt a thread-pool thread (it is reused, and a leaked rounding mode would
/// break unrelated FP-sensitive tests): the corruption is always on the calling thread and always restored in a
/// finally, mirroring the discipline in <c>DeterministicFpHarnessTests</c>.</para>
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
/// Test-only native poke at the FP rounding mode, so a test can observe the environment from inside a fanned-out
/// worker. Declares its own P/Invoke (test infrastructure, not a production seam). The sibling in
/// <c>KhaozEngine.Foundation.Tests</c> (<c>FpPoke</c>) does the same for the scope's own unit tests. They are kept
/// separate rather than shared so neither test project takes a reference on the other.
/// </summary>
internal static class FpRounding
{
    private const string Lib = "ke_fpround";

    /// <summary>FE_TONEAREST, which is 0 on glibc, musl, macOS and ucrt alike.</summary>
    public const int ToNearest = 0;

    /// <summary>FE_TOWARDZERO, whose encoding differs by C runtime rather than only by architecture: ucrt abstracts
    /// the register write (0x300 on every arch), while glibc/musl/macOS pass the pre-shifted control bits.</summary>
    public static int TowardZero =>
        OperatingSystem.IsWindows() ? 0x300
        : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0xC00000
        : 0xC00;

    [ModuleInitializer]
    internal static void Init() => NativeLibrary.SetDllImportResolver(typeof(FpRounding).Assembly, Resolve);

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

    public static int Get() => FeGetRound();
    public static void SetToNearest() => FeSetRound(ToNearest);
    public static void SetTowardZero() => FeSetRound(TowardZero);

    [DllImport(Lib, EntryPoint = "fesetround")]
    private static extern int FeSetRound(int round);

    [DllImport(Lib, EntryPoint = "fegetround")]
    private static extern int FeGetRound();
}
