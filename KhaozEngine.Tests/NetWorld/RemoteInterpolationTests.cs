using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Covers the <see cref="WorldClient"/> remote-smoothing drive: between the discrete (~tick-rate) replicated
/// snapshots, <see cref="WorldClient.AdvancePresentation"/> must interpolate remotes through
/// <c>ClientReplicationView.Interpolate</c> so a remote glides (render slightly in the past, one snapshot of
/// interpolation delay) instead of teleporting one snapshot-step per ingest. Default-on, opt-out via
/// <see cref="WorldClientConfig.InterpolateRemotes"/>; the local (predicted) avatar is untouched.
/// </summary>
public class RemoteInterpolationTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveCommand Right = new(new Vector2(1f, 0f), run: false, cameraYaw: 0f);   // +X
    static readonly MoveCommand Forward = new(new Vector2(0f, 1f), run: false, cameraYaw: 0f); // -Z

    sealed class Rig
    {
        public required WorldServer Server { get; init; }
        public required WorldServerConfig Config { get; init; }
        public required WorldClient A { get; init; }   // the observer under test
        public required WorldClient B { get; init; }   // the moving remote A watches
        public int BId => B.LocalNetId;
        public float Dt => Config.TickSeconds;

        /// <summary>One server tick. Polls A/B; optionally advances A's presentation by <paramref name="presentDt"/>.</summary>
        public void Tick(float presentDt, bool moveB = true, bool advanceA = true, MoveCommand? aCmd = null)
        {
            B.SendInput(moveB ? Right : MoveCommand.Idle);
            A.SendInput(aCmd ?? MoveCommand.Idle);
            Server.Poll();
            Server.Tick(Config.TickSeconds);
            A.Poll();
            B.Poll();
            if (advanceA) A.AdvancePresentation(presentDt);
        }

        public Vector3 Remote() => RemotePos(A, BId);
    }

    /// <summary>Connects A (observer) + B (remote), then runs a clean warm-up (one snapshot + one presentation step
    /// per tick) so the inter-snapshot interval estimate converges to the tick and B is moving steadily in +X.</summary>
    static Rig NewRig(bool interpolateRemotesOnA = true)
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds, InterpolateRemotes = interpolateRemotesOnA });
        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });
        var rig = new Rig { Server = server, Config = config, A = a, B = b };

        // Connect both, then warm: B walks +X every tick; A applies one snapshot + one presentation step per tick.
        for (int i = 0; i < 30; i++) rig.Tick(config.TickSeconds);
        Assert.True(a.Joined && b.Joined, "both clients should be joined after warm-up");
        Assert.True(b.LocalNetId > 0);
        Assert.Contains(a.Snapshot(), e => !e.IsLocal && e.Id.Value == b.LocalNetId);
        return rig;
    }

    static Vector3 RemotePos(WorldClient observer, int remoteNetId)
    {
        foreach (EntityRenderState e in observer.Snapshot())
            if (!e.IsLocal && e.Id.Value == remoteNetId) return e.Position;
        throw new Xunit.Sdk.XunitException($"remote {remoteNetId} not visible");
    }

    [Fact]
    public void Mid_interval_renders_between_the_two_snapshots_not_the_latest()
    {
        Rig rig = NewRig();

        // Tick once and read the remote BEFORE advancing: world holds the freshly applied 'current' = p0.
        // This exact value becomes the interpolation 'previous' after the NEXT snapshot.
        rig.Tick(rig.Dt, advanceA: false);
        Vector3 p0 = rig.Remote();

        // Next snapshot: previous = p0, current = p1, clock reset to 0 (read before advancing = raw current).
        rig.Tick(rig.Dt, advanceA: false);
        Vector3 p1Raw = rig.Remote();
        Assert.True(p1Raw.X > p0.X + 1e-3f, $"B should have advanced +X between snapshots ({p0.X} -> {p1Raw.X})");

        // Advance HALF an interval: the remote must render ~midway between p0 and p1, NOT snapped to the latest p1.
        rig.A.AdvancePresentation(0.5f * rig.Dt);
        Vector3 mid = rig.Remote();

        // Push well past one interval (alpha clamps to 1) to read the exact 'current' p1.
        rig.A.AdvancePresentation(4f * rig.Dt);
        Vector3 p1 = rig.Remote();
        Assert.Equal(p1Raw.X, p1.X, 3);   // clamped ramp lands exactly on current

        Assert.True(mid.X > p0.X + 1e-4f, $"mid should be past previous p0 ({p0.X}), got {mid.X}");
        Assert.True(mid.X < p1.X - 1e-4f, $"mid must NOT be the latest p1 ({p1.X}), got {mid.X} (pre-fix bug)");
        float frac = (mid.X - p0.X) / (p1.X - p0.X);
        Assert.InRange(frac, 0.40f, 0.60f);   // ~halfway, tolerating the interval estimate
    }

    [Fact]
    public void Ramp_is_monotonic_and_never_overshoots_current()
    {
        Rig rig = NewRig();

        rig.Tick(rig.Dt, advanceA: false);
        Vector3 p0 = rig.Remote();                 // becomes 'previous'
        rig.Tick(rig.Dt, advanceA: false);
        Vector3 p1 = rig.Remote();                 // raw 'current' (ramp target)
        Assert.True(p1.X > p0.X + 1e-3f);

        float prevX = p0.X;                         // the ramp starts from 'previous'
        float firstX = float.NaN;
        for (int i = 0; i < 12; i++)
        {
            rig.A.AdvancePresentation(0.15f * rig.Dt);   // 12 * 0.15 = 1.8 intervals -> reaches and holds at current
            float x = rig.Remote().X;
            if (i == 0) firstX = x;
            Assert.True(x >= prevX - 1e-4f, $"ramp went backwards: {prevX} -> {x}");
            Assert.True(x <= p1.X + 1e-4f, $"ramp overshot current p1 ({p1.X}): {x}");
            prevX = x;
        }
        // A genuine ramp starts near 'previous': the first sub-interval step must be well below current (snapping
        // straight to the latest snapshot - the pre-fix behaviour - would put it at p1 on the first step).
        Assert.True(firstX < p0.X + 0.6f * (p1.X - p0.X), $"ramp must start near previous, first sample {firstX} (p0 {p0.X}, p1 {p1.X})");
        Assert.Equal(p1.X, prevX, 3);   // ramp completes at current
    }

    [Fact]
    public void Late_snapshot_holds_at_current_without_extrapolating_then_resumes()
    {
        Rig rig = NewRig();

        rig.Tick(rig.Dt, advanceA: false);     // current -> p0 (becomes previous)
        rig.Tick(rig.Dt, advanceA: false);     // previous = p0, current = p1
        Vector3 p1 = rig.Remote();

        rig.A.AdvancePresentation(4f * rig.Dt); // overshoot the interval: alpha clamps to 1 -> at p1
        Vector3 atFull = rig.Remote();
        Assert.Equal(p1.X, atFull.X, 3);

        // No new snapshot: advancing further must HOLD at current, never extrapolate past it.
        rig.A.AdvancePresentation(4f * rig.Dt);
        Vector3 held = rig.Remote();
        Assert.Equal(atFull.X, held.X, 4);
        Assert.True(held.X <= p1.X + 1e-4f, "held position must not extrapolate beyond current");

        // The next snapshot resumes interpolation forward from the held (current) value.
        rig.Tick(rig.Dt, advanceA: false);     // previous = p1, current = p2
        Vector3 p2 = rig.Remote();
        rig.A.AdvancePresentation(0.5f * rig.Dt);
        Vector3 resumed = rig.Remote();
        Assert.True(resumed.X > held.X + 1e-4f, "should resume moving forward from the held value");
        Assert.True(resumed.X < p2.X - 1e-4f, "should still be interpolating, not snapped to the new current");
    }

    [Fact]
    public void Local_player_render_position_is_not_perturbed_by_remote_interpolation()
    {
        Rig rig = NewRig();

        // Drive the local avatar (A) forward while remote interpolation is running.
        for (int i = 0; i < 8; i++) rig.Tick(rig.Dt, aCmd: Forward);

        EntityRenderState local = rig.A.Snapshot().Single(e => e.IsLocal);
        // The local entry must track the predicted/reconciled state, NOT the interpolated replicated position.
        Assert.Equal(rig.A.LocalRenderState.Position.X, local.Position.X, 5);
        Assert.Equal(rig.A.LocalRenderState.Position.Z, local.Position.Z, 5);
        Assert.True(local.Position.Z < -0.1f, "local avatar should have moved forward (-Z)");
    }

    [Fact]
    public void InterpolateRemotes_false_returns_the_raw_latest_position()
    {
        Rig rig = NewRig(interpolateRemotesOnA: false);

        rig.Tick(rig.Dt, advanceA: false);
        Vector3 p0 = rig.Remote();
        rig.Tick(rig.Dt, advanceA: false);
        Vector3 latest = rig.Remote();              // raw current
        Assert.True(latest.X > p0.X + 1e-3f);

        // With the opt-out, advancing presentation does NOT interpolate: the remote stays at the latest snapshot.
        rig.A.AdvancePresentation(0.5f * rig.Dt);
        Vector3 afterAdvance = rig.Remote();
        Assert.Equal(latest.X, afterAdvance.X, 5);  // no smoothing, no interpolation delay
    }

    [Fact]
    public void First_snapshot_for_a_new_remote_has_no_previous_and_does_not_throw()
    {
        // A remote that just appeared has only a 'current' buffer (no 'previous'); Interpolate must skip it and
        // render it at its spawn, never throwing or producing NaN.
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(config.TickSeconds); a.Poll(); a.AdvancePresentation(config.TickSeconds); }
        Assert.True(a.Joined);

        // B joins late: A's first snapshot containing B carries only 'current'.
        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });

        bool sawB = false;
        for (int i = 0; i < 12; i++)
        {
            b.SendInput(Right);
            server.Poll();
            server.Tick(config.TickSeconds);
            a.Poll();
            b.Poll();
            a.AdvancePresentation(config.TickSeconds);   // must not throw even on B's brand-new (no-previous) frame
            foreach (EntityRenderState e in a.Snapshot())
            {
                if (e.IsLocal || e.Id.Value != b.LocalNetId) continue;
                sawB = true;
                Assert.True(float.IsFinite(e.Position.X) && float.IsFinite(e.Position.Y) && float.IsFinite(e.Position.Z),
                    $"new remote position must be finite, got {e.Position}");
            }
        }
        Assert.True(sawB, "A never saw the late-joining remote B");
    }
}
