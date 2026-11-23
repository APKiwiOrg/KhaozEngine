using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.NetWorld;

[Collection("AllocSensitive")]
public sealed class ClientReceiveAllocationDiagnostics(ITestOutputHelper output)
{
    private const int WarmupFrames = 16;
    private const int MeasuredFrames = 32;
    private const NetChannelReliability Channel = NetChannelReliability.ReliableOrdered;

    [Theory]
    [InlineData(8, false)]
    [InlineData(64, false)]
    [InlineData(8, true)]
    [InlineData(64, true)]
    [Trait("Category", "Diagnostic")]
    public void ReportWarmedReceiveAndPresentation(int entityCount, bool delta)
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry(static r => r.Register<Marker>(
            ReplicationRegistry.FirstExtensionTypeId, static (m, w) => w.Write(m.Value),
            static r => new Marker(r.ReadInt32()), discreteSample: true));
        byte[][] frames = MakeFrames(registry, entityCount, delta);
        (LoopbackTransport source, LoopbackTransport receiver) = LoopbackTransport.CreatePair();
        using var sourceLease = source;
        using var receiverLease = receiver;
        var session = new NetClient(receiver, token: null, borrowOwnedFrames: true);
        using var client = new WorldClient(receiver, static (_, _) => 0f, MoveTuning.Default, registry: registry);
        World world = client.WorldForTest;
        ClientReplicationView view = client.ViewForTest;
        var reusedRenderSnapshot = new List<EntityRenderState>(entityCount);
        Span<long> counts = stackalloc long[7];
        for (int i = 0; i < frames.Length; i++)
        {
            if (i == WarmupFrames) counts.Clear();
            source.Send(new NetConnectionId(1), frames[i], Channel);
            world.AdvanceTick();

            long start = GC.GetAllocatedBytesForCurrentThread();
            session.Poll();
            if (!session.TryDequeueFrame(out ClientSessionFrame ev) || ev.Kind != ClientSessionEventKind.Data)
                throw new UnreachableException("The diagnostic queued one data frame.");
            counts[0] += GC.GetAllocatedBytesForCurrentThread() - start;

            start = GC.GetAllocatedBytesForCurrentThread();
            if (!MoveProtocol.TryBorrowServerFrame(ev.Payload, out MoveProtocol.ServerFrameKind kind,
                    out ReadOnlyMemory<byte> payload)
                || !MoveProtocol.TryBorrowServedFrame(kind, payload, out _, out _, out _, out ReadOnlyMemory<byte> body))
                throw new UnreachableException("The diagnostic sent a valid world frame.");
            counts[1] += GC.GetAllocatedBytesForCurrentThread() - start;

            start = GC.GetAllocatedBytesForCurrentThread();
            if (kind == MoveProtocol.ServerFrameKind.Delta) view.ApplyDeltaBorrowed(world, body);
            else view.ApplyBorrowed(world, body);
            counts[2] += GC.GetAllocatedBytesForCurrentThread() - start;

            start = GC.GetAllocatedBytesForCurrentThread();
            view.RecordInterpolationSample(i);
            counts[3] += GC.GetAllocatedBytesForCurrentThread() - start;

            start = GC.GetAllocatedBytesForCurrentThread();
            view.InterpolateAt(world, i - 0.5);
            counts[4] += GC.GetAllocatedBytesForCurrentThread() - start;

            start = GC.GetAllocatedBytesForCurrentThread();
            var snapshot = client.Snapshot();
            counts[5] += GC.GetAllocatedBytesForCurrentThread() - start;
            start = GC.GetAllocatedBytesForCurrentThread();
            client.CopySnapshotTo(reusedRenderSnapshot);
            counts[6] += GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal(entityCount, snapshot.Count);
            Assert.Equal(entityCount, reusedRenderSnapshot.Count);
        }

        output.WriteLine($"{entityCount} remotes, 2 sampled components each, {MeasuredFrames} warmed "
            + $"{(delta ? "delta" : "snapshot")} frames and updates");
        string[] names =
        [
            "Session receive/decode", "World dispatch", "Apply", "History stamp", "Interpolation",
            "Render snapshot", "Render snapshot into caller storage"
        ];
        for (int i = 0; i < names.Length; i++)
            output.WriteLine($"{names[i]}: {counts[i] / (double)MeasuredFrames:F2} B/{(i < 4 ? "frame" : "update")}");
    }

    private static byte[][] MakeFrames(ReplicationRegistry registry, int entityCount, bool delta)
    {
        var world = new World();
        var entities = new Entity[entityCount];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Spawn();
            world.Set(entities[i], new NetId(i + 1));
        }

        var replicator = new ServerReplicator(registry);
        var frames = new byte[WarmupFrames + MeasuredFrames][];
        for (int i = 0; i < frames.Length; i++)
        {
            world.AdvanceTick();
            for (int j = 0; j < entities.Length; j++)
            {
                world.Set(entities[j], ReplicatedPosition.InFrame(WorldFrame.Origin, new Vector3(i, j, 0)));
                world.Set(entities[j], new Marker(i));
            }

            byte[] body;
            if (delta)
            {
                replicator.Capture(world);
                body = replicator.WriteFor(slot: 0);
            }
            else
            {
                body = SnapshotWriter.Write(world, registry);
            }

            var kind = delta ? MoveProtocol.ServerFrameKind.Delta : MoveProtocol.ServerFrameKind.Snapshot;
            frames[i] = SessionFrame.Write(SessionOpcode.Data,
                MoveProtocol.EncodeServerFrame(kind, MoveProtocol.EncodeSnapshotFrame(-1, -1, body)));
        }

        return frames;
    }

    private readonly record struct Marker(int Value) : IComponent;
}
