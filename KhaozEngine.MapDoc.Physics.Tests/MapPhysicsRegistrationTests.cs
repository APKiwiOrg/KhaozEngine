using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Installing a built world's statics into a physics world, mapping them back to their owners and faces, and
/// the legacy-only move context.</summary>
public class MapPhysicsRegistrationTests
{
    [Fact]
    public void Registration_RebasedOriginAndFaultsLeakNothing()
    {
        var world = NativeWorldFixtures.BuildStackedCave();
        using (var physics = new BepuPhysicsWorld())
        {
            physics.Rebase(new Vector3(64, 0, -64));
            using var r = MapPhysicsRegistration.Register(world, physics);
            Assert.True(physics.Raycast(NativeWorldFixtures.InLowerRoom - physics.Origin, -Vector3.UnitY, 10f, out var hit));
            Assert.Equal(0f, hit.Point.Y + physics.Origin.Y, 4);
        }
        var fault = new NativeRegistrationFaultWorld(failOnAdd: 3);
        Assert.Throws<InvalidOperationException>(() => MapPhysicsRegistration.Register(world, fault));
        Assert.Equal(0, fault.LiveStaticCount);
        var offOrigin = new NativeRegistrationFaultWorld(origin: new Vector3(0.5f, 0, 0));
        Assert.Contains("whole-metre origin", Assert.Throws<MapDocumentException>(() => MapPhysicsRegistration.Register(world, offOrigin)).Message);
    }

    [Fact]
    public void StackedCave_SidednessHoldsInTheBackend()
    {
        using var physics = new BepuPhysicsWorld();
        using var r = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), physics);
        Assert.True(physics.Raycast(NativeWorldFixtures.InLowerRoom, Vector3.UnitY, 10f, out var ceiling));
        Assert.Equal(3f, ceiling.Point.Y, 4);
        Assert.True(physics.Raycast(NativeWorldFixtures.InLowerRoom, -Vector3.UnitY, 10f, out var floor));
        Assert.Equal(0f, floor.Point.Y, 4);
        Assert.True(physics.Raycast(NativeWorldFixtures.InShaftBelowUpperFloor, Vector3.UnitY, 10f, out var shaftTop));
        Assert.Equal(6.5f, shaftTop.Point.Y, 4);
    }

    [Fact]
    public void FeatureQueryFaces_MapToCanonicalOwners()
    {
        var world = NativeWorldFixtures.BuildStackedCave();
        using var physics = new BepuPhysicsWorld();
        using var r = MapPhysicsRegistration.Register(world, physics);
        var chunk = r.Handles.First(h => r.TryOwner(h, out var o) && o.Kind == MapStaticKind.TerrainChunk && o.OwnerId == NativeWorldFixtures.LowerFloorChunkId);
        var expected = world.Terrain.Chunks.Single(c => c.ChunkId == NativeWorldFixtures.LowerFloorChunkId);
        Span<CapsuleIncidentFace> faces = stackalloc CapsuleIncidentFace[256];
        foreach (var at in new[] { NativeWorldFixtures.InsideOneLowerFloorTriangle, NativeWorldFixtures.OnLowerFloorTriangleEdge })
        {
            using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
            var result = ((IPhysicsCapsuleFeatures)physics).QueryCapsuleFeature(lease, chunk, new CapsuleShape(0.2f, 0.4f), Pose.At(at + new Vector3(0, 0.405f, 0)), 0.01f, faces);
            Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
            Assert.Equal(at == NativeWorldFixtures.OnLowerFloorTriangleEdge ? 2 : 1, result.Written);
            for (int i = 0; i < result.Written; i++)
            {
                Assert.True(r.TryFaceOwner(chunk, faces[i].FaceId, out var face));
                Assert.Equal(expected.TriangleOwners[faces[i].FaceId], face);
                Assert.Equal(MapFaceRole.SupportFloor, expected.TriangleRoles[faces[i].FaceId]);
            }
        }
    }

    [Fact]
    public void BridgeDeck_OverhangsSupportAndParapetsBlock()
    {
        using var physics = new BepuPhysicsWorld();
        using var r = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildBridge(), physics);
        foreach (float x in new[] { -8.75f, 8.75f })
        {
            Assert.True(physics.Raycast(new Vector3(x, 10f, 0f), -Vector3.UnitY, 20f, out var deck));
            Assert.Equal(2.825f, deck.Point.Y, 4);
        }
        Assert.True(physics.Raycast(NativeWorldFixtures.OnDeckFacingParapet, Vector3.UnitZ, 3f, out var parapet));
        Assert.True(r.TryOwner(parapet.Body!.Value, out var owner) && owner.OwnerId.StartsWith("parapet-", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyMoveContext_IsLegacyOnly()
    {
        using var physics = new BepuPhysicsWorld();
        using var native = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), physics);
        Assert.Contains("contact controller", Assert.Throws<MapDocumentException>(() => native.CreateLegacyMoveContext()).Message);
        using var legacyPhysics = new BepuPhysicsWorld();
        using var legacy = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildLegacySlope(), legacyPhysics);
        Assert.Same(legacyPhysics, legacy.CreateLegacyMoveContext().Physics);
    }
}
