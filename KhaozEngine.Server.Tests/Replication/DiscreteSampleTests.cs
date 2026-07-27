using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

// E2 (coherent remote timeline): a component registered discreteSample is fixed-delay NEAREST-sampled - time-buffered
// like a lerp component, but InterpolateAt writes the sample nearest the render time verbatim (no blend). This is what
// lets a remote's discrete MovementState flags (grounded / swim / the quantized ClimbRate) ride the SAME delayed render
// timeline as the interpolated position, instead of being read live at the newest snapshot ~InterpolationDelayTicks
// ahead of the drawn feet - the flag/position skew that would bracket every climb with a bob-on-flat transient.
public class DiscreteSampleTests
{
    private struct Pos : IComponent { public float X; }
    private struct Flag : IComponent { public int V; }   // a discrete quantity (mimics ClimbRateQ): must NOT be blended

    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(1,
            write: (p, bw) => bw.Write(p.X),
            read: br => new Pos { X = br.ReadSingle() },
            lerp: (a, b, t) => new Pos { X = a.X + (b.X - a.X) * t });
        r.Register<Flag>(2,
            write: (f, bw) => bw.Write(f.V),
            read: br => new Flag { V = br.ReadInt32() },
            discreteSample: true);
        return r;
    }

    private static void Push(ClientReplicationView view, World client, World server, Entity s, float x, int flag, double t)
    {
        server.Set(s, new Pos { X = x });
        server.Set(s, new Flag { V = flag });
        view.Apply(client, SnapshotWriter.Write(server, NewRegistry()));
        view.RecordInterpolationSample(t);
    }

    [Fact]
    public void Register_RejectsLerpAndDiscreteTogether()
    {
        var r = new ReplicationRegistry();
        Assert.Throws<ArgumentException>(() => r.Register<Flag>(2,
            write: (f, bw) => bw.Write(f.V),
            read: br => new Flag { V = br.ReadInt32() },
            lerp: (a, b, t) => a,
            discreteSample: true));
    }

    [Fact]
    public void DiscreteComponent_PicksNearestSample_TieToLower_NeverBlends()
    {
        var registry = NewRegistry();
        var client = new World();
        var view = new ClientReplicationView(registry);
        var server = new World();
        Entity s = server.Spawn(); server.Set(s, new NetId(7));

        Push(view, client, server, s, 0f, flag: 0, t: 1.00);
        Push(view, client, server, s, 10f, flag: 5, t: 1.10);
        Assert.True(view.TryGetEntity(7, out Entity c));

        // Nearest the lower bracket -> its value verbatim; nearest the upper -> its value. A value strictly between the
        // two buffered ints (an interpolation) is NEVER produced.
        view.InterpolateAt(client, 1.04);
        Assert.Equal(0, client.Get<Flag>(c).V);
        view.InterpolateAt(client, 1.06);
        Assert.Equal(5, client.Get<Flag>(c).V);
        view.InterpolateAt(client, 1.05);   // exact tie -> lower (mirrors the lerp clamp's <=)
        Assert.Equal(0, client.Get<Flag>(c).V);
    }

    [Fact]
    public void DiscreteFlag_RidesSameDelayedTimelineAsPosition_NotNewestLive()
    {
        // Position advances toward "the riser" at X=2; the flag flips at the SAME sample (t=1.2, X=2). Read live at the
        // newest snapshot the flag would be true ~2 ticks before the DELAYED position reaches the riser; nearest-sampled
        // at the render time it flips coherently with the drawn position.
        var registry = NewRegistry();
        var client = new World();
        var view = new ClientReplicationView(registry);
        var server = new World();
        Entity s = server.Spawn(); server.Set(s, new NetId(7));

        Push(view, client, server, s, 0f, flag: 0, t: 1.00);
        Push(view, client, server, s, 1f, flag: 0, t: 1.10);
        Push(view, client, server, s, 2f, flag: 1, t: 1.20);   // the riser: position 2, flag flips true
        Push(view, client, server, s, 3f, flag: 1, t: 1.30);   // newest sample (flag live = true)
        Assert.True(view.TryGetEntity(7, out Entity c));

        // The render clock only advances (InterpolateAt prunes below it), so probe in increasing renderTime order.
        // renderTime 1.05: the DELAYED position is only 0.5 (well before the riser). The nearest-sampled flag is 0, NOT
        // the newest-live 1 - that is the whole point (no ~2-tick-early flip).
        view.InterpolateAt(client, 1.05);
        Assert.Equal(0.5f, client.Get<Pos>(c).X, 4);
        Assert.Equal(0, client.Get<Flag>(c).V);

        // The flip lands within ONE tick of the position reaching the riser (1.15 tie -> still 0; 1.16 -> 1).
        view.InterpolateAt(client, 1.15);
        Assert.Equal(0, client.Get<Flag>(c).V);
        view.InterpolateAt(client, 1.16);
        Assert.Equal(1, client.Get<Flag>(c).V);

        // renderTime 1.20: the delayed position reaches the riser (X=2) and the flag is true at the same time.
        view.InterpolateAt(client, 1.20);
        Assert.Equal(2f, client.Get<Pos>(c).X, 4);
        Assert.Equal(1, client.Get<Flag>(c).V);
    }

    [Fact]
    public void SnapInterpolationToNewest_FlushesDiscreteBuffer()
    {
        // A teleport flush drops the pre-jump samples for ALL of an entity's fixed-delay components, discrete included,
        // so the post-teleport flag renders immediately (no stale pre-teleport flag lingering across the cut).
        var registry = NewRegistry();
        var client = new World();
        var view = new ClientReplicationView(registry);
        var server = new World();
        Entity s = server.Spawn(); server.Set(s, new NetId(7));

        Push(view, client, server, s, 0f, flag: 0, t: 1.00);
        Push(view, client, server, s, 1f, flag: 0, t: 1.10);
        Push(view, client, server, s, 500f, flag: 9, t: 1.20);   // teleport: far position + new flag

        view.SnapInterpolationToNewest(7);
        view.InterpolateAt(client, 1.11);   // would have bracketed inside the pre-jump samples
        Assert.True(view.TryGetEntity(7, out Entity c));
        Assert.Equal(500f, client.Get<Pos>(c).X, 4);   // position snapped
        Assert.Equal(9, client.Get<Flag>(c).V);         // discrete flag snapped too (not the stale pre-teleport 0)
    }

    // ---- End-to-end with the real MoveProtocol registry + MovementState over a stair entry ----
    [Fact]
    public void MovementState_ClimbSignal_IsCoherentWithInterpolatedPosition()
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var client = new World();
        var view = new ClientReplicationView(registry);
        var server = new World();
        Entity s = server.Spawn(); server.Set(s, new NetId(42));

        void PushMs(float z, sbyte climbQ, bool grounded, double t)
        {
            server.Set(s, ReplicatedPosition.FromWorld(new Vector3(0f, 1f, z), WorldFrame.Origin));
            server.Set(s, new MovementState { Grounded = grounded, ClimbRateQ = climbQ });
            view.Apply(client, SnapshotWriter.Write(server, registry));
            view.RecordInterpolationSample(t);
        }

        // Walk into a stair: flat (climbQ 0) until Z=-1 (the first riser), then climbing (ClimbRateQ = 70 = 3.5 m/s).
        PushMs(0f, 0, true, 1.00);
        PushMs(-0.5f, 0, true, 1.10);
        PushMs(-1f, 70, true, 1.20);   // reaches the riser: climb signal turns on
        PushMs(-1.5f, 70, true, 1.30);
        Assert.True(view.TryGetEntity(42, out Entity c));

        // At a delayed render time where the interpolated position is still on the flat approach, the climb signal is
        // still 0 - it does NOT switch on ~2 ticks early (which is what reading MovementState live at the newest
        // snapshot did). The decoded rate is exactly 0 (a discrete value, never an interpolated in-between).
        view.InterpolateAt(client, 1.05);
        Assert.True(client.Get<ReplicatedPosition>(c).Value.Z > -1f + 1e-3f, "delayed position should still be on the approach");
        Assert.Equal(0, client.Get<MovementState>(c).ClimbRateQ);

        // When the delayed position reaches the riser, the climb signal is on and decodes to exactly the sim's rate.
        view.InterpolateAt(client, 1.20);
        Assert.Equal(-1f, client.Get<ReplicatedPosition>(c).Value.Z, 3);
        Assert.Equal((sbyte)70, client.Get<MovementState>(c).ClimbRateQ);
        Assert.Equal(3.5f, MovementState.DecodeClimbRate(client.Get<MovementState>(c).ClimbRateQ), 3);
    }
}
