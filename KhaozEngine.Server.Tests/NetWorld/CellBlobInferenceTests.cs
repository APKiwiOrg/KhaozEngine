using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The UNDER-read direction of inferring a pre-v4 cell blob's wire generation, which is the case the 17.37.1
/// most-frames heuristic got wrong.
/// <para>
/// Its safety argument was that an over-long read can only swallow frames, so the truth always scores at least as
/// high as any candidate. That only covers candidates NEWER than the truth. A candidate OLDER than the truth reads a
/// built-in payload SHORT, and the bytes it leaves behind re-sync into frames the walk copies verbatim (an id at or
/// above <see cref="ReplicationRegistry.FirstExtensionTypeId"/> is opaque and length-prefixed, so any two bytes over
/// 16 open a frame), which can make the wrong candidate outscore the truth. The result was a structurally valid
/// current-generation body that restores silently with zeroed movement fields, a flipped ground flag and phantom
/// components, and is then re-persisted.
/// </para>
/// <para>
/// The contract these tests hold the migrations to is: a body either comes back EXACTLY, or it is refused. Never a
/// third thing.
/// </para>
/// </summary>
public class CellBlobInferenceTests
{
    private readonly ITestOutputHelper output;

    public CellBlobInferenceTests(ITestOutputHelper output) => this.output = output;

    private static readonly CellCoord C00 = new(0, 0);
    private static readonly Vector3 Pos = new(12.5f, 3f, 44.25f);   // inside cell (0,0) at the 64 m cell size

    // Two consumer extension components, the shapes the adversarial fixtures below need: a six-byte one at id 16 and
    // a twenty-four-byte one at id 17.
    private struct Trinket : IComponent { public int A; public short B; }

    private struct Bulky : IComponent { public byte[]? Data; }

    private const ushort TrinketId = ReplicationRegistry.FirstExtensionTypeId;          // 16
    private const ushort BulkyId = ReplicationRegistry.FirstExtensionTypeId + 1;        // 17
    private const int BulkyBytes = 24;

    private static ReplicationRegistry RegistryWithTrinket() => MoveProtocol.CreateRegistry(r =>
        r.Register<Trinket>(TrinketId,
            write: (t, bw) => { bw.Write(t.A); bw.Write(t.B); },
            read: br => new Trinket { A = br.ReadInt32(), B = br.ReadInt16() }));

    private static ReplicationRegistry RegistryWithBulky() => MoveProtocol.CreateRegistry(r =>
        r.Register<Bulky>(BulkyId,
            write: (b, bw) => bw.Write(b.Data ?? new byte[BulkyBytes]),
            read: br => new Bulky { Data = br.ReadBytes(BulkyBytes) }));

    // A length-prefixed extension frame's payload as the writer lays it down: [7-bit len][bytes].
    private static byte[] ExtensionFrame(byte[] payload)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write7BitEncodedInt(payload.Length);
        bw.Write(payload);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// The reviewer's v2 construction, hand-built so the arithmetic is on the page: a body written at wire generation
    /// 8 whose movement carries <c>HorizontalVelocityXQ = 16</c>, followed by a six-byte extension frame at id 16
    /// whose first payload byte is 5.
    /// <para>
    /// Walked at generation 6 the movement payload is 20 bytes rather than 24, so the two horizontal-velocity shorts
    /// are left over: <c>[16, 0]</c> reads as extension id 16, the next byte (2, the low half of
    /// <c>HorizontalVelocityZQ</c>) as its length, and the walk copies its way through the real frame's header into a
    /// second phantom frame at id 1536 whose length byte is the real payload's leading 5 - landing exactly on the
    /// terminator. Four frames recovered against the truth's three, so the most-frames rule preferred it and silently
    /// dropped both velocity shorts while inventing two components.
    /// </para>
    /// </summary>
    private static byte[] AdversarialV2Body(out MovementState seeded, out Trinket trinket)
    {
        seeded = new MovementState
        {
            VerticalVelocity = -3.25f,
            Grounded = true,
            TimeSinceGrounded = 1.5f,
            JumpBufferRemaining = 0.25f,
            Swimming = false,
            TeleportEpoch = 77u,
            ClimbRateQ = 9,
            SpeedScaleQ = -6,
            HorizontalVelocityXQ = 16,   // reads as extension type id 16 when the walk is four bytes short
            HorizontalVelocityZQ = 2,    // and its low byte as that phantom frame's length
            FacingYawQ = 4321,           // generation 8 never wrote this field
        };
        trinket = new Trinket { A = 5, B = 0x0201 };   // encodes to [5, 0, 0, 0, 1, 2]: the leading 5 is the second phantom's length
        byte[] payload = { 5, 0, 0, 0, 1, 2 };

        return new CellBlobFixtures.BodyBuilder()
            .Entity(1,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(8, Pos)),
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(8, seeded)),
                (TrinketId, ExtensionFrame(payload)))
            .ToBody();
    }

    /// <summary>
    /// The same trap one schema version later, and the one the reviewer measured through
    /// <see cref="WireGenerationBlobMigration.NormalizeV3ToV4(byte[])"/>: a 69-byte generation-10 body whose
    /// <c>FacingYawQ</c> is 16 and whose 24-byte extension payload carries <c>[15] = 0x20, [16] = 0x00,
    /// [17] = 6</c>. Walked at generation 9 the movement payload is two bytes short, <c>FacingYawQ</c> opens a
    /// phantom frame, and the walk re-syncs into a second one and lands on the terminator: three frames against the
    /// truth's two, so the most-frames rule returned 71 bytes for a 69-byte input.
    /// </summary>
    private static byte[] AdversarialV3Body(out MovementState seeded, out byte[] extensionPayload)
    {
        seeded = new MovementState
        {
            VerticalVelocity = 2f,
            Grounded = false,
            TimeSinceGrounded = 0.5f,
            JumpBufferRemaining = 0f,
            Swimming = true,
            TeleportEpoch = 3u,
            ClimbRateQ = -1,
            SpeedScaleQ = 2,
            HorizontalVelocityXQ = -300,
            HorizontalVelocityZQ = 450,
            FacingYawQ = 16,   // reads as extension type id 16 when the walk is two bytes short
        };
        extensionPayload = new byte[BulkyBytes];
        for (int i = 0; i < extensionPayload.Length; i++) extensionPayload[i] = (byte)(i + 1);
        extensionPayload[15] = 0x20;   // the phantom's next type id, 32
        extensionPayload[16] = 0x00;
        extensionPayload[17] = 6;      // its length, which lands the phantom walk exactly on the terminator

        return new CellBlobFixtures.BodyBuilder()
            .Entity(11,
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(10, seeded)),
                (BulkyId, ExtensionFrame(extensionPayload)))
            .ToBody();
    }

    private static void AssertMovement(MovementState expected, MovementState actual)
    {
        Assert.Equal(expected.VerticalVelocity, actual.VerticalVelocity);
        Assert.Equal(expected.Grounded, actual.Grounded);
        Assert.Equal(expected.TimeSinceGrounded, actual.TimeSinceGrounded);
        Assert.Equal(expected.JumpBufferRemaining, actual.JumpBufferRemaining);
        Assert.Equal(expected.Swimming, actual.Swimming);
        Assert.Equal(expected.TeleportEpoch, actual.TeleportEpoch);
        Assert.Equal(expected.ClimbRateQ, actual.ClimbRateQ);
        Assert.Equal(expected.SpeedScaleQ, actual.SpeedScaleQ);
        Assert.Equal(expected.HorizontalVelocityXQ, actual.HorizontalVelocityXQ);
        Assert.Equal(expected.HorizontalVelocityZQ, actual.HorizontalVelocityZQ);
        Assert.Equal(expected.FacingYawQ, actual.FacingYawQ);
    }

    /// <summary>
    /// The registry is what saves this one: the generation-6 mis-walk recovers a frame at id 1536, which nobody
    /// registered, so that candidate is discarded and the truth stands alone. Decoded through the same registry the
    /// server restores with, every movement field must be exactly what was seeded.
    /// </summary>
    [Fact]
    public void FrameV2ToV3_UnderReadingCandidate_DoesNotWinOverTheTruth()
    {
        byte[] body = AdversarialV2Body(out MovementState seeded, out Trinket trinket);
        ReplicationRegistry registry = RegistryWithTrinket();

        byte[] migrated = PositionFrameBlobMigration.FrameV2ToV3(body, new CellBlobMigrationOptions { Registry = registry });

        var world = new World();
        var view = new ClientReplicationView(registry);
        view.Apply(world, migrated);
        Assert.True(view.TryGetEntity(1, out Entity e));

        MovementState expected = seeded;
        expected.FacingYawQ = 0;   // generation 8 predates the field, so it restores at its default
        AssertMovement(expected, world.Get<MovementState>(e));
        Assert.Equal(Pos, world.Get<ReplicatedPosition>(e).Value);
        Assert.Equal(trinket.A, world.Get<Trinket>(e).A);
        Assert.Equal(trinket.B, world.Get<Trinket>(e).B);
    }

    /// <summary>
    /// And with no registry to filter with, both candidates survive and disagree, so the migration refuses rather
    /// than scoring them. Quarantine is a cell lost loudly; the alternative was a cell corrupted quietly.
    /// </summary>
    [Fact]
    public void FrameV2ToV3_UnderReadingCandidate_WithNoRegistry_IsAmbiguousNotGuessed()
    {
        byte[] body = AdversarialV2Body(out _, out _);

        var ex = Assert.Throws<AmbiguousCellBlobGenerationException>(() => PositionFrameBlobMigration.FrameV2ToV3(body));

        Assert.Contains(8, ex.CandidateGenerations);
        Assert.Contains(6, ex.CandidateGenerations);
    }

    /// <summary>The 71-bytes-for-69 case: a generation-10 body is already current, so the only correct output is the
    /// input.</summary>
    [Fact]
    public void NormalizeV3ToV4_UnderReadingCandidate_DoesNotWinOverTheTruth()
    {
        byte[] body = AdversarialV3Body(out MovementState seeded, out byte[] extensionPayload);
        Assert.Equal(69, body.Length);
        ReplicationRegistry registry = RegistryWithBulky();

        byte[] normalized = WireGenerationBlobMigration.NormalizeV3ToV4(body,
            new CellBlobMigrationOptions { Registry = registry });

        Assert.Equal(body, normalized);   // 71 bytes under the most-frames rule, from the generation-9 mis-walk

        var world = new World();
        var view = new ClientReplicationView(registry);
        view.Apply(world, normalized);
        Assert.True(view.TryGetEntity(11, out Entity e));
        AssertMovement(seeded, world.Get<MovementState>(e));
        Assert.Equal(extensionPayload, world.Get<Bulky>(e).Data);
    }

    [Fact]
    public void NormalizeV3ToV4_UnderReadingCandidate_WithNoRegistry_IsAmbiguousNotGuessed()
    {
        byte[] body = AdversarialV3Body(out _, out _);

        var ex = Assert.Throws<AmbiguousCellBlobGenerationException>(() => WireGenerationBlobMigration.NormalizeV3ToV4(body));

        Assert.Equal(new[] { 9, 10 }, ex.CandidateGenerations);
    }

    /// <summary>
    /// The generation-3 body whose identity frame a generation-8 walk swallows exactly (14 movement bytes plus a
    /// 2 + 2 + 6 name is 24, a generation-8 movement's worth). Nothing in the bytes distinguishes them, so this is
    /// what a genuinely ambiguous blob looks like: the driver quarantines it with its own reason, keeps the bytes,
    /// and an operator who knows which build wrote the save brings it in with AssumedWireGeneration.
    /// </summary>
    [Fact]
    public async Task AmbiguousBlob_IsQuarantinedWithItsOwnReason_AndAssumedWireGenerationResolvesIt()
    {
        byte[] ambiguous = new CellBlobFixtures.BodyBuilder()
            .Entity(21,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(3, Pos)),
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(3, Gen3Movement())),
                (MoveProtocol.IdentityTypeId, CellBlobFixtures.Identity("Runner")))
            .ToBody();
        byte[] blob = CellBlobFixtures.Wrap(PositionFrameBlobMigration.AbsolutePositionSchemaVersion, 0, ambiguous);

        var store = new InMemoryWorldStore();
        await store.SaveAsync("cell:0:0", blob);
        var host = new ShardPersistenceHost(MoveProtocol.CreateRegistry());
        var cp = new CellPersistence(host, store, new CellPersistenceConfig { Registry = MoveProtocol.CreateRegistry() });
        var issues = new List<CellPersistenceIssue>();
        cp.Issue += issues.Add;

        await cp.PreloadAsync();
        await cp.FlushAsync();

        CellPersistenceIssue quarantined = Assert.Single(issues);
        Assert.Equal(CellPersistenceIssueKind.QuarantinedAmbiguous, quarantined.Kind);
        Assert.Equal(PositionFrameBlobMigration.AbsolutePositionSchemaVersion, quarantined.FromVersion);
        Assert.Contains("3", quarantined.Message!);
        Assert.Contains("8", quarantined.Message!);
        Assert.Contains("AssumedWireGeneration", quarantined.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, cp.QuarantinedAmbiguousCellCount);
        Assert.Equal(blob, await store.LoadAsync("quarantine:cell:0:0"));   // bytes preserved
        Assert.True(host.Shard.TryGetCell(C00, out CellSim empty));
        Assert.False(empty.TryGetOwned(21, out _));                        // cell started fresh

        // Same blob, same build, with the save's provenance supplied: no candidates, no ambiguity, one walk.
        var store2 = new InMemoryWorldStore();
        await store2.SaveAsync("cell:0:0", blob);
        var host2 = new ShardPersistenceHost(MoveProtocol.CreateRegistry());
        var resolved = new CellPersistence(host2, store2, new CellPersistenceConfig
        {
            Registry = MoveProtocol.CreateRegistry(),
            AssumedWireGeneration = 3,
        });
        var issues2 = new List<CellPersistenceIssue>();
        resolved.Issue += issues2.Add;

        await resolved.PreloadAsync();
        await resolved.FlushAsync();

        Assert.Contains(issues2, i => i.Kind == CellPersistenceIssueKind.Migrated);
        Assert.DoesNotContain(issues2, i => i.Kind == CellPersistenceIssueKind.QuarantinedAmbiguous);
        Assert.True(host2.Shard.TryGetCell(C00, out CellSim cell));
        Assert.True(cell.TryGetOwned(21, out Entity restored));
        Assert.Equal("Runner", cell.World.Get<PlayerIdentity>(restored).DisplayName);
        MovementState m = cell.World.Get<MovementState>(restored);
        Assert.Equal(Gen3Movement().VerticalVelocity, m.VerticalVelocity);
        Assert.True(m.Swimming);
        Assert.Equal(0u, m.TeleportEpoch);   // generation 3 predates it
    }

    /// <summary>
    /// The rollback case, which the quarantine alone does not make safe: an older build skips a blob it cannot read
    /// and starts the cell FRESH, and the next save pass then writes that empty cell over the main key.
    /// <see cref="CellPersistenceConfig.FailFastOnTooNew"/> stops the boot instead, and even a caller that swallows
    /// the throw keeps the stored blob, because the coordinate stays marked as a load in flight and the dirty pass
    /// skips it.
    /// </summary>
    [Fact]
    public async Task FailFastOnTooNew_StopsTheBoot_AndLeavesTheStoredBlobAlone()
    {
        byte[] fromTheFuture = CellBlobFixtures.Wrap(WireGenerationBlobMigration.StampedSchemaVersion,
            MoveProtocol.WireProtocolVersion + 1,
            new CellBlobFixtures.BodyBuilder()
                .Entity(31, (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(10, Gen3Movement())))
                .ToBody());
        var store = new InMemoryWorldStore();
        await store.SaveAsync("cell:0:0", fromTheFuture);

        var host = new ShardPersistenceHost(MoveProtocol.CreateRegistry());
        var cp = new CellPersistence(host, store, new CellPersistenceConfig { FailFastOnTooNew = true });
        await cp.PreloadAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => cp.FlushAsync());
        Assert.Equal(1, cp.SkippedTooNewCellCount);

        // Swallow it and keep ticking: the empty cell must still not reach the store.
        cp.SaveDirtyPass();
        await cp.FlushAsync();
        Assert.Equal(fromTheFuture, await store.LoadAsync("cell:0:0"));
        Assert.Equal(fromTheFuture, await store.LoadAsync("quarantine:cell:0:0"));
    }

    [Fact]
    public void AssumedWireGeneration_OutsideTheKnownTable_ThrowsAtConstruction()
    {
        var cfg = new CellPersistenceConfig { AssumedWireGeneration = MoveProtocol.WireProtocolVersion + 1 };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CellPersistence(new ShardPersistenceHost(MoveProtocol.CreateRegistry()), new InMemoryWorldStore(), cfg));
    }

    private static MovementState Gen3Movement() => new()
    {
        VerticalVelocity = -1.25f,
        Grounded = true,
        TimeSinceGrounded = 0.5f,
        JumpBufferRemaining = 0.75f,
        Swimming = true,
    };

    /// <summary>
    /// The sweep, and the test that would have caught this before it shipped: two thousand bodies per schema range,
    /// written at a known generation with adversarial-by-construction field values (the quantized shorts that sit at
    /// the end of a movement payload take any value, including the ones that read as extension type ids), each pushed
    /// through the migration that has to infer that generation back.
    /// <para>
    /// The assertion is that the count of SILENT mis-decodes is zero: every body either comes back byte-for-byte as
    /// the same body walked at its true generation, or it is refused. A refusal is a cell an operator can still
    /// recover, so it is counted and reported rather than asserted on - the rate moves with the fixtures, the safety
    /// property does not.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(PositionFrameBlobMigration.AbsolutePositionSchemaVersion)]
    [InlineData(PositionFrameBlobMigration.FramedPositionSchemaVersion)]
    public void RandomisedSweep_NeverMisDecodes(int schemaVersion)
    {
        const int bodies = 2000;
        bool v2 = schemaVersion == PositionFrameBlobMigration.AbsolutePositionSchemaVersion;
        int oldest = v2 ? PositionFrameBlobMigration.OldestAbsolutePositionWireGeneration
            : WireGenerationBlobMigration.OldestUnstampedWireGeneration;
        int newest = v2 ? PositionFrameBlobMigration.NewestAbsolutePositionWireGeneration
            : MoveProtocol.WireProtocolVersion;
        int current = MoveProtocol.WireProtocolVersion;
        var options = new CellBlobMigrationOptions { Registry = RegistryWithTrinket() };

        var rng = new Random(20260819);
        int exact = 0, ambiguous = 0, noWalk = 0;
        var misDecoded = new List<string>();

        for (int i = 0; i < bodies; i++)
        {
            int generation = rng.Next(oldest, newest + 1);
            byte[] body = RandomBody(rng, generation);
            byte[] expected = CellBlobRewriter.Rewrite(body, generation, current, widenNetIds: false);

            byte[] actual;
            try
            {
                actual = v2 ? PositionFrameBlobMigration.FrameV2ToV3(body, options)
                    : WireGenerationBlobMigration.NormalizeV3ToV4(body, options);
            }
            catch (AmbiguousCellBlobGenerationException) { ambiguous++; continue; }
            catch (InvalidOperationException) { noWalk++; continue; }

            if (actual.AsSpan().SequenceEqual(expected)) { exact++; continue; }
            misDecoded.Add($"body {i} written at generation {generation}: {expected.Length} bytes expected, {actual.Length} returned");
        }

        output.WriteLine($"schema v{schemaVersion}: {exact} exact, {ambiguous} quarantined ambiguous, " +
                         $"{noWalk} quarantined unwalkable, {misDecoded.Count} MIS-DECODED (of {bodies})");
        Assert.True(misDecoded.Count == 0,
            $"{misDecoded.Count} of {bodies} bodies decoded to something other than themselves: " +
            string.Join(" | ", misDecoded.Count > 5 ? misDecoded.GetRange(0, 5) : misDecoded));
    }

    // One entity's worth of plausible saved state at a given generation, in the order the snapshot writer emits
    // components: built-ins ascending, then the consumer extension.
    private static byte[] RandomBody(Random rng, int generation)
    {
        var builder = new CellBlobFixtures.BodyBuilder();
        int entities = rng.Next(1, 4);
        for (int e = 0; e < entities; e++)
        {
            var components = new List<(ushort, byte[])>();
            if (rng.Next(4) != 0)
                components.Add((MoveProtocol.PositionTypeId,
                    CellBlobFixtures.Position(generation, new Vector3(rng.Next(-64, 64), rng.Next(0, 32), rng.Next(-64, 64)))));
            components.Add((MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(generation, RandomMovement(rng))));
            if (rng.Next(3) == 0)
                components.Add((MoveProtocol.IdentityTypeId, CellBlobFixtures.Identity(RandomName(rng))));
            if (rng.Next(4) == 0)
                components.Add((MoveProtocol.DynamicBodyTypeId, CellBlobFixtures.DynamicBody(
                    Quaternion.Identity, new Vector3(rng.Next(-5, 5), 0f, rng.Next(-5, 5)), Vector3.Zero)));
            if (generation >= BuiltinBlobLayout.PickupWireGeneration && rng.Next(4) == 0)
                components.Add((MoveProtocol.PickupTypeId, CellBlobFixtures.Pickup(rng.Next(1, 999), rng.Next(1, 99))));
            if (rng.Next(2) == 0)
            {
                var payload = new byte[6];
                rng.NextBytes(payload);
                components.Add((TrinketId, ExtensionFrame(payload)));
            }
            builder.Entity(e + 1, components.ToArray());
        }
        return builder.ToBody();
    }

    private static MovementState RandomMovement(Random rng) => new()
    {
        VerticalVelocity = (float)(rng.NextDouble() * 20 - 10),
        Grounded = rng.Next(2) == 0,
        TimeSinceGrounded = (float)rng.NextDouble(),
        JumpBufferRemaining = (float)rng.NextDouble(),
        Swimming = rng.Next(2) == 0,
        TeleportEpoch = (uint)rng.Next(0, 1000),
        ClimbRateQ = (sbyte)rng.Next(-128, 128),
        SpeedScaleQ = (sbyte)rng.Next(-128, 128),
        HorizontalVelocityXQ = (short)rng.Next(short.MinValue, short.MaxValue + 1),
        HorizontalVelocityZQ = (short)rng.Next(short.MinValue, short.MaxValue + 1),
        FacingYawQ = (short)rng.Next(short.MinValue, short.MaxValue + 1),
    };

    private static string RandomName(Random rng)
    {
        int len = rng.Next(1, 12);
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append((char)rng.Next('a', 'z' + 1));
        return sb.ToString();
    }
}
