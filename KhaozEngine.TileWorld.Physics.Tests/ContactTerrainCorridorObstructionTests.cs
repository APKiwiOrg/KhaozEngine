using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainCorridorObstructionTests
{
    // #1697 F1. An interior face can cross the body slab with every original vertex outside it.
    [Theory]
    [InlineData(-16f, 16f, false)]
    [InlineData(-1f, -0.5f, true)]
    [InlineData(7f, 9f, true)]
    public void ExactClippingDetectsInteriorSlabCrossings(float low, float high, bool expected)
    {
        using var scene = new ContactTerrainScene(4, extra: (world, builder) =>
        {
            Vector3[] vertices = [new(2.01f, low, -1.49f), new(2.08f, high, -1.48f), new(2.02f, high, -1.42f)];
            var handle = world.AddStatic(new TriangleMeshShape(vertices, [0, 1, 2]), Pose.Identity);
            builder.Add(handle, 0, 0, new("world", "crossing-face"), new(2.01f, low, -1.49f), new(2.08f, high, -1.42f));
        });
        var options = new PhysicsNavBakeOptions(0, -3, 4, 0, 0.25f, 10, 14,
            ContactTerrainScene.Tuning.MaxSlopeRadians, 256, 2048);
        using var capture = PhysicsNavBake.CaptureDescent(scene.Context, options, _ => 0);
        var footprint = new NavAreaFootprint(capture.Columns, options, ContactTerrainScene.Tuning, default);
        Assert.Equal(expected, new ContactTerrainDropCorridor(capture.DescentEvidence!, footprint,
            ContactTerrainScene.Tuning, scene.From, scene.To, 6).Certify());
    }
}
