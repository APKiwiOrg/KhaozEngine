using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

public class InterpolationSegmentTests
{
    private const ushort PositionType = 1;
    private const long NetIdValue = 7;

    [Fact]
    public void Segment_matches_the_drawn_bracket_after_pruning_and_replacing_an_arrival()
    {
        var rig = new Rig();
        rig.Push(0, 0, 10);
        rig.Push(1, 4, 11);
        rig.Push(2, 6, 12);
        rig.Push(2, 8, 13);
        rig.Push(3, 20, 14);

        rig.Present(1.5);
        Assert.Equal(6f, rig.DrawnX);
        rig.AssertSegment(4, 8, 11, 13);

        rig.Present(2.25);
        Assert.Equal(11f, rig.DrawnX);
        rig.AssertSegment(8, 20, 13, 14);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void Clamp_or_hold_does_not_report_a_segment(double renderTime)
    {
        var rig = new Rig();
        rig.Push(1, 4, 11);
        rig.Push(2, 8, 12);
        rig.Present(renderTime);
        Assert.False(rig.TryGetSegment());
    }

    [Fact]
    public void Teleport_leaves_one_sample_and_its_tick_starts_the_next_segment()
    {
        var rig = new Rig();
        rig.Push(1, 4, 11);
        rig.Push(2, 8, 12);
        rig.Present(1.5);
        Assert.True(rig.TryGetSegment());

        rig.Push(3, 100, 13);
        rig.View.SnapInterpolationToNewest(NetIdValue);
        rig.Present(3);
        Assert.Equal(100f, rig.DrawnX);
        Assert.False(rig.TryGetSegment());

        rig.Push(4, 104, 14);
        rig.Present(3.5);
        rig.AssertSegment(100, 104, 13, 14);
    }

    [Fact]
    public void Legacy_recording_preserves_the_bracket_without_a_server_tick()
    {
        var rig = new Rig();
        rig.Push(1, 4);
        rig.Push(2, 8, 12);

        rig.Present(1.5);
        rig.AssertSegment(4, 8, null, 12);
    }

    private readonly record struct Position(float X) : IComponent;

    private sealed class Rig
    {
        private readonly ReplicationRegistry registry = new();
        private readonly World server = new();
        private readonly World client = new();
        private readonly Entity serverEntity;

        public Rig()
        {
            registry.Register<Position>(PositionType, (p, writer) => writer.Write(p.X),
                reader => new Position(reader.ReadSingle()),
                (a, b, fraction) => new Position(a.X + (b.X - a.X) * fraction));
            View = new ClientReplicationView(registry);
            serverEntity = server.Spawn();
            server.Set(serverEntity, new NetId(NetIdValue));
        }

        public ClientReplicationView View { get; }

        public float DrawnX
        {
            get
            {
                Assert.True(View.TryGetEntity(NetIdValue, out Entity entity));
                return client.Get<Position>(entity).X;
            }
        }

        public void Push(double stamp, float x, long? tick = null)
        {
            server.Set(serverEntity, new Position(x));
            View.Apply(client, SnapshotWriter.Write(server, registry));
            if (tick.HasValue) View.RecordInterpolationSample(stamp, excludeNetId: null, serverTick: tick);
            else View.RecordInterpolationSample(stamp);
        }

        public void Present(double time) => View.InterpolateAt(client, time);

        public bool TryGetSegment() =>
            View.TryGetInterpolationSegment<Position>(NetIdValue, PositionType, out _, out _, out _, out _);

        public void AssertSegment(float fromX, float toX, long? fromTick, long? toTick)
        {
            Assert.True(View.TryGetInterpolationSegment<Position>(NetIdValue, PositionType,
                out Position from, out Position to, out long? firstTick, out long? lastTick));
            Assert.Equal(fromX, from.X);
            Assert.Equal(toX, to.X);
            Assert.Equal(fromTick, firstTick);
            Assert.Equal(toTick, lastTick);
        }
    }
}
