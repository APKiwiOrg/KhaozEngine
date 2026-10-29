using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropFootprintTests
{
    [Theory]
    [InlineData("forbidden")]
    [InlineData("missing")]
    [InlineData("deep")]
    [InlineData("off-grid")]
    public void CompleteProjectedCorridorMustHaveEligibleNextSupport(string fault)
    {
        PhysicsNavBakeOptions options = ContactDropFixture.Options;
        var starts = new List<int>();
        var surfaces = new List<PhysicsNavSurface>();
        for (int z = 0; z < 3; z++)
            for (int x = 0; x < 6; x++)
            {
                starts.Add(surfaces.Count);
                if (fault == "missing" && x == 3) continue;
                surfaces.Add(new(fault == "deep" && x == 3 ? -3 : 0, float.PositiveInfinity, 0));
                if (x < 3) surfaces.Add(new(4, float.PositiveInfinity, 0));
                if (fault == "forbidden" && x == 3) surfaces.Add(new(2, float.PositiveInfinity, 1));
            }
        starts.Add(surfaces.Count);
        var columns = new PhysicsNavColumns(options, 6, 3, starts.ToArray(), surfaces.ToArray(), []);
        var tuning = ContactDropFixture.Tuning;
        var ground = new NavAreaFootprint(columns, options, tuning, new(0, 1));
        Vector3 from = new(-0.25f, 4, 0), to = new(fault == "off-grid" ? 1.4f : 0.25f, 0, 0);
        Assert.False(new NavDropFootprint(ground, tuning, from, to, 6).Certify());
    }

    [Fact]
    public void AirMayBeHighAboveTheNextEligibleSurfaceWithoutRelaxingGroundFootprints()
    {
        using var fixture = new ContactDropFixture();
        var ground = new NavAreaFootprint(fixture.Capture.Columns, ContactDropFixture.Options, ContactDropFixture.Tuning, default);
        var corridor = new NavDropFootprint(ground, ContactDropFixture.Tuning, new(-0.25f, 4, 0), new(0.25f, 0, 0), 6);
        Assert.True(corridor.Certify());
        Assert.True(corridor.Contains(new(0.25f, 3, 0)));
        Assert.False(ground.Accepts(new(0.25f, 3, 0)));
        Assert.False(corridor.Contains(new(0.25f, -3, 0)));
        Assert.False(corridor.Contains(new(0.25f, 3, 0.3f)));
    }

    [Fact]
    public void ProposedSweepCanFindSupportButAcceptedRouteCannotExceedTheDepthBudget()
    {
        using var fixture = new ContactDropFixture(6);
        var ground = new NavAreaFootprint(fixture.Capture.Columns, ContactDropFixture.Options, ContactDropFixture.Tuning, default);
        var corridor = new NavDropFootprint(ground, ContactDropFixture.Tuning, new(-0.25f, 6, 0), new(0.25f, 0, 0), 6);
        Assert.True(corridor.Certify());
        MovementBoundary boundary = corridor.Compose(MovementBoundary.Rectangle(-20, -20, 20, 20), fixture.Context.NavigationFrame);
        Vector3 start = new(0.25f, 0.85f, 0), proposed = new(0.25f, 0.65f, 0), supported = new(0.25f, 0.75f, 0);
        Assert.Equal(MovementAvailability.Known, boundary.ProveSegment(start, proposed - start, proposed, out bool query));
        Assert.True(query);
        var route = Assert.IsAssignableFrom<IMovementRouteBoundary>(boundary);
        Assert.Equal(MovementAvailability.Known, route.ProveRouteSegment(start, supported - start, supported, out bool landing));
        Assert.True(landing);
        Assert.Equal(MovementAvailability.Known, route.ProveRouteSegment(start, proposed - start, proposed, out bool tooDeep));
        Assert.False(tooDeep);
    }
}
