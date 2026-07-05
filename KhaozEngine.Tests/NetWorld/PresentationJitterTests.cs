using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Acceptance for the presentation-layer jitter fix, run headless on loopback (zero network jitter). At both an
/// integer (60 fps vs 30 Hz) and a non-integer (~178 fps vs 30 Hz) render:tick ratio the per-frame RENDERED
/// position of a remote and of the local player must be smooth: no hold frames (near-zero steps), no catch-up snaps
/// (multiple-of-norm steps), no direction reversals. This is exactly the signature the throwaway Ruinborne position
/// trace measured (docs/diagnostics/presentation-trace.md); asserted here so it can never regress.
/// </summary>
public class PresentationJitterTests
{
    private readonly ITestOutputHelper _out;
    public PresentationJitterTests(ITestOutputHelper output) => _out = output;

    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveCommand Right = new(new Vector2(1f, 0f), run: false, cameraYaw: 0f);   // +X

    sealed class Loop
    {
        public required WorldServer Server { get; init; }
        public required WorldServerConfig Config { get; init; }
        public required WorldClient A { get; init; }   // observer whose render we trace
        public required WorldClient B { get; init; }   // remote mover (always +X)
        // Client input (Predict) and the server tick (which produces the snapshot the client reconciles against) run
        // on SEPARATE accumulators with a half-tick phase offset. This reproduces the real loop, where a snapshot
        // arrives out of phase with the client's local tick, so reconcile lands mid inter-tick (~frac 0.5) - the phase
        // that makes the old reconcile-collapse leak its per-tick velocity dip. A shared accumulator would phase-lock
        // Predict and Reconcile and hide the sawtooth.
        float clientAccum = 0.5f * (1f / 30f);
        float serverAccum;
        public float Tick => Config.TickSeconds;

        // One render frame of dt seconds. Poll + AdvancePresentation run every frame (so at a high render fps most
        // frames fire no tick). Mirrors the real consumer loop (KhaozEngine.Showcase/RoomNet): tick-gated SendInput,
        // Poll, AdvancePresentation - but with the server tick out of phase with the client's input tick.
        public void Frame(float dt, MoveCommand aCmd)
        {
            clientAccum += dt;
            while (clientAccum >= Tick) { clientAccum -= Tick; B.SendInput(Right); A.SendInput(aCmd); }

            serverAccum += dt;
            while (serverAccum >= Tick) { serverAccum -= Tick; Server.Poll(); Server.Tick(Tick); }

            A.Poll(dt); B.Poll(dt);
            A.AdvancePresentation(dt); B.AdvancePresentation(dt);
        }
    }

    static Loop NewLoop()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        var loop = new Loop { Server = server, Config = config, A = a, B = b };
        for (int i = 0; i < 120; i++) loop.Frame(config.TickSeconds, MoveCommand.Idle);   // join + fill the interpolation buffer
        Assert.True(a.Joined && b.Joined, "both clients should be joined after warm-up");
        Assert.True(b.LocalNetId > 0);
        return loop;
    }

    static float RemoteX(WorldClient obs, long id)
    {
        foreach (EntityRenderState e in obs.Snapshot())
            if (!e.IsLocal && e.Id.Value == id) return e.Position.X;
        throw new Xunit.Sdk.XunitException("remote not visible");
    }

    readonly record struct Trace(int Frames, int Holds, int Snaps, int Reversals, float MedStep, float MaxStep, float MinStep)
    {
        public override string ToString() =>
            $"frames={Frames} holds={Holds} snaps={Snaps} reversals={Reversals} " +
            $"med={MedStep:F5} max={MaxStep:F5} min={MinStep:F5} (max/med={MaxStep / MedStep:F2} min/med={MinStep / MedStep:F2})";
    }

    // Characterise a 1D rendered-position stream by its per-frame step distribution. A hold is a near-zero step
    // (interpolation stalled); a snap is a step several times the norm (catch-up); a reversal is a sign flip.
    static Trace Measure(List<float> pos)
    {
        var d = new List<float>();
        for (int i = 1; i < pos.Count; i++) d.Add(pos[i] - pos[i - 1]);
        List<float> mag = d.Select(MathF.Abs).ToList();
        float med = mag.OrderBy(x => x).ElementAt(mag.Count / 2);
        int holds = mag.Count(m => m < 0.35f * med);
        int snaps = mag.Count(m => m > 2.5f * med);
        int rev = 0;
        for (int i = 1; i < d.Count; i++)
            if (d[i - 1] * d[i] < 0 && mag[i - 1] > 0.2f * med && mag[i] > 0.2f * med) rev++;
        return new Trace(mag.Count, holds, snaps, rev, med, mag.Max(), mag.Min());
    }

    [Theory]
    [InlineData(2.0f)]     // integer ratio: 60 fps against a 30 Hz tick
    [InlineData(5.9f)]     // non-integer ratio: ~177 fps against a 30 Hz tick (the uncapped worst case)
    public void Remote_render_has_no_holds_or_catch_up_snaps(float ratio)
    {
        Loop loop = NewLoop();
        float dt = loop.Tick / ratio;
        int frames = (int)(4f / dt);
        var xs = new List<float>();
        for (int i = 0; i < frames; i++) { loop.Frame(dt, MoveCommand.Idle); xs.Add(RemoteX(loop.A, loop.B.LocalNetId)); }
        Trace t = Measure(xs.Skip(xs.Count / 4).ToList());   // drop this run's warm-up quarter
        _out.WriteLine($"remote ratio {ratio}: {t}");

        Assert.True(t.MedStep > 1e-4f, $"remote not gliding: {t}");
        Assert.Equal(0, t.Reversals);
        Assert.True(t.Holds == 0, $"remote hold frames at ratio {ratio}: {t}");
        Assert.True(t.Snaps == 0, $"remote catch-up snaps at ratio {ratio}: {t}");
        // Fixed-delay interpolation over uniformly-spaced snapshots is a straight line: steps are tightly uniform.
        Assert.True(t.MaxStep < 1.6f * t.MedStep, $"remote step not uniform at ratio {ratio}: {t}");
    }

    [Theory]
    [InlineData(2.0f)]
    [InlineData(5.9f)]
    public void Local_render_has_no_tick_sawtooth(float ratio)
    {
        Loop loop = NewLoop();
        float dt = loop.Tick / ratio;
        int frames = (int)(4f / dt);
        var xs = new List<float>();
        for (int i = 0; i < frames; i++) { loop.Frame(dt, Right); xs.Add(loop.A.LocalRenderState.Position.X); }
        Trace t = Measure(xs.Skip(xs.Count / 4).ToList());
        _out.WriteLine($"local ratio {ratio}: {t}");

        Assert.True(t.MedStep > 1e-4f, $"local not moving: {t}");
        Assert.Equal(0, t.Reversals);
        // The reported artifact was per-tick SHORT frames (the reconcile-collapse velocity dip). It must be gone: no
        // near-zero holds, and no big catch-up snaps.
        Assert.True(t.Holds == 0, $"local 30 Hz short-frame sawtooth at ratio {ratio}: {t}");
        Assert.True(t.Snaps == 0, $"local catch-up snaps at ratio {ratio}: {t}");
    }

    // Decel-to-stop shake: warm up moving (walk or sprint), zero the command, and trace the local rendered X across the
    // stop at both an integer (60 fps vs 30 Hz) and a non-integer (~178 fps) render:tick ratio. When the local player
    // stops, it stops INSTANTLY in its own prediction, but the authority is an input-RTT behind - so for a tick or two
    // the basis the client reconciles against dips BACKWARD (the server is still applying the pre-stop moves) and then
    // catches up. Pre-fix, the inter-tick lerp chased that dip and the avatar sharply reversed ~55% of a tick (the
    // reported back-and-forth shake); the fix (C1 inter-tick across the rebase + critically-damped offset) turns it into
    // a smooth sub-dead-zone sag. A client running ahead over a genuine authority dip cannot be bit-perfectly monotone
    // without stranding a real backward misprediction, so the guarantee is a bounded, imperceptible residual, not zero.
    [Theory]
    [InlineData(2.0f, false)]
    [InlineData(5.9f, false)]
    [InlineData(2.0f, true)]
    [InlineData(5.9f, true)]
    public void Local_render_decel_to_stop_does_not_shake(float ratio, bool run)
    {
        Loop loop = NewLoop();
        float dt = loop.Tick / ratio;
        MoveCommand move = new(new Vector2(1f, 0f), run, cameraYaw: 0f);
        int moveFrames = (int)(2f / dt);   // 2 s of steady motion to reach a clean streaming state
        int stopFrames = (int)(1.5f / dt); // 1.5 s to let the stop fully settle

        for (int i = 0; i < moveFrames; i++) loop.Frame(dt, move);
        var xs = new List<float>();
        for (int i = 0; i < stopFrames; i++) { loop.Frame(dt, MoveCommand.Idle); xs.Add(loop.A.LocalRenderState.Position.X); }

        // The visible "shake" is the peak BACKWARD EXCURSION: how far the avatar ever dips below its furthest-forward
        // point while it settles, as a fraction of one pre-stop tick of travel (frame-rate and speed independent).
        float runMax = float.NegativeInfinity, excursion = 0f;
        foreach (float x in xs) { if (x > runMax) runMax = x; excursion = MathF.Max(excursion, runMax - x); }
        float tickStep = (run ? MoveTuning.Default.RunSpeed : MoveTuning.Default.WalkSpeed) * loop.Tick;
        _out.WriteLine($"decel ratio {ratio} run {run}: backExcursion={excursion:F5} m ({excursion / tickStep:P0} of a tick) finalX={xs[^1]:F4}");

        // Pre-fix the reversal was ~55% of a tick; the fix keeps the residual sag under 20% (in practice ~10-13%, all
        // within the 0.03 m CorrectionDeadZone). And the stop lands exactly on the authoritative position.
        Assert.True(excursion < 0.20f * tickStep,
            $"local avatar shakes on stop: backward excursion {excursion:F5} m is {excursion / tickStep:P0} of one tick at ratio {ratio} run {run}");
        Assert.Equal(run ? 12f : 6f, xs[^1], 3);
    }
}
