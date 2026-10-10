using System;
using System.Numerics;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Installed terrain chunks against the shipped backend: the capsule feature query sees every chunk, and
/// chunk boundaries inside one patch leave neither gaps nor duplicate contact.</summary>
public class MapTerrainSeamTests
{
    [Fact]
    public void FeatureQuery_IsCompleteOnLongStripsHighTerrainAndBothStripSides()
    {
        // One buffer for every query. CA2014 refuses a stackalloc inside the loop.
        Span<CapsuleIncidentFace> faces = stackalloc CapsuleIncidentFace[256];
        foreach (var f in new[] { NativeWorldFixtures.LongWallStrip(300f), NativeWorldFixtures.HighLegacyPatch(180f), NativeWorldFixtures.TwoSidedStrip() })
        {
            var set = MapTerrainPhysics.Compile(f.View, new MapTerrainChunkPolicy());
            using var physics = new BepuPhysicsWorld();
            foreach (var c in set.Chunks)
            {
                var handle = physics.AddStatic(c.Shape, new Pose(new Vector3(c.Anchor.X, c.Anchor.Y, c.Anchor.Z), Quaternion.Identity));
                using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
                var probe = f.ProbeNear(c);
                var result = ((IPhysicsCapsuleFeatures)physics).QueryCapsuleFeature(lease, handle, new CapsuleShape(0.2f, 0.4f), probe, 0.01f, faces);
                Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
            }
        }
    }

    [Fact]
    public void ChunkSeams_HaveCompleteContactWithoutGapsOrDuplicates()
    {
        var f = NativeWorldFixtures.FinePatch();
        var set = MapTerrainPhysics.Compile(f.View, new MapTerrainChunkPolicy(64));
        using var physics = new BepuPhysicsWorld();
        foreach (var c in set.Chunks)
            physics.AddStatic(c.Shape, new Pose(new Vector3(c.Anchor.X, c.Anchor.Y, c.Anchor.Z), Quaternion.Identity));
        foreach (var p in f.SeamProbePoints)
        {
            Assert.True(physics.Raycast(p + Vector3.UnitY * 5f, -Vector3.UnitY, 10f, out var hit));
            Assert.Equal(f.HeightAt(p.X, p.Z), hit.Point.Y, 4);
            Assert.True(physics.SweepCapsule(new CapsuleShape(0.3f, 0.6f), Pose.At(p + Vector3.UnitY * 3f), -Vector3.UnitY, 5f, out var sweep));
            Assert.Equal(f.HeightAt(p.X, p.Z), sweep.Point.Y, 3);
        }
    }
}
