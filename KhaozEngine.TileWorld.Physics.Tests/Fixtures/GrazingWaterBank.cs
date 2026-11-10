using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

internal static class GrazingWaterBank
{
    internal static readonly MovementSpaceKey Space = new("water-world", "ordinary");
    internal static readonly MovementQueryIdentity Identity = new("grazing-bank", 1, "captured");

    internal static MoveTuning Tuning => new(4, 6, 0.75f, MathF.PI / 4, CapsuleRadius: 0.3f,
        TractionHysteresisRadians: 0)
    {
        Controller = MovementControllerKind.Contact,
        StepHeight = 0.4f,
        FootRadiusFraction = 0.5f,
        SwimSpeed = 3,
        SwimBuoyancyStiffness = 8,
        SwimEnterDepthFraction = 0.65f,
        SwimExitDepthFraction = 0.55f,
        SwimSurfaceSubmersionFraction = 0.6f,
        Water = new(WaterTraversalMode.SurfaceSwimmer),
    };

    internal static TileWorldDocument Document()
    {
        var doc = TileWorldPhysicsTestData.FlatWorld(new RegionCoord(1, 3));
        for (int z = 236; z <= 238; z++)
            for (int x = 110; x <= 111; x++)
                doc.SetCornerHeightCm(x, z, 0, (short)(z == 236 ? -35 : -130));
        doc.SetUnderlay(110, 236, 0, TileWorldPhysicsTestData.Water);
        doc.SetUnderlay(110, 237, 0, TileWorldPhysicsTestData.Water);
        return doc;
    }

    internal static TileWorldColliders Capture() =>
        TileWorldColliders.Build(Document(), TileWorldPhysicsTestData.Catalogs(), Space.WorldId);

    internal static MovementMediumSweepQuery Lift(float x = 110.375f) =>
        new(new(new(x, -0.45606077f, -236.79904f), 0.3f, 0.75f, Space, null), new(0, 0.096971154f, 0));
}
