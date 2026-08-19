using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The wire-generation stamp (cell-blob schema v4) and the bring-forward it enables.
/// <para>
/// Until v4 the blob header recorded the schema version and nothing else, while the layout of every UNFRAMED built-in
/// moved with the wire generation - which ran 2 to 10 across two schema versions (#353, #322). From v4 the header
/// carries the writing build's <see cref="MoveProtocol.WireProtocolVersion"/>, so the driver knows the layout instead
/// of inferring it, a later generation bump needs neither a schema bump nor a new migration, and a blob written by a
/// NEWER generation is skipped cleanly instead of misread as the shapes this build happens to know.
/// </para>
/// </summary>
public class WireGenerationBlobMigrationTests
{
    private static readonly CellCoord C00 = new(0, 0);
    private static readonly Vector3 Pos = new(12.5f, 3f, 44.25f);   // inside cell (0,0) at the 64 m cell size

    private static MovementState Movement() => new()
    {
        VerticalVelocity = -1.5f,
        Grounded = true,
        TimeSinceGrounded = 0.25f,
        JumpBufferRemaining = 0.5f,
        Swimming = true,
        TeleportEpoch = 9u,
        ClimbRateQ = -4,
        SpeedScaleQ = 7,
        HorizontalVelocityXQ = 321,
        HorizontalVelocityZQ = -654,
        FacingYawQ = 4242,
    };

    private static byte[] BodyAt(int generation, MovementState m) =>
        new CellBlobFixtures.BodyBuilder()
            .Entity(11,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(generation, Pos)),
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(generation, m)))
            .ToBody();

    [Fact]
    public void NormalizeV3ToV4_WidensAGeneration9Body_ToTheCurrentLayout()
    {
        MovementState m = Movement();
        byte[] atNine = BodyAt(BuiltinBlobLayout.FramedPositionWireGeneration, m);

        byte[] normalized = WireGenerationBlobMigration.NormalizeV3ToV4(atNine);

        // Generation 9 predates FacingYawQ, so the field it never wrote comes back at its default.
        MovementState expected = m;
        expected.FacingYawQ = 0;
        Assert.Equal(BodyAt(MoveProtocol.WireProtocolVersion, expected), normalized);
    }

    [Fact]
    public void NormalizeV3ToV4_ACurrentGenerationBody_IsUnchanged()
    {
        byte[] atCurrent = BodyAt(MoveProtocol.WireProtocolVersion, Movement());
        Assert.Equal(atCurrent, WireGenerationBlobMigration.NormalizeV3ToV4(atCurrent));
    }

    [Fact]
    public void NormalizeV3ToV4_BodyThatWalksAtNoGeneration_Throws()
    {
        byte[] corrupt = new CellBlobFixtures.BodyBuilder()
            .Entity(11,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(BuiltinBlobLayout.FramedPositionWireGeneration, Pos)),
                (MoveProtocol.MovementTypeId, new byte[21]))
            .ToBody();

        Assert.Throws<InvalidOperationException>(() => WireGenerationBlobMigration.NormalizeV3ToV4(corrupt));
    }

    [Fact]
    public void NormalizeToCurrent_FromAGenerationThatBodyIsNotAt_Throws()
    {
        // The stamped path does not guess: a header claiming generation 10 over a generation-9 body is a lie the walk
        // catches, and the driver quarantines rather than restoring a frame short.
        byte[] atNine = BodyAt(BuiltinBlobLayout.FramedPositionWireGeneration, Movement());
        Assert.Throws<InvalidOperationException>(() =>
            BuiltinBlobLayout.NormalizeToCurrent(atNine, MoveProtocol.WireProtocolVersion));
    }

    [Fact]
    public async Task SavedBlob_CarriesTheSchemaAndWireGenerationInItsHeader()
    {
        var store = new InMemoryWorldStore();
        var host = new ShardPersistenceHost(MoveProtocol.CreateRegistry());
        SeedEntity(host, netId: 5);

        var cp = new CellPersistence(host, store);
        cp.SaveDirtyPass();
        await cp.FlushAsync();

        byte[] blob = (await store.LoadAsync("cell:0:0"))!;
        byte[] body = host.SnapshotCell(C00)!;
        Assert.Equal(CellBlobFixtures.Wrap(WireGenerationBlobMigration.StampedSchemaVersion,
            MoveProtocol.WireProtocolVersion, body), blob);
    }

    /// <summary>
    /// The whole point of the stamp: once a generation bump lands with no schema bump, a stored blob still says which
    /// layout it is in, and the driver walks it forward. Modelled by stamping a blob at the PREVIOUS generation, which
    /// is exactly what every blob on disk will look like after the next bump.
    /// </summary>
    [Fact]
    public async Task StampedBlobFromAnOlderWireGeneration_IsBroughtForwardAndRewritten()
    {
        int older = MoveProtocol.WireProtocolVersion - 1;
        MovementState m = Movement();
        var store = new InMemoryWorldStore();
        await store.SaveAsync("cell:0:0", CellBlobFixtures.Wrap(
            WireGenerationBlobMigration.StampedSchemaVersion, older, BodyAt(older, m)));

        var host = new ShardPersistenceHost(MoveProtocol.CreateRegistry());
        var cp = new CellPersistence(host, store);
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        await cp.PreloadAsync();
        await cp.FlushAsync();

        Assert.DoesNotContain(issues, i => i.Kind == CellPersistenceIssueKind.QuarantinedCorrupt);
        Assert.Contains(issues, i => i.Kind == CellPersistenceIssueKind.Migrated
            && i.Message is not null && i.Message.Contains($"wire generation {older}", StringComparison.Ordinal));

        Assert.True(host.Shard.TryGetCell(C00, out CellSim cell));
        Assert.True(cell.TryGetOwned(11, out Entity e));
        MovementState restored = cell.World.Get<MovementState>(e);
        Assert.Equal(m.TeleportEpoch, restored.TeleportEpoch);
        Assert.Equal(m.HorizontalVelocityXQ, restored.HorizontalVelocityXQ);
        Assert.Equal((short)0, restored.FacingYawQ);   // the older generation never wrote it

        // A brought-forward cell is rewritten once, so the stored blob now carries the CURRENT generation and the
        // next boot does no work.
        cp.SaveDirtyPass();
        await cp.FlushAsync();
        byte[] rewritten = (await store.LoadAsync("cell:0:0"))!;
        Assert.Equal(MoveProtocol.WireProtocolVersion, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(rewritten.AsSpan(8, 4)));
    }

    [Fact]
    public async Task StampedBlobFromANewerWireGeneration_IsSkippedNotMisread()
    {
        var store = new InMemoryWorldStore();
        await store.SaveAsync("cell:0:0", CellBlobFixtures.Wrap(
            WireGenerationBlobMigration.StampedSchemaVersion, MoveProtocol.WireProtocolVersion + 1,
            BodyAt(MoveProtocol.WireProtocolVersion, Movement())));
        byte[] original = (await store.LoadAsync("cell:0:0"))!;

        var host = new ShardPersistenceHost(MoveProtocol.CreateRegistry());
        var cp = new CellPersistence(host, store);
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        await cp.PreloadAsync();
        await cp.FlushAsync();

        Assert.Contains(issues, i => i.Kind == CellPersistenceIssueKind.SkippedTooNew);
        Assert.True(host.Shard.TryGetCell(C00, out CellSim cell));
        Assert.False(cell.TryGetOwned(11, out _));                        // cell starts fresh
        Assert.Equal(original, await store.LoadAsync("quarantine:cell:0:0"));   // bytes preserved
    }

    /// <summary>Round trip at the current version: snapshot -&gt; blob -&gt; restore -&gt; snapshot is byte-stable, so
    /// a blob written by this build is not silently rewritten on every boot.</summary>
    [Fact]
    public async Task CurrentVersionBlob_RoundTripsByteIdentically()
    {
        var store = new InMemoryWorldStore();
        var writer = new ShardPersistenceHost(MoveProtocol.CreateRegistry());
        SeedEntity(writer, netId: 5);
        byte[] written = writer.SnapshotCell(C00)!;

        var writing = new CellPersistence(writer, store);
        writing.SaveDirtyPass();
        await writing.FlushAsync();

        var reader = new ShardPersistenceHost(MoveProtocol.CreateRegistry());
        var loading = new CellPersistence(reader, store);
        var issues = new List<CellPersistenceIssue>();
        loading.Issue += issues.Add;
        await loading.PreloadAsync();
        await loading.FlushAsync();

        Assert.Empty(issues);   // no migration, no generation walk, no quarantine
        Assert.Equal(written, reader.SnapshotCell(C00));
    }

    private static void SeedEntity(ShardPersistenceHost host, long netId)
    {
        Entity e = host.Shard.SpawnOwned(Pos.X, Pos.Z, netId, out CellSim cell);
        Assert.Equal(C00, cell.Coord);
        cell.World.Set(e, ReplicatedPosition.InFrame(cell.Frame, cell.Frame.ToLocal(Pos)));
        cell.World.Set(e, Movement());
    }
}
