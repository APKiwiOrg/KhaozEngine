using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class FootSupportFiniteContactTests
{
    [Theory]
    [InlineData(0.15f, false, false)]
    [InlineData(0.3f, false, true)]
    [InlineData(0.075f, false, false)]
    [InlineData(0.3f, true, false)]
    public void FiniteTopEdgeOffersCompleteSupport(float drop, bool lowerFloor, bool steep)
    {
        using var scene = new FootSupportScene(SceneVariant.Mesh).Mesh("edge", FootSupportScene.Quad(
            new(0.15f, 0, -0.5f), new(0.3f, -drop, -0.5f),
            new(0.15f, 0, 0.5f), new(0.3f, -drop, 0.5f)));
        if (lowerFloor)
        {
            // A convex steep ridge caps both planes at -0.3 on the axis. The separate floor at -0.2 wins.
            scene.Mesh("cap", FootSupportScene.Quad(new(0.1f, -0.1f, -0.5f), new(0.15f, 0, -0.5f),
                new(0.1f, -0.1f, 0.5f), new(0.15f, 0, 0.5f)));
            scene.Flat("lower", -1, 1, -1, 1, -0.2f);
        }
        var query = new FootSupportQuery(Vector2.Zero, 0, 0.2f, 0.125f, 0.4f, MathF.Cos(MathF.PI / 4));

        SupportSample sample = FootSupport.Find(null, null, scene.World, scene.Lease, query);

        SupportStatus status = steep ? SupportStatus.Steep : SupportStatus.Walkable;
        Assert.Equal(status, sample.Status);
        Assert.Equal(scene[lowerFloor ? "lower" : "edge"], sample.Static);
        // On the isolated edge p = drop and m = 0, so min(p, m) is 0. The floor beats the capped ridge.
        double height = lowerFloor ? (double)-0.2f : 0;
        Assert.True(Math.Abs(sample.Height - height) <= sample.HeightError, $"{sample}");
        Assert.True(sample.HeightError <= 0.0005f);
        Assert.Equal(status == SupportStatus.Walkable, sample.Normal.Y >= query.CosMaxSlope);
    }
}
