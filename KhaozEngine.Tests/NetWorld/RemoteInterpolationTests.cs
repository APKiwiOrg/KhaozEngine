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
/// snapshots, <see cref="WorldClient.AdvancePresentation"/> renders remotes on a FIXED delay
/// (<see cref="WorldClientConfig.InterpolationDelayTicks"/>) via <c>ClientReplicationView.InterpolateAt</c>, so a
/// remote glides in the past (behind the newest snapshot) instead of teleporting one snapshot-step per ingest.
/// Default-on, opt-out via <see cref="WorldClientConfig.InterpolateRemotes"/>; the local (predicted) avatar is
/// untouched. Per-frame smoothness across a non-integer render:tick ratio is asserted in <see cref="PresentationJitterTests"/>.
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
    /// per tick) so the fixed-delay interpolation buffer fills with distinctly-stamped samples and B is moving
    /// steadily in +X.</summary>
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
    public void Remote_renders_behind_the_latest_snapshot_not_snapped_to_it()
    {
        Rig rig = NewRig();

        // One more snapshot without presenting: the world holds B's raw latest position (Apply wrote it; InterpolateAt
        // has not run this frame yet).
        rig.Tick(rig.Dt, advanceA: false);
        float rawLatest = rig.Remote().X;

        // Present the frame: on the fixed delay the remote renders STRICTLY BEHIND the raw latest (in the past),
        // never snapped onto the newest snapshot.
        rig.A.AdvancePresentation(rig.Dt);
        float rendered = rig.Remote().X;
        Assert.True(rendered < rawLatest - 1e-4f,
            $"remote should render on a delay, behind the latest snapshot; rendered {rendered} vs raw {rawLatest}");
    }

    [Fact]
    public void Remote_glides_forward_between_snapshots_no_hold_no_overshoot()
    {
        Rig rig = NewRig();

        // One fresh snapshot + present, so the render sits on the fixed delay with a full buffer behind it.
        rig.Tick(rig.Dt, advanceA: false);
        float rawLatest = rig.Remote().X;
        rig.A.AdvancePresentation(rig.Dt);

        // Sub-tick presentation steps with NO new snapshot: the remote must glide forward monotonically (each step
        // advances it, none is a hold ~0), and it must never overshoot the latest snapshot it is interpolating toward.
        // Four 0.2-tick steps keep the render time inside the buffered bracket (~1 tick behind the newest sample after
        // the present above), so every step lands on a genuine interpolation, not the past-the-buffer hold.
        float prevX = rig.Remote().X;
        for (int i = 0; i < 4; i++)
        {
            rig.A.AdvancePresentation(0.2f * rig.Dt);
            float x = rig.Remote().X;
            Assert.True(x > prevX + 1e-5f, $"remote must glide forward (no hold): {prevX} -> {x}");
            Assert.True(x <= rawLatest + 1e-4f, $"remote must not overshoot the latest snapshot ({rawLatest}): {x}");
            prevX = x;
        }
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
