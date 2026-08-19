using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Proves <see cref="PositionFrameBlobMigration.FrameV2ToV3(byte[])"/> round-trips a REAL v2 snapshot body end to end, at
/// EVERY wire generation a v2 blob can have been written at.
/// <para>
/// That range is the point. The cell-blob schema sat at v2 while the wire ran from generation 2 to 8, and the
/// movement built-in grew in five of those steps, so "a v2 blob" is seven different byte layouts and nothing in the
/// header says which (#353, #322). The migration infers the generation by walking the body at every candidate,
/// discarding the ones that recovered something no build writes, and brings every built-in payload to this build's
/// layout - so a gen-5 save and a gen-8 save both boot into a gen-10 server, with the fields their generation
/// predates coming back at their defaults rather than as garbage read out of the following frame. When two candidates
/// survive to different results the migration refuses (see the ambiguity case below) and the operator's
/// <see cref="CellBlobMigrationOptions.AssumedWireGeneration"/> is what brings that body in.
/// </para>
/// <para>
/// The previous version of this file seeded its fixture with the CURRENT movement encoder and called it v2, which no
/// build ever wrote: schema v2 and wire generation 10 never coexisted. It passed because the migration's private
/// table happened to state that same current length, which is exactly the staleness #353 is about.
/// </para>
/// </summary>
public class PositionFrameBlobMigrationTests
{
    // The newest wire generation a v2 blob can carry: generation 9 (framed position) is what moved the schema to v3.
    private const int NewestV2Generation = 8;

    private static readonly Vector3 PlayerPos = new(123.5f, 45f, -678.25f);
    private static readonly Vector3 PropPos = new(-40f, 3f, 90.5f);

    private static MovementState FullMovement() => new()
    {
        VerticalVelocity = -2.5f,
        Grounded = true,
        TimeSinceGrounded = 0.75f,
        JumpBufferRemaining = 0.1f,
        Swimming = true,
        TeleportEpoch = 42u,
        ClimbRateQ = 5,
        SpeedScaleQ = -3,
        HorizontalVelocityXQ = 1000,
        HorizontalVelocityZQ = -2000,
        FacingYawQ = 12345,
    };

    // What a state written at `generation` must read back as on a current build: the fields that generation carried,
    // and the DEFAULT for every field the codec appended after it (there were no bytes on disk to carry them).
    private static MovementState AsStoredAt(int generation, MovementState m) => new()
    {
        VerticalVelocity = m.VerticalVelocity,
        Grounded = m.Grounded,
        TimeSinceGrounded = m.TimeSinceGrounded,
        JumpBufferRemaining = m.JumpBufferRemaining,
        Swimming = generation >= 3 && m.Swimming,
        TeleportEpoch = generation >= 4 ? m.TeleportEpoch : 0u,
        ClimbRateQ = generation >= 5 ? m.ClimbRateQ : (sbyte)0,
        SpeedScaleQ = generation >= 6 ? m.SpeedScaleQ : (sbyte)0,
        HorizontalVelocityXQ = generation >= 7 ? m.HorizontalVelocityXQ : (short)0,
        HorizontalVelocityZQ = generation >= 7 ? m.HorizontalVelocityZQ : (short)0,
        FacingYawQ = generation >= 10 ? m.FacingYawQ : (short)0,
    };

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

    // A v2 body as a build at `generation` wrote it: entity 1 carries position + movement + identity (the shape the
    // stale table broke on), entity 2 a dynamic body, plus a pickup once that built-in existed.
    private static byte[] V2BodyAt(int generation, MovementState movement)
    {
        var propComponents = new List<(ushort, byte[])>
        {
            (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(generation, PropPos)),
            (MoveProtocol.DynamicBodyTypeId, CellBlobFixtures.DynamicBody(
                Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)),
                new Vector3(1f, 2f, 3f), new Vector3(0.5f, -0.5f, 0.25f))),
        };
        if (generation >= BuiltinBlobLayout.PickupWireGeneration)
            propComponents.Add((MoveProtocol.PickupTypeId, CellBlobFixtures.Pickup(999, 2)));

        return new CellBlobFixtures.BodyBuilder()
            .Entity(1,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(generation, PlayerPos)),
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(generation, movement)),
                (MoveProtocol.IdentityTypeId, CellBlobFixtures.Identity("Runner")))
            .Entity(2, propComponents.ToArray())
            .ToBody();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void FrameV2ToV3_MigratesARealBodyFromEveryV2WireGeneration(int generation)
    {
        MovementState seeded = FullMovement();
        byte[] v2Body = V2BodyAt(generation, seeded);
        var options = new CellBlobMigrationOptions { Registry = MoveProtocol.CreateRegistry() };

        byte[] v3Body;
        try
        {
            v3Body = PositionFrameBlobMigration.FrameV2ToV3(v2Body, options);
        }
        catch (AmbiguousCellBlobGenerationException ex)
        {
            // A body that reads cleanly at more than one generation is not migrated on a guess. The truth is always
            // among the candidates, and stating it is what recovers the cell.
            Assert.Contains(generation, ex.CandidateGenerations);
            v3Body = PositionFrameBlobMigration.FrameV2ToV3(v2Body,
                new CellBlobMigrationOptions { Registry = MoveProtocol.CreateRegistry(), AssumedWireGeneration = generation });
        }

        AssertRestoresAsWrittenAt(generation, seeded, v3Body);
    }

    // Every field of the migrated body, decoded through the production registry the server restores with.
    private static void AssertRestoresAsWrittenAt(int generation, MovementState seeded, byte[] v3Body)
    {
        var clientWorld = new World();
        var view = new ClientReplicationView(MoveProtocol.CreateRegistry());
        view.Apply(clientWorld, v3Body);

        Assert.True(view.TryGetEntity(1, out Entity player));
        Assert.Equal(PlayerPos, clientWorld.Get<ReplicatedPosition>(player).Value);
        AssertMovement(AsStoredAt(generation, seeded), clientWorld.Get<MovementState>(player));
        Assert.Equal("Runner", clientWorld.Get<PlayerIdentity>(player).DisplayName);

        Assert.True(view.TryGetEntity(2, out Entity prop));
        Assert.Equal(PropPos, clientWorld.Get<ReplicatedPosition>(prop).Value);
        DynamicBodyState body = clientWorld.Get<DynamicBodyState>(prop);
        Assert.Equal(new Vector3(1f, 2f, 3f), body.LinearVelocity);
        Assert.Equal(new Vector3(0.5f, -0.5f, 0.25f), body.AngularVelocity);

        if (generation >= BuiltinBlobLayout.PickupWireGeneration)
        {
            PickupState pickup = clientWorld.Get<PickupState>(prop);
            Assert.Equal(999, pickup.PayloadId);
            Assert.Equal(2, pickup.OwnerNetId);
        }
    }

    /// <summary>
    /// The walk over an unrecorded generation is ambiguous by construction, and this is the case that proved it: a
    /// generation-3 movement payload (14 bytes) followed by an identity frame for a six-character name (2 + 2 + 6
    /// bytes) is EXACTLY a generation-8 movement payload, so a generation-8 walk reads the name into the movement
    /// frame, ends on the same terminator and consumes the buffer exactly. Both readings are structurally perfect and
    /// nothing in the bytes prefers either.
    /// <para>
    /// 17.37.1 shipped a frame count as the tie-break, on the argument that an over-long read can only ever swallow
    /// frames so the truth always scores at least as high. That is only true of candidates NEWER than the truth: an
    /// OLDER candidate under-reads a payload and the leftover bytes re-sync into extra frames, which can outscore the
    /// truth (see <c>CellBlobInferenceTests</c>). So there is no tie-break any more. A body that reads two ways is
    /// refused, and the cell is quarantined with its bytes intact rather than migrated into one of them.
    /// </para>
    /// </summary>
    [Fact]
    public void FrameV2ToV3_TwoCleanWalks_AreRefusedRatherThanScored()
    {
        byte[] v2Body = new CellBlobFixtures.BodyBuilder()
            .Entity(1,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(3, PlayerPos)),
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(3, FullMovement())),
                (MoveProtocol.IdentityTypeId, CellBlobFixtures.Identity("Runner")))
            .ToBody();

        var ex = Assert.Throws<AmbiguousCellBlobGenerationException>(() =>
            PositionFrameBlobMigration.FrameV2ToV3(v2Body, new CellBlobMigrationOptions { Registry = MoveProtocol.CreateRegistry() }));

        // 7 and 8 read this body identically (they share a movement length), so the disagreement is 3 against them,
        // and every surviving candidate is named: an operator picking one has to see all of them.
        Assert.Equal(new[] { 3, 7, 8 }, ex.CandidateGenerations);
    }

    /// <summary>The same body, with the save's provenance stated: one walk, no candidates, and the name that a
    /// generation-8 reading would have eaten comes back.</summary>
    [Fact]
    public void FrameV2ToV3_AssumedWireGeneration_ReadsTheAmbiguousBodyExactly()
    {
        MovementState seeded = FullMovement();
        byte[] v2Body = new CellBlobFixtures.BodyBuilder()
            .Entity(1,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(3, PlayerPos)),
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(3, seeded)),
                (MoveProtocol.IdentityTypeId, CellBlobFixtures.Identity("Runner")))
            .ToBody();

        byte[] v3Body = PositionFrameBlobMigration.FrameV2ToV3(v2Body,
            new CellBlobMigrationOptions { AssumedWireGeneration = 3 });

        var clientWorld = new World();
        var view = new ClientReplicationView(MoveProtocol.CreateRegistry());
        view.Apply(clientWorld, v3Body);
        Assert.True(view.TryGetEntity(1, out Entity player));

        // Read as generation 8 the name would be gone and the epoch would be four bytes of "Runne".
        Assert.Equal("Runner", clientWorld.Get<PlayerIdentity>(player).DisplayName);
        AssertMovement(AsStoredAt(3, seeded), clientWorld.Get<MovementState>(player));
    }

    /// <summary>
    /// The migrated body must be exactly what the CURRENT codec would have written for the same state, not merely
    /// something that decodes: a payload padded to the right length with the wrong bytes would pass a field-by-field
    /// read of a state whose newer fields are all zero anyway.
    /// </summary>
    [Fact]
    public void FrameV2ToV3_ProducesTheBytesTheCurrentCodecWouldHaveWritten()
    {
        MovementState seeded = FullMovement();
        byte[] migrated = PositionFrameBlobMigration.FrameV2ToV3(V2BodyAt(NewestV2Generation, seeded));

        byte[] expected = new CellBlobFixtures.BodyBuilder()
            .Entity(1,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(MoveProtocol.WireProtocolVersion, PlayerPos)),
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(MoveProtocol.WireProtocolVersion,
                    AsStoredAt(NewestV2Generation, seeded))),
                (MoveProtocol.IdentityTypeId, CellBlobFixtures.Identity("Runner")))
            .Entity(2,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(MoveProtocol.WireProtocolVersion, PropPos)),
                (MoveProtocol.DynamicBodyTypeId, CellBlobFixtures.DynamicBody(
                    Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)),
                    new Vector3(1f, 2f, 3f), new Vector3(0.5f, -0.5f, 0.25f))),
                (MoveProtocol.PickupTypeId, CellBlobFixtures.Pickup(999, 2)))
            .ToBody();

        Assert.Equal(expected, migrated);
    }

    /// <summary>A consumer extension frame is opaque to the engine and must survive the walk verbatim, including at a
    /// generation the walk had to infer.</summary>
    [Fact]
    public void FrameV2ToV3_CopiesExtensionFramesVerbatim()
    {
        byte[] extension = { 1, 2, 3, 4, 5 };
        byte[] v2Body = new CellBlobFixtures.BodyBuilder()
            .Entity(7,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(5, PropPos)),
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(5, FullMovement())),
                (ReplicationRegistry.FirstExtensionTypeId, new byte[] { (byte)extension.Length, 1, 2, 3, 4, 5 }))
            .ToBody();

        byte[] v3Body = PositionFrameBlobMigration.FrameV2ToV3(v2Body);

        var reader = new SnapshotBlobReader(v3Body, id => BuiltinBlobLayout.PayloadLength(id, MoveProtocol.WireProtocolVersion));
        List<SnapshotBlobComponent> kept = reader.Entities[0].Components.Where(c => c.IsExtension).ToList();
        Assert.Single(kept);
        Assert.Equal(extension, kept[0].Payload);
    }

    [Fact]
    public void FrameV2ToV3_BodyThatWalksAtNoGeneration_Throws()
    {
        // One entity whose movement payload is a length no generation ever wrote, so every candidate runs off the end
        // of the buffer or lands on a type id that is not a component. Undecodable -> the driver quarantines it.
        byte[] corrupt = new CellBlobFixtures.BodyBuilder()
            .Entity(1,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(4, PlayerPos)),
                (MoveProtocol.MovementTypeId, new byte[17]))
            .ToBody();

        Assert.Throws<InvalidOperationException>(() => PositionFrameBlobMigration.FrameV2ToV3(corrupt));
    }

    [Fact]
    public void FrameV2ToV3_TruncatedBody_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => PositionFrameBlobMigration.FrameV2ToV3(new byte[] { 1, 0, 0, 0, 5, 0 }));
    }
}
