using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Round-trips a 64-bit <see cref="NetId"/> through every wire path (full snapshot, AoI delta, persist blob, and the
/// per-client frame header) at boundary values: 0, <see cref="int.MaxValue"/>, a value past 2^32 (would have
/// overflowed the old 32-bit id), and one with the top node bits set (a negative signed long). A pre-10.0.0 build
/// truncated anything past 2^31; these confirm the widened path carries the full 64-bit value intact.
/// </summary>
public class NetIdWireBoundaryTests
{
    // 0; int.MaxValue; > 2^32; high-node-bits (node 0xABCD in the top 16 bits -> a negative signed long).
    public static IEnumerable<object[]> Boundaries => new[]
    {
        new object[] { 0L },
        new object[] { 1L },
        new object[] { (long)int.MaxValue },
        new object[] { 4_294_967_297L },                         // 2^32 + 1
        new object[] { unchecked((long)0xABCD_0000_1234_5678UL) } // high node bits set
    };

    private static readonly Vector3 Pos = new(1.5f, -2.5f, 3.5f);

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void FullSnapshot_round_trips_the_id(long value)
    {
        ReplicationRegistry reg = MoveProtocol.CreateRegistry();
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(value));
        server.Set(e, new ReplicatedPosition { Value = Pos });

        byte[] snap = SnapshotWriter.WriteFiltered(server, reg, new HashSet<long> { value },
            ReplicationChannels.Replicate, ownerNetId: value);

        var client = new World();
        var view = new ClientReplicationView(reg);
        view.Apply(client, snap);

        Assert.True(view.TryGetEntity(value, out Entity ce));
        Assert.True(client.TryGet(ce, out NetId nid) && nid.Value == value);
        Assert.True(client.TryGet(ce, out ReplicatedPosition p) && p.Value == Pos);
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void AoiDelta_round_trips_the_id(long value)
    {
        ReplicationRegistry reg = MoveProtocol.CreateRegistry();
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(value));
        server.Set(e, new ReplicatedPosition { Value = Pos });

        var repl = new AoiDeltaReplicator(reg);
        repl.BeginTick();
        byte[] delta = repl.WriteFor(slot: 0, server, new HashSet<long> { value }, ownerNetId: value);

        var client = new World();
        var view = new ClientReplicationView(reg);
        view.ApplyDelta(client, delta);

        Assert.True(view.TryGetEntity(value, out Entity ce));
        Assert.True(client.TryGet(ce, out NetId nid) && nid.Value == value);
        Assert.True(client.TryGet(ce, out ReplicatedPosition p) && p.Value == Pos);
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void PersistBlob_round_trips_the_id(long value)
    {
        var writer = new SnapshotBlobWriter();
        writer.AddEntity(value, new[] { new SnapshotBlobComponent(16, new byte[] { 1, 2, 3 }) });
        byte[] blob = writer.ToArray();

        var reader = new SnapshotBlobReader(blob);
        Assert.Single(reader.Entities);
        Assert.Equal(value, reader.Entities[0].NetId);
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.Entities[0].Components[0].Payload);
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void FrameHeader_round_trips_the_localNetId(long value)
    {
        byte[] frame = MoveProtocol.EncodeSnapshotFrame(localNetId: value, ackSeq: 7, snapshot: new byte[] { 9, 9 });
        Assert.True(MoveProtocol.TryDecodeSnapshotFrame(frame, out long localNetId, out int ackSeq, out byte[] body));
        Assert.Equal(value, localNetId);
        Assert.Equal(7, ackSeq);
        Assert.Equal(new byte[] { 9, 9 }, body);
    }
}
