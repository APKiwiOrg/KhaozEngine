using System;
using System.Linq;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainProofBoundaryTests(ContactTerrainPolicyFixture fixture) : IClassFixture<ContactTerrainPolicyFixture>
{
    [Fact]
    public void ARemoteSameHeightFloorIsOutsideTheLandingFringe()
    {
        Assert.True(fixture.Prove().Accepted);
        var link = fixture.Link with { ToX = fixture.Link.ToX + 4 };
        var grid = fixture.Navigation.Space.Layers[link.ToLayer];
        Assert.Equal(grid.RestingHeightAt(fixture.Link.ToX, link.ToZ), grid.RestingHeightAt(link.ToX, link.ToZ));
        var refused = fixture.Prove(link: link);
        Assert.False(refused.Accepted);
        Assert.Equal(ContactTerrainDropRefusal.Certificate, refused.Reason);
    }

    [Fact]
    public void AnUnboundRawLayerCannotBorrowTheSameRestingAnchor()
    {
        var space = fixture.Navigation.Space;
        // Reuse the captured immutable grid. Only the logical layer address changes, never any anchor float.
        var layers = space.Layers.Append(space.Layers[fixture.Link.ToLayer]).ToArray();
        var addressed = new NavSpace(layers, space.Links);
        Assert.True(fixture.Prove(space: addressed).Accepted);
        var refused = fixture.Prove(space: addressed, link: fixture.Link with { ToLayer = layers.Length - 1 });
        Assert.False(refused.Accepted);
        Assert.Equal(ContactTerrainDropRefusal.Certificate, refused.Reason);
    }

    [Fact]
    public void SlowLaunchExhaustsTheSamePhysicalDuration()
    {
        Assert.True(fixture.Prove().Accepted);
        var refused = fixture.Prove(tuning: ContactTerrainScene.Tuning with { WalkSpeed = 0.1f });
        Assert.False(refused.Accepted);
        Assert.Equal(ContactTerrainDropRefusal.Budget, refused.Reason);
        Assert.Equal(fixture.Capture.Options.MaxEdgeProbeSteps, refused.Steps.Count);
    }
}
