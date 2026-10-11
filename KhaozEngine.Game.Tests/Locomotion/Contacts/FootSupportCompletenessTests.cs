using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class FootSupportCompletenessTests
{
    [Fact]
    public void HiddenCrateUnderSixtyDegreeWedge()
    {
        using SupportFootprintScene scene = SupportFootprintScenes.HiddenCrate(FootprintPartition.Boxes);
        double installedCrateTop = scene.Faces.Find(f => f.Static == scene.Crate && f.Normal.Y > 0)!.Vertices[0].Y;
        var query = new FootSupportQuery(Vector2.Zero, 0, 0.2f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4));
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        SupportSample sample = FootSupport.Find(null, null, scene.World, lease, query);
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.Equal(scene.Crate, sample.Static);
        Assert.True(Math.Abs(sample.Height - installedCrateTop) <= sample.HeightError);
        Assert.True(sample.HeightError <= 0.0005f);
    }
}
