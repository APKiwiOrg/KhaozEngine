using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// The fixed-delay snapshot-interpolation buffer on <see cref="ClientReplicationView"/>: instead of the
/// estimate-and-ramp-to-latest scheme (<see cref="ClientReplicationView.Interpolate"/>), a timestamped history of
/// samples plus <see cref="ClientReplicationView.InterpolateAt"/> renders at an arbitrary render time by lerping the
/// two bracketing samples by their TRUE timestamps. This decouples presentation from both the tick cadence and the
/// render fps, so there are no hold frames and no catch-up snaps at a non-integer render:tick ratio.
/// </summary>
public class ClientReplicationBufferTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(
            typeId: 1,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() },
            lerp: (a, b, t) => new Pos { X = a.X + (b.X - a.X) * t, Y = a.Y + (b.Y - a.Y) * t });
        return r;
    }

    // Applies a one-entity snapshot at position (x,y) and records a buffer sample stamped at time t.
    private static void Push(ClientReplicationView view, World client, World server, Entity s, float x, float y, double t)
    {
        server.Set(s, new Pos { X = x, Y = y });
        view.Apply(client, SnapshotWriter.Write(server, NewRegistry()));
        view.RecordInterpolationSample(t);
    }

    [Fact]
    public void InterpolateAt_lerps_between_bracketing_samples_by_true_time_fraction()
    {
        var registry = NewRegistry();
        var client = new World();
        var view = new ClientReplicationView(registry);
        var server = new World();
        Entity s = server.Spawn(); server.Set(s, new NetId(7));

        Push(view, client, server, s, 0f, 0f, t: 1.00);
        Push(view, client, server, s, 10f, 20f, t: 1.10);

        // renderTime 1.075 sits 75% of the way from the t=1.00 sample to the t=1.10 sample.
        view.InterpolateAt(client, 1.075);

        Assert.True(view.TryGetEntity(7, out Entity c));
        Assert.Equal(7.5f, client.Get<Pos>(c).X, 4);
        Assert.Equal(15f, client.Get<Pos>(c).Y, 4);
    }

    [Fact]
    public void InterpolateAt_holds_at_the_newest_sample_when_renderTime_is_beyond_it()
    {
        // A stalled snapshot stream (renderTime advanced past the newest sample) HOLDS at the newest value; it never
        // extrapolates. This is the only remaining "hold", and it happens solely on genuine starvation, not per tick.
        var registry = NewRegistry();
        var client = new World();
        var view = new ClientReplicationView(registry);
        var server = new World();
        Entity s = server.Spawn(); server.Set(s, new NetId(7));

        Push(view, client, server, s, 0f, 0f, t: 1.00);
        Push(view, client, server, s, 10f, 20f, t: 1.10);

        view.InterpolateAt(client, 5.0);   // long past the newest sample

        Assert.True(view.TryGetEntity(7, out Entity c));
        Assert.Equal(10f, client.Get<Pos>(c).X, 4);
        Assert.Equal(20f, client.Get<Pos>(c).Y, 4);
    }

    [Fact]
    public void InterpolateAt_clamps_to_the_oldest_sample_before_the_buffer_starts()
    {
        // During warm-up renderTime (latest - delay) can precede the oldest buffered sample; render the oldest value
        // rather than NaN or extrapolate backward.
        var registry = NewRegistry();
        var client = new World();
        var view = new ClientReplicationView(registry);
        var server = new World();
        Entity s = server.Spawn(); server.Set(s, new NetId(7));

        Push(view, client, server, s, 3f, 4f, t: 1.00);
        Push(view, client, server, s, 10f, 20f, t: 1.10);

        view.InterpolateAt(client, 0.5);   // before the oldest sample

        Assert.True(view.TryGetEntity(7, out Entity c));
        Assert.Equal(3f, client.Get<Pos>(c).X, 4);
        Assert.Equal(4f, client.Get<Pos>(c).Y, 4);
    }

    private struct Vel : IComponent { public float X; public float Y; }

    private static ReplicationRegistry TwoInterpolatableRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(1,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() },
            lerp: (a, b, t) => new Pos { X = a.X + (b.X - a.X) * t, Y = a.Y + (b.Y - a.Y) * t });
        r.Register<Vel>(2,
            write: (v, bw) => { bw.Write(v.X); bw.Write(v.Y); },
            read: br => new Vel { X = br.ReadSingle(), Y = br.ReadSingle() },
            lerp: (a, b, t) => new Vel { X = a.X + (b.X - a.X) * t, Y = a.Y + (b.Y - a.Y) * t });
        return r;
    }

    [Fact]
    public void InterpolateAt_does_not_resurrect_a_delta_removed_interpolatable_component()
    {
        // A delta that removes an interpolatable component from a still-alive entity must purge that component's
        // sample history too - otherwise InterpolateAt keeps lerping the stale samples and world.Set re-adds the
        // component the server removed, every frame (the resurrection bug).
        var registry = TwoInterpolatableRegistry();
        var server = new World();
        Entity s = server.Spawn(); server.Set(s, new NetId(7));
        server.Set(s, new Pos { X = 1, Y = 2 });
        server.Set(s, new Vel { X = 5, Y = 6 });

        var repl = new ServerReplicator(registry);
        int seq1 = repl.Capture(server);
        byte[] full = repl.WriteFor(slot: 0);          // baseline -1 full delta

        var client = new World();
        var view = new ClientReplicationView(registry);
        view.ApplyDelta(client, full);
        view.RecordInterpolationSample(1.0);
        Assert.True(view.TryGetEntity(7, out Entity c));
        Assert.True(client.Has<Vel>(c));               // present initially

        // Advance slot 0's baseline to seq1 so the next WriteFor diffs against it (a per-component removal delta,
        // not a fresh full snapshot which would not remove an omitted component).
        repl.Acknowledge(slot: 0, seq: seq1);

        // Server removes Vel from the (still-alive) entity; capture + apply the delta.
        server.Remove<Vel>(s);
        repl.Capture(server);
        byte[] delta = repl.WriteFor(slot: 0);
        view.ApplyDelta(client, delta);
        view.RecordInterpolationSample(1.1);
        Assert.False(client.Has<Vel>(c));              // the delta removed it

        // The bug: InterpolateAt re-Sets Vel from the stale (7, Vel) sample history. It must STAY removed.
        view.InterpolateAt(client, 1.05);
        Assert.False(client.Has<Vel>(c));
    }

    [Fact]
    public void InterpolateAt_a_single_sample_renders_that_sample()
    {
        // A remote seen in exactly one snapshot has no bracket; render it at that one value, never throwing / NaN.
        var registry = NewRegistry();
        var client = new World();
        var view = new ClientReplicationView(registry);
        var server = new World();
        Entity s = server.Spawn(); server.Set(s, new NetId(7));

        Push(view, client, server, s, 2f, 9f, t: 1.00);

        view.InterpolateAt(client, 1.00);

        Assert.True(view.TryGetEntity(7, out Entity c));
        Assert.Equal(2f, client.Get<Pos>(c).X, 4);
        Assert.Equal(9f, client.Get<Pos>(c).Y, 4);
    }
}
