using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The first-class engine presentation trace (ask D): gated behind <see cref="WorldClientConfig.PresentationTraceEnabled"/>,
/// it records per render frame the internal signals a consumer cannot otherwise see - the render time, the fixed
/// interpolation delay, seconds since the last snapshot, the per-remote starvation-hold flag, snapshot-arrival marks,
/// and the local reconcile-error - for the local avatar and every remote, dumpable to CSV. Off by default (null, zero
/// overhead). This is the headless promotion of the throwaway Ruinborne client-side position logger.
/// </summary>
public class PresentationTraceTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveCommand Right = new(new Vector2(1f, 0f), run: false, cameraYaw: 0f);

    sealed class Rig
    {
        public required WorldServer Server { get; init; }
        public required WorldClient A { get; init; }
        public required WorldClient B { get; init; }
        float clientAccum = 0.5f / 30f, serverAccum;
        public float Tick => 1f / 30f;

        public void Frame(float dt)
        {
            clientAccum += dt;
            while (clientAccum >= Tick) { clientAccum -= Tick; B.SendInput(Right); A.SendInput(Right); }
            serverAccum += dt;
            while (serverAccum >= Tick) { serverAccum -= Tick; Server.Poll(); Server.Tick(Tick); }
            A.Poll(dt); B.Poll(dt);
            A.AdvancePresentation(dt); B.AdvancePresentation(dt);
        }
    }

    static Rig NewRig(bool traceOnA)
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds, PresentationTraceEnabled = traceOnA });
        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });
        var rig = new Rig { Server = server, A = a, B = b };
        for (int i = 0; i < 90; i++) rig.Frame(config.TickSeconds);
        Assert.True(a.Joined && b.Joined);
        Assert.True(b.LocalNetId > 0);
        return rig;
    }

    [Fact]
    public void Trace_is_null_by_default()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        _ = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });
        Assert.Null(a.PresentationTrace);
    }

    [Fact]
    public void Enabled_trace_records_local_and_remote_rows_with_internal_signals()
    {
        Rig rig = NewRig(traceOnA: true);
        PresentationTrace? trace = rig.A.PresentationTrace;
        Assert.NotNull(trace);

        float dt = rig.Tick / 5.9f;
        for (int i = 0; i < 120; i++) rig.Frame(dt);

        var rows = trace!.Rows.ToList();
        Assert.NotEmpty(rows);
        // Both the local avatar and the remote B are traced.
        Assert.Contains(rows, r => r.IsLocal);
        Assert.Contains(rows, r => !r.IsLocal && r.EntityId == rig.B.LocalNetId);

        // The internal render-time signal is exactly renderTime = presentationClock - interpolationDelay.
        foreach (PresentationTrace.Row r in rows)
            Assert.Equal(r.T - r.InterpolationDelay, r.RenderTime, 5);

        // Steady loopback streaming: no remote ever hits the snapshot-starvation hold.
        Assert.DoesNotContain(rows, r => !r.IsLocal && r.Held);

        // A snapshot arrival is marked on some frames, and the interpolation delay is the configured 2 ticks.
        Assert.Contains(rows, r => r.SnapshotArrived);
        Assert.Equal(2f * rig.Tick, rows[0].InterpolationDelay, 5);
    }

    [Fact]
    public void Trace_writes_a_csv_header_and_rows()
    {
        Rig rig = NewRig(traceOnA: true);
        for (int i = 0; i < 30; i++) rig.Frame(rig.Tick / 3f);

        string path = Path.Combine(Path.GetTempPath(), $"ke-pres-trace-{Guid.NewGuid():N}.csv");
        try
        {
            rig.A.PresentationTrace!.WriteCsv(path);
            string[] lines = File.ReadAllLines(path);
            Assert.True(lines.Length > 1, "CSV should have a header plus rows");
            Assert.Contains("renderTime", lines[0]);
            Assert.Contains("held", lines[0]);
            Assert.Contains("reconcileError", lines[0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
