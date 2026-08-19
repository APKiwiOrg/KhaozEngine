using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The 10.0.0 engine cell-blob migration (<see cref="NetIdBlobMigration.WidenV1ToV2(byte[])"/>): widens 32-bit entity ids to
/// 64-bit while leaving every component byte identical, and the full <see cref="CellPersistence"/> boot path brings a
/// committed pre-10.0.0 (9.x) save forward and restores it. The fixture <c>NetWorld/Fixtures/cell-v1-32bit.blob</c> is
/// a real wrapped v1 blob (one ReplicatedPosition entity at (10,0,20) under 32-bit NetId 5).
/// </summary>
public class NetIdBlobMigrationTests
{
    private static readonly Vector3 FixturePos = new(10f, 0f, 20f);

    private static byte[] FixtureBlob()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "NetWorld", "Fixtures", "cell-v1-32bit.blob");
        return File.ReadAllBytes(path);
    }

    // Just the snapshot body of the fixture (strip the 8-byte [magic][schemaVersion] cell-blob header).
    private static byte[] FixtureV1Body() => FixtureBlob()[8..];

    // A hand-built v1 (32-bit) snapshot body: one entity with the given 32-bit netId + a ReplicatedPosition (id 1).
    private static byte[] V1Body(int netId)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(1);                                  // entity count
        bw.Write(netId);                              // 32-bit netId
        bw.Write((ushort)MoveProtocol.PositionTypeId);
        bw.Write(10f); bw.Write(0f); bw.Write(20f);   // Vector3
        bw.Write((ushort)0);                          // terminator
        bw.Flush();
        return ms.ToArray();
    }

    private static Func<ushort, int> Builtins => id => id == MoveProtocol.PositionTypeId ? 12 : -1;

    [Fact]
    public void WidenV1ToV2_widens_the_fixture_id_and_keeps_the_component_bytes()
    {
        byte[] v2 = NetIdBlobMigration.WidenV1ToV2(FixtureV1Body());

        // The widened body is a valid v2 blob: read with the built-in length resolver (Position is unframed).
        var walked = new SnapshotBlobReader(v2, Builtins);
        Assert.Single(walked.Entities);
        Assert.Equal(5L, walked.Entities[0].NetId);           // 5 widened, numerically unchanged (node 0)
        Assert.Equal((ushort)MoveProtocol.PositionTypeId, walked.Entities[0].Components[0].TypeId);
        Assert.Equal(12, walked.Entities[0].Components[0].Payload.Length);
        // The widening added exactly 4 bytes (the id grew 4 -> 8); every other byte is unchanged.
        Assert.Equal(FixtureV1Body().Length + 4, v2.Length);
    }

    [Theory]
    [InlineData(0, 0L)]
    [InlineData(5, 5L)]
    [InlineData(int.MaxValue, (long)int.MaxValue)]
    [InlineData(-1, 4_294_967_295L)]   // a counter past 2^31 (stored as a negative int32) widens UNSIGNED into node 0
    public void WidenV1ToV2_widens_boundary_ids(int oldId, long expected)
    {
        byte[] v2 = NetIdBlobMigration.WidenV1ToV2(V1Body(oldId));
        var reader = new SnapshotBlobReader(v2, Builtins);
        Assert.Equal(expected, reader.Entities[0].NetId);
        Assert.Equal(0, NetIdAllocator.NodeOf(reader.Entities[0].NetId));   // node 0 (single process)
    }

    [Fact]
    public void WidenV1ToV2_throws_on_a_truncated_body()
    {
        // A body claiming one entity but cut off mid-netId: undecodable -> throws (the driver quarantines it).
        Assert.Throws<InvalidOperationException>(() => NetIdBlobMigration.WidenV1ToV2(new byte[] { 1, 0, 0, 0, 5, 0 }));
    }

    [Fact]
    public async Task CellPersistence_boots_a_real_9x_save_forward_and_restores_it()
    {
        var store = new InMemoryWorldStore();
        await store.SaveAsync("cell:0:0", FixtureBlob());   // the committed 9.x (v1) blob on disk

        ReplicationRegistry reg = MoveProtocol.CreateRegistry();
        var host = new ShardPersistenceHost(reg);
        var cp = new CellPersistence(host, store);          // default schema + the engine migration chain
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        await cp.LoadMetaAsync();
        await cp.PreloadAsync();   // enumerates cell:* -> instantiates (0,0) -> load enqueued
        await cp.FlushAsync();     // migrate + restore applied on the server thread

        Assert.Contains(issues, i => i.Kind == CellPersistenceIssueKind.Migrated && i.FromVersion == 1
            && i.ToVersion == WireGenerationBlobMigration.StampedSchemaVersion);   // v1 -> v2 -> v3 -> v4, all three engine steps
        Assert.DoesNotContain(issues, i => i.Kind == CellPersistenceIssueKind.QuarantinedCorrupt);

        Assert.True(host.Shard.TryGetCell(new CellCoord(0, 0), out CellSim cell));
        Assert.True(cell.TryGetOwned(5, out Entity e));                          // restored under the widened 64-bit id 5
        Assert.True(cell.World.TryGet(e, out ReplicatedPosition p) && p.Value == FixturePos);
        Assert.True(host.NextNetId >= 6);   // high-water resumed above the restored id, so a fresh spawn can't collide
    }

    /// <summary>
    /// The #353 headline, end to end. A v1 blob carrying a <see cref="MovementState"/> used to be QUARANTINED on the
    /// first boot after the upgrade: the v1 walk read its 13-byte payload correctly, and then the v2 -&gt; v3 step
    /// walked the same frame at the CURRENT 26-byte layout, ran off the end of it, and read the following bytes as a
    /// component type id. Nothing about the blob was corrupt - the chain simply described a payload that had grown six
    /// times since the version it was reading.
    /// </summary>
    [Fact]
    public async Task CellPersistence_bootsAV1SaveCarryingAMovementFrame_withoutQuarantine()
    {
        var movement = new MovementState
        {
            VerticalVelocity = -3.25f,
            Grounded = true,
            TimeSinceGrounded = 1.5f,
            JumpBufferRemaining = 0.25f,
        };
        byte[] body = new CellBlobFixtures.BodyBuilder(netId32: true)
            .Entity(5,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(NetIdBlobMigration.NetId32WireGeneration, FixturePos)),
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(NetIdBlobMigration.NetId32WireGeneration, movement)),
                (MoveProtocol.IdentityTypeId, CellBlobFixtures.Identity("Runner")))
            .ToBody();

        var store = new InMemoryWorldStore();
        await store.SaveAsync("cell:0:0", CellBlobFixtures.Wrap(NetIdBlobMigration.NetId32SchemaVersion, 0, body));

        var host = new ShardPersistenceHost(MoveProtocol.CreateRegistry());
        var cp = new CellPersistence(host, store);
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        await cp.PreloadAsync();
        await cp.FlushAsync();

        Assert.DoesNotContain(issues, i => i.Kind == CellPersistenceIssueKind.QuarantinedCorrupt);
        Assert.Contains(issues, i => i.Kind == CellPersistenceIssueKind.Migrated && i.FromVersion == 1);

        Assert.True(host.Shard.TryGetCell(new CellCoord(0, 0), out CellSim cell));
        Assert.True(cell.TryGetOwned(5, out Entity e));
        Assert.Equal(FixturePos, cell.World.Get<ReplicatedPosition>(e).Value);
        MovementState restored = cell.World.Get<MovementState>(e);
        Assert.Equal(movement.VerticalVelocity, restored.VerticalVelocity);
        Assert.True(restored.Grounded);
        Assert.Equal(movement.TimeSinceGrounded, restored.TimeSinceGrounded);
        Assert.Equal(movement.JumpBufferRemaining, restored.JumpBufferRemaining);
        // Every field the codec appended after generation 1 was never on disk, so it restores at its default.
        Assert.False(restored.Swimming);
        Assert.Equal(0u, restored.TeleportEpoch);
        Assert.Equal((sbyte)0, restored.ClimbRateQ);
        Assert.Equal((short)0, restored.FacingYawQ);
        Assert.Equal("Runner", cell.World.Get<PlayerIdentity>(e).DisplayName);
    }
}
