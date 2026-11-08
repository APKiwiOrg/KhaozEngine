using System.Linq;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainCurvedCaptureTests
{
    // #1697 F4. A represented standable prop away from the rim must retain its original raw rows.
    [Theory]
    [InlineData(PhysicsEvidenceKind.Sphere, false, false)]
    [InlineData(PhysicsEvidenceKind.Capsule, false, false)]
    [InlineData(PhysicsEvidenceKind.Cylinder, false, false)]
    [InlineData(PhysicsEvidenceKind.Cylinder, true, false)]
    [InlineData(PhysicsEvidenceKind.Cylinder, false, true)]
    public void CurvedRawSupportBindsWithoutInventingAPlanarFace(PhysicsEvidenceKind kind, bool inCorridor, bool compound)
    {
        float x = inCorridor ? 2.625f : 4.125f;
        using var scene = new ContactTerrainScene(4, extra: (world, builder) =>
        {
            PhysicsShape shape = kind switch
            {
                PhysicsEvidenceKind.Sphere => new SphereShape(0.35f),
                PhysicsEvidenceKind.Capsule => new CapsuleShape(0.35f, 1),
                _ => new CylinderShape(0.35f, 1)
            };
            if (compound) shape = new CompoundShape([
                new(new BoxShape(new(0.1f)), Pose.At(new(0.75f, 0, 0))),
                new(shape, Pose.Identity)]);
            var handle = world.AddStatic(shape, Pose.At(new(x, 1, -1.375f)));
            builder.Add(handle, 0, int.MaxValue, new("world", "curved-prop"), new(x - 0.35f, 0, -1.725f), new(x + 1, 2, -1.025f));
        });
        var options = new PhysicsNavBakeOptions(0, -3, 5, 0, 0.25f, 10, 14,
            ContactTerrainScene.Tuning.MaxSlopeRadians, 256, 2048);
        using var capture = PhysicsNavBake.CaptureDescent(scene.Context, options, _ => 0);
        var evidence = capture.DescentEvidence!;
        int cx = (int)(x / options.CellSize), cz = (int)((-1.375f - options.MinZ) / options.CellSize);
        var top = capture.Columns.GetColumn(cx, cz).ToArray().MaxBy(s => s.Height);
        Assert.True(top.Height > 0);
        Assert.True(evidence.TryRaw(cx, cz, System.BitConverter.SingleToInt32Bits(top.Height), out var raw));
        Assert.Equal(kind, evidence.Face(raw.Face).Kind);
        Assert.True(evidence.Face(raw.Face).Points.IsEmpty);
        var footprint = new NavAreaFootprint(capture.Columns, options, ContactTerrainScene.Tuning, default);
        Assert.Equal(!inCorridor, new ContactTerrainDropCorridor(evidence, footprint,
            ContactTerrainScene.Tuning, scene.From, scene.To, 6).Certify());
    }
}
