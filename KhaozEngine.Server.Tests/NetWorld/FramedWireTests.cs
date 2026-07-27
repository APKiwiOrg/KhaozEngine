using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The frame-relative <see cref="ReplicatedPosition"/> wire (generation 9): the stamp plus a frame-local offset,
/// the cross-frame interpolation that rebases before it blends, and the proof that a game at the world origin is
/// unchanged by any of it.
/// </summary>
public class FramedWireTests
{
    private const long Id = 7L;

    private static byte[] Encode(ReplicatedPosition pos)
    {
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(Id));
        server.Set(e, pos);
        return SnapshotWriter.WriteFiltered(server, MoveProtocol.CreateRegistry(), new HashSet<long> { Id },
            ReplicationChannels.Replicate, ownerNetId: Id);
    }

    private static ReplicatedPosition Decode(byte[] snapshot)
    {
        var client = new World();
        var view = new ClientReplicationView(MoveProtocol.CreateRegistry());
        view.Apply(client, snapshot);
        Assert.True(view.TryGetEntity(Id, out Entity ce));
        Assert.True(client.TryGet(ce, out ReplicatedPosition p));
        return p;
    }

    [Fact]
    public void Test12_A_position_at_100km_round_trips_bit_identically_and_the_wire_carries_a_small_local()
    {
        // The whole point of the framed encoding: the float payload is bounded by the frame, not by how far the
        // world extends, so the wire stops being the first thing that quantizes a position.
        var far = new Vector3(100_000f, 12.5f, -100_000f);
        WorldFrame frame = WorldFrame.Nearest(far);
        ReplicatedPosition sent = ReplicatedPosition.FromWorld(far, frame);

        ReplicatedPosition got = Decode(Encode(sent));

        Assert.Equal(frame, got.Frame);
        Assert.Equal(sent.Local, got.Local);       // the three floats ride verbatim
        Assert.Equal(sent.Value, got.Value);       // and the absolute position is bit-identical

        // sent's local was built via FromWorld(far, frame) with frame = WorldFrame.Nearest(far), so by
        // WorldFrame.Nearest's own contract (rounds to the anchor NEAREST far) the local is structurally bounded by
        // half a grid cell on each planar axis - Grid/2, not any replay-window-derived figure. There is no
        // prediction replay anywhere in this test (Encode/Decode is a pure wire round trip), so a bound built out of
        // PredictionSettings.MaxPendingCommands and a sprint speed was never the real constraint here. It just
        // happened to be loose enough to pass.
        const float bound = WorldFrame.Grid / 2f;
        Assert.True(MathF.Abs(got.Local.X) <= bound, $"local X was {got.Local.X}, bound {bound}");
        Assert.True(MathF.Abs(got.Local.Z) <= bound, $"local Z was {got.Local.Z}, bound {bound}");
    }

    [Fact]
    public void Test16_A_game_at_the_origin_is_byte_identical_to_the_pre_frame_encoding_for_the_local_triple()
    {
        // Provable inertness at the origin: WorldFrame.Origin is (0, 0), so the two stamp shorts are zero and the
        // three floats that follow are exactly the bytes the pre-frame codec wrote.
        var p = new Vector3(1.5f, -2.5f, 3.5f);
        byte[] snapshot = Encode(ReplicatedPosition.FromWorld(p, WorldFrame.Origin));

        int at = IndexOfPositionPayload(snapshot);
        Assert.Equal(0, BitConverter.ToInt16(snapshot, at));         // frame X
        Assert.Equal(0, BitConverter.ToInt16(snapshot, at + 2));     // frame Z
        Assert.Equal(p.X, BitConverter.ToSingle(snapshot, at + 4));
        Assert.Equal(p.Y, BitConverter.ToSingle(snapshot, at + 8));
        Assert.Equal(p.Z, BitConverter.ToSingle(snapshot, at + 12));

        ReplicatedPosition got = Decode(snapshot);
        Assert.Equal(WorldFrame.Origin, got.Frame);
        Assert.Equal(p, got.Value);
    }

    // The snapshot body is [count:int][netId:long][typeId:ushort][payload...]: the position payload starts at 14.
    private static int IndexOfPositionPayload(byte[] snapshot)
    {
        Assert.Equal(1, BitConverter.ToInt32(snapshot, 0));
        Assert.Equal(Id, BitConverter.ToInt64(snapshot, 4));
        Assert.Equal(MoveProtocol.PositionTypeId, BitConverter.ToUInt16(snapshot, 12));
        return 14;
    }

    [Fact]
    public void Test13_Two_snapshots_in_different_frames_interpolate_along_the_straight_world_line()
    {
        // The failure this rules out: lerping two locals that are expressed against different anchors interpolates
        // between two SPACES, which parks a remote a frame-width from the line it should be travelling along. The
        // codec rebases into the newer sample's frame first, so the blend runs on one space and stays precise.
        var frameA = new WorldFrame(780, -781);                       // ~ (99,840, -99,968)
        var frameB = new WorldFrame(781, -781);                       // one grid step along X
        Vector3 worldA = frameA.ToWorld(new Vector3(60f, 4f, -10f));
        Vector3 worldB = frameB.ToWorld(new Vector3(-50f, 6f, 20f));  // ~18 m further on in world space

        var client = new World();
        var view = new ClientReplicationView(MoveProtocol.CreateRegistry());

        view.Apply(client, Encode(ReplicatedPosition.InFrame(frameA, frameA.ToLocal(worldA))));
        view.RecordInterpolationSample(0.0);
        view.Apply(client, Encode(ReplicatedPosition.InFrame(frameB, frameB.ToLocal(worldB))));
        view.RecordInterpolationSample(1.0);

        Assert.True(view.TryGetEntity(Id, out Entity ce));
        for (int i = 0; i <= 10; i++)
        {
            double t = i / 10.0;
            view.InterpolateAt(client, t);
            Assert.True(client.TryGet(ce, out ReplicatedPosition p));
            Vector3 expected = Vector3.Lerp(worldA, worldB, (float)t);
            // A raw two-space lerp (blending the two locals without rebasing one into the other's frame first) would
            // land a whole grid step off even at t=0 (frameB.ToWorld(aLocal) differs from worldA by frameB.Anchor -
            // frameA.Anchor, one grid step here), so this 0.01 m bound alone already fails a broken implementation
            // by two orders of magnitude - a separate looser bound added nothing a reader could not already see.
            Assert.True(Vector3.Distance(p.Value, expected) < 0.01f,
                $"at t={t:F1} the interpolated world position was {p.Value}, expected {expected}");
        }
    }

    [Fact]
    public void A_frame_conversion_preserves_the_absolute_position_which_is_what_every_reader_keys_on()
    {
        // Handoff, ghost mirror and the step loop's self-heal all go through ToFrame, and every engine path behind
        // them (cell keying, the interest grid, persistence) reads Value. Exactness there is the load-bearing bit.
        var frameA = new WorldFrame(0, 0);
        var frameB = new WorldFrame(1, 0);
        ReplicatedPosition a = ReplicatedPosition.FromWorld(new Vector3(60.1f, 3f, 12f), frameA);
        ReplicatedPosition b = a.ToFrame(frameB);

        Assert.Equal(frameB, b.Frame);
        // Exact to half a ULP of the destination magnitude, not bit-exact: a crossing can grow the local across a
        // binade boundary (60.1 becomes -67.9 here, which is exactly that case).
        Assert.True(Vector3.Distance(a.Value, b.Value) <= MathF.Pow(2f, -18f),
            $"the conversion moved the absolute position by {Vector3.Distance(a.Value, b.Value)} m");
    }
}
