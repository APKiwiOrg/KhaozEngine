using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using static KhaozEngine.Tests.TileWorld.Physics.TileWorldPhysicsTestData;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]  // its zero-alloc assertion must not run alongside the GC-churning parallel tests
public class TileSamplerAllocTests
{
    [Fact]
    public void SamplingAllocatesNothing()
    {
        TileWorldDocument doc = BridgedRiverWorld();
        doc.SetUnderlay(40, 40, 0, 0);
        TileWorldColliders colliders = TileWorldColliders.Build(doc, Catalogs());
        // Drawn ground, the river, a void tile and off the world.
        (float X, float Z)[] points = { (3.3f, -1.7f), (5.5f, -11.5f), (40.5f, -40.5f), (-9f, 99f) };
        Func<float, float, float> height = colliders.Ground.HeightDelegate;
        Func<float, float, Vector3> normal = colliders.Ground.NormalDelegate;
        Func<float, float, float, MovementMedium> medium = colliders.Medium.MediumDelegate;
        float sink = 0f;
        for (int warm = 0; warm < 2; warm++)
            foreach ((float x, float z) in points)
                sink += height(x, z) + normal(x, z).Y + medium(x, z, -0.5f).WaterSurfaceY;

        AllocAssert.NoPerCallAllocation("the ground and medium samplers", () =>
        {
            for (int i = 0; i < 100; i++)
                foreach ((float x, float z) in points)
                    sink += height(x, z) + normal(x, z).Y + medium(x, z, -0.5f).WaterSurfaceY;
        });

        Assert.True(float.IsFinite(sink));
    }
}
