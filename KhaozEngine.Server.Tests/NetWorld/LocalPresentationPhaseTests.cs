using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// <see cref="WorldClient.AdvancePresentation(float, float)"/> over real loopback (#1313): the command clock's
/// residual reaches the local avatar's prediction and nothing else. The remote render clock, the server tick and the
/// presentation trace advance on the frame time exactly as under <see cref="WorldClient.AdvancePresentation(float)"/>.
/// </summary>
public class LocalPresentationPhaseTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveCommand Right = new(new Vector2(1f, 0f), run: false, cameraYaw: 0f);   // +X
    const float Tick = 1f / 30f;

    sealed class Rig
    {
        public required WorldServer Server { get; init; }
        public required WorldClient A { get; init; }   // the local avatar under test, traced
        public required WorldClient B { get; init; }   // a remote mover A observes
        public required bool Phased { get; init; }
        // The client command clock and the server tick run half a tick out of phase, so ticks land mid-frame and the
        // reconcile that follows a prediction lands mid inter-tick, as in a real loop.
        readonly FixedTickHost commandClock = new(Tick);
        float serverAccum;

        public Rig() => commandClock.Advance(0.5f * Tick, _ => { });

        public float CommandPhase => commandClock.TickSeconds - commandClock.SecondsUntilNextTick;

        public void Frame(float dt)
        {
            commandClock.Advance(dt, _ => { B.SendInput(Right); A.SendInput(Right); });
            serverAccum += dt;
            while (serverAccum >= Tick) { serverAccum -= Tick; Server.Poll(); Server.Tick(Tick); }
            A.Poll(dt); B.Poll(dt);
            if (Phased) A.AdvancePresentation(dt, CommandPhase);
            else A.AdvancePresentation(dt);
            B.AdvancePresentation(dt);
        }
    }

    static Rig NewRig(bool phased)
    {
        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig { TickSeconds = Tick, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = Tick, PresentationTraceEnabled = true });
        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = Tick });
        var rig = new Rig { Server = server, A = a, B = b, Phased = phased };
        for (int i = 0; i < 90; i++) rig.Frame(Tick);
        Assert.True(a.Joined && b.Joined);
        Assert.True(b.LocalNetId > 0);
        return rig;
    }

    // One second to settle into the frame rate, then the per-frame local X steps over the next second.
    static List<float> SteadyLocalSteps(Rig rig, int frameRate)
    {
        float dt = 1f / frameRate;
        for (int i = 0; i < frameRate; i++) rig.Frame(dt);
        var steps = new List<float>(frameRate);
        float previous = rig.A.LocalRenderState.Position.X;
        for (int i = 0; i < frameRate; i++)
        {
            rig.Frame(dt);
            float current = rig.A.LocalRenderState.Position.X;
            steps.Add(current - previous);
            previous = current;
        }
        return steps;
    }

    [Theory]
    [InlineData(100)]
    [InlineData(144)]
    [InlineData(165)]
    public void Phase_overload_moves_the_local_avatar_the_same_distance_every_frame(int frameRate)
    {
        Rig phased = NewRig(phased: true);
        Rig legacy = NewRig(phased: false);
        List<float> phasedSteps = SteadyLocalSteps(phased, frameRate);
        List<float> legacySteps = SteadyLocalSteps(legacy, frameRate);

        float expected = phased.A.LocalHorizontalSpeed / frameRate;
        Assert.True(expected > 0f, "the local avatar should be walking");
        foreach (float step in phasedSteps)
            Assert.InRange(step, expected * 0.98f, expected * 1.02f);

        // The single-argument overload still renders a whole frame past every tick, which is the uneven cadence the
        // phase overload exists to remove.
        Assert.Contains(legacySteps, step => MathF.Abs(step - expected) > expected * 0.1f);
    }

    [Fact]
    public void Catch_up_cap_preserves_the_hosts_inclusive_tick_residual()
    {
        Rig rig = NewRig(phased: true);

        rig.Frame(12f * Tick);

        Assert.Equal(Tick, rig.CommandPhase);
        Assert.True(float.IsFinite(rig.A.LocalRenderState.Position.X));
        rig.Frame(0.01f);
        Assert.InRange(rig.CommandPhase, 0f, Tick);
        Assert.True(float.IsFinite(rig.A.LocalRenderState.Position.X));
    }

    [Fact]
    public void Phase_overload_reaches_only_the_local_avatar()
    {
        Rig phased = NewRig(phased: true);
        Rig legacy = NewRig(phased: false);
        phased.A.PresentationTrace!.Clear();
        legacy.A.PresentationTrace!.Clear();

        const float Dt = 1f / 100f;
        for (int i = 0; i < 100; i++)
        {
            phased.Frame(Dt);
            legacy.Frame(Dt);
            Assert.Equal(legacy.A.LatestServerTick, phased.A.LatestServerTick);
            Assert.Equal(legacy.A.RemoteRenderTick, phased.A.RemoteRenderTick);
        }

        IReadOnlyList<PresentationTrace.Row> phasedRows = phased.A.PresentationTrace!.Rows;
        IReadOnlyList<PresentationTrace.Row> legacyRows = legacy.A.PresentationTrace!.Rows;
        Assert.Equal(legacyRows.Count, phasedRows.Count);
        bool localDiffers = false;
        for (int i = 0; i < phasedRows.Count; i++)
        {
            PresentationTrace.Row p = phasedRows[i], l = legacyRows[i];
            Assert.Equal(l.T, p.T);
            Assert.Equal(l.Dt, p.Dt);
            Assert.Equal(l.RenderTime, p.RenderTime);
            Assert.Equal(l.InterpolationDelay, p.InterpolationDelay);
            Assert.Equal(l.SinceSnapshot, p.SinceSnapshot);
            Assert.Equal(l.SnapshotArrived, p.SnapshotArrived);
            Assert.Equal(l.IsLocal, p.IsLocal);
            Assert.Equal(l.EntityId, p.EntityId);
            if (p.IsLocal)
            {
                // Reconciliation measures prediction against authority, which presentation never touches.
                Assert.Equal(l.ReconcileError, p.ReconcileError);
                localDiffers |= l.Position != p.Position;
            }
            else
            {
                Assert.Equal(l.Position, p.Position);
                Assert.Equal(l.VerticalVelocity, p.VerticalVelocity);
                Assert.Equal(l.Held, p.Held);
            }
        }
        Assert.Contains(phasedRows, r => !r.IsLocal && r.EntityId == phased.B.LocalNetId);
        Assert.True(localDiffers, "the residual should reach the local avatar's presentation");
    }

    [Fact]
    public void Refused_phase_advances_no_clock_and_records_no_trace_row()
    {
        Rig rig = NewRig(phased: true);
        const float Dt = 1f / 100f;
        for (int i = 0; i < 50; i++) rig.Frame(Dt);

        PresentationTrace trace = rig.A.PresentationTrace!;
        int rows = trace.Count;
        double lastT = trace.Rows[rows - 1].T;
        double remoteRenderTick = rig.A.RemoteRenderTick;
        Vector3 local = rig.A.LocalRenderState.Position;

        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => rig.A.AdvancePresentation(Dt, float.NaN));
        Assert.Equal("commandPhaseSeconds", thrown.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.A.AdvancePresentation(Dt, 2f * Tick));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.A.AdvancePresentation(Dt, -Tick));

        Assert.Equal(rows, trace.Count);
        Assert.Equal(remoteRenderTick, rig.A.RemoteRenderTick);
        Assert.Equal(local, rig.A.LocalRenderState.Position);

        // The next valid frame's render clock moves on from where the last valid frame left it, by one frame.
        rig.Frame(Dt);
        Assert.Equal(lastT + Dt, trace.Rows[rows].T);
    }

    [Fact]
    public void Refused_send_input_applies_no_phase()
    {
        var rh = new RestartableHub();
        var config = new WorldServerConfig { TickSeconds = Tick, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(rh.ServerTransport, config, Flat, MoveTuning.Default);
        using var client = new WorldClient(rh.Connect, Flat, MoveTuning.Default,
            new WorldClientConfig
            {
                TickSeconds = Tick,
                DisconnectTimeoutSeconds = 0.3f,
                Reconnect = new ReconnectBackoff { InitialSeconds = 0.1f, Multiplier = 2f, MaxSeconds = 0.2f },
            });

        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(Tick); client.Poll(0.016f); }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        for (int i = 0; i < 30; i++)
        {
            client.SendInput(Right);
            server.Poll(); server.Tick(Tick);
            client.Poll(0.016f); client.AdvancePresentation(Tick, 0f);
        }

        // The server goes away. Once the client stops treating the session as live, every send is refused.
        rh.Restart();
        for (int i = 0; i < 40 && client.ConnectionState != WorldConnectionState.Reconnecting; i++)
        {
            client.Poll(0.05f); client.AdvancePresentation(0.05f);
        }
        Assert.Equal(WorldConnectionState.Reconnecting, client.ConnectionState);
        // Let the last walked segment finish and any correction settle, so the render sits on its target. These
        // frames accumulate whatever the overload, so the hold below is not an artifact of an earlier zero phase.
        for (int i = 0; i < 40; i++) { client.Poll(0.05f); client.AdvancePresentation(0.05f); }
        Vector3 settled = client.LocalRenderState.Position;

        // The last forward prediction was a walking step. Placing the clock on a zero phase would draw the avatar
        // back at that step's start. A refused send predicts nothing, so the frame accumulates and holds.
        Assert.Equal(-1, client.SendInput(Right));
        client.AdvancePresentation(1f / 144f, 0f);
        Assert.Equal(settled, client.LocalRenderState.Position);
        // A wrongly predicted extra step could hide at phase zero. Its endpoint must hold as well.
        client.AdvancePresentation(Tick);
        Assert.Equal(settled, client.LocalRenderState.Position);
    }
}
