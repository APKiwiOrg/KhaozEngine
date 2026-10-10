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
        var installed = world.Statics.Single(s => s.OwnerId == NativeWorldFixtures.LowerFloorChunkId);
        var mesh = Assert.IsType<TriangleMeshShape>(installed.Shape);
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
                Assert.True(TriangleContainsXz(mesh, faces[i].FaceId, at - installed.Position),
                    $"triangle {faces[i].FaceId} does not contain the probe at {at}");
            }
            if (result.Written == 2) Assert.NotEqual(faces[0].FaceId, faces[1].FaceId);
        }
    }

    [Fact]
    public void Dispose_RetriesAfterARefusedRemoval()
    {
        var fault = new NativeRegistrationFaultWorld(failOnRemove: 2);
        var r = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), fault);
        int installed = fault.LiveStaticCount;
        Assert.True(installed > 2);
        Assert.Single(Assert.Throws<AggregateException>(r.Dispose).InnerExceptions);
        Assert.Equal(1, fault.LiveStaticCount);
        Assert.Single(r.Handles);
        r.Dispose();
        Assert.Equal(0, fault.LiveStaticCount);
        Assert.Empty(r.Handles);
        Assert.Equal(installed, fault.Removed.Count);
    }

    [Fact]
    public void Rollback_RemovesInReverseOrder()
    {
        var fault = new NativeRegistrationFaultWorld(failOnAdd: 4);
        Assert.Throws<InvalidOperationException>(() => MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), fault));
        Assert.Equal(new[] { 3, 2, 1 }, fault.Removed);
        Assert.Equal(0, fault.LiveStaticCount);
    }

    [Fact]
    public void OriginBeyondTheWorldLimit_IsRefused()
    {
        var far = new NativeRegistrationFaultWorld(origin: new Vector3(0f, 0f, 2_000_000f));
        Assert.Contains("1,000,000 m", Assert.Throws<MapDocumentException>(
            () => MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), far)).Message);
        Assert.Equal(0, far.LiveStaticCount);
    }

    [Fact]
    public void TryFaceOwner_RefusesPlacementsOutOfRangeFacesAndForeignHandles()
    {
        var world = NativeWorldFixtures.BuildBridge();
        using var physics = new BepuPhysicsWorld();
        using var r = MapPhysicsRegistration.Register(world, physics);
        int placement = world.Statics.ToList().FindIndex(s => s.Kind == MapStaticKind.Placement);
        int terrain = world.Statics.ToList().FindIndex(s => s.Kind == MapStaticKind.TerrainChunk);
        Assert.False(r.TryFaceOwner(r.Handles[placement], 0, out _));
        Assert.True(r.TryFaceOwner(r.Handles[terrain], 0, out _));
        Assert.False(r.TryFaceOwner(r.Handles[terrain], world.Statics[terrain].TriangleOwners.Count, out _));
        Assert.False(r.TryFaceOwner(r.Handles[terrain], -1, out _));
        var foreign = new StaticHandle(-1);
        Assert.DoesNotContain(foreign, r.Handles);
        Assert.False(r.TryFaceOwner(foreign, 0, out _));
        Assert.False(r.TryOwner(foreign, out _));
    }

    [Fact]
    public void RebaseWithAHeightOffset_StillHitsTheFloorAtWorldZero()
    {
        using var physics = new BepuPhysicsWorld();
        physics.Rebase(new Vector3(64, 32, -64));
        using var r = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), physics);
        Assert.True(physics.Raycast(NativeWorldFixtures.InLowerRoom - physics.Origin, -Vector3.UnitY, 10f, out var hit));
        Assert.Equal(0f, hit.Point.Y + physics.Origin.Y, 4);
    }

    // Whether triangle t of the mesh contains the XZ of a point in the mesh's own frame, edges included.
    static bool TriangleContainsXz(TriangleMeshShape mesh, int t, Vector3 p)
    {
        Vector3 a = mesh.Vertices[mesh.Indices[3 * t]], b = mesh.Vertices[mesh.Indices[3 * t + 1]],
            c = mesh.Vertices[mesh.Indices[3 * t + 2]];
        static float Side(Vector3 u, Vector3 v, Vector3 q) => (v.X - u.X) * (q.Z - u.Z) - (v.Z - u.Z) * (q.X - u.X);
        float ab = Side(a, b, p), bc = Side(b, c, p), ca = Side(c, a, p);
        const float tolerance = 1e-5f;
        return (ab >= -tolerance && bc >= -tolerance && ca >= -tolerance) ||
            (ab <= tolerance && bc <= tolerance && ca <= tolerance);
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
