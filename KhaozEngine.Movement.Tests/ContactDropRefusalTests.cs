using System;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropRefusalTests
{
    [Fact]
    public void ActualMomentumAndBrakingCanCompleteTheCertifiedFall()
    {
        var tuning = ContactDropFixture.Tuning with { AirMomentum = true, AirBrakeAccel = 100 };
        using var fixture = new ContactDropFixture(tuning: tuning);
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.Contains(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop);
    }

    [Theory]
    [InlineData("gravity")]
    [InlineData("terminal")]
    [InlineData("zero-pace")]
    [InlineData("budget")]
    [InlineData("momentum")]
    public void UnfinishedRealFallCannotPublish(string fault)
    {
        MoveTuning tuning = fault switch
        {
            "gravity" => ContactDropFixture.Tuning with { Gravity = 0.01f },
            "terminal" => ContactDropFixture.Tuning with { MaxFallSpeed = 0.01f },
            "zero-pace" => ContactDropFixture.Tuning with { WalkSpeed = 0, RunSpeed = 0 },
            "momentum" => ContactDropFixture.Tuning with { AirMomentum = true, AirBrakeAccel = 0 },
            _ => ContactDropFixture.Tuning,
        };
        var options = fault == "budget" ? ContactDropFixture.Options with { MaxEdgeProbeSteps = 1 } : ContactDropFixture.Options;
        using var fixture = new ContactDropFixture(tuning: tuning, options: options);
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.DoesNotContain(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullShellMustClearRoofAndWall(bool roof)
    {
        using var fixture = new ContactDropFixture(install: (world, builder) =>
        {
            if (roof) ContactNavigationFixture.Box(world, builder, "roof", new(0.05f, 4.8f, -1), new(0.5f, 5, 1));
            else ContactNavigationFixture.Box(world, builder, "wall", new(0, 0, -1), new(0.1f, 6, 1));
        });
        if (!roof)
        {
            var refused = Assert.Throws<InvalidOperationException>(() => fixture.Capture.BuildProfile(ContactDropFixture.Tuning,
                default, new GroundProfileOptions { MaxDropMetres = 6 }));
            Assert.Contains("EnvironmentUnresolved", refused.Message, StringComparison.Ordinal);
            return;
        }
        var nav = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.DoesNotContain(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop &&
            nav.Space.Layers[link.FromLayer].RestingHeightAt(link.FromX, link.FromZ) == 4 &&
            nav.Space.Layers[link.ToLayer].RestingHeightAt(link.ToX, link.ToZ) == 0);
    }

    [Theory]
    [InlineData(WaterTraversalMode.DryOnly, false)]
    [InlineData(WaterTraversalMode.WadeOnly, false)]
    [InlineData(WaterTraversalMode.SurfaceSwimmer, false)]
    [InlineData(WaterTraversalMode.SurfaceSwimmer, true)]
    [InlineData(WaterTraversalMode.WadeOnly, true)]
    public void OnlyNonSwimmingLandingsAreAdmitted(WaterTraversalMode mode, bool dry)
    {
        var tuning = ContactDropFixture.Tuning with { Water = new(mode) };
        var water = dry ? null : MovementWaterField.Level("world", 2, -10);
        using var fixture = new ContactDropFixture(tuning: tuning, water: water);
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.Equal(dry, nav.Graph.Links.Any(link => link.Kind == NavLinkKind.Drop));
    }

    [Fact]
    public void LandingOnAnUpperDeckCannotProveTheLowerNodeInTheSameColumn()
    {
        using var fixture = new ContactDropFixture(install: (world, builder) =>
            ContactNavigationFixture.Box(world, builder, "deck", new(0, 1.8f, -1), new(1, 2, 1)));
        var ground = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default);
        int fromLayer = -1, toLayer = -1;
        for (int layer = 0; layer < ground.Space.Layers.Count; layer++)
        {
            if (ground.Space.Layers[layer].RestingHeightAt(2, 1) == 4) fromLayer = layer;
            if (ground.Space.Layers[layer].RestingHeightAt(3, 1) == 0) toLayer = layer;
        }
        Assert.NotEqual(-1, fromLayer);
        Assert.NotEqual(-1, toLayer);
        using var source = fixture.Context.CreateNavigationBinding(ContactDropFixture.Tuning);
        using var proof = new ContactNavigationProof(fixture.Capture, fixture.Context, source,
            ground.Footprint, maxDropMetres: 6);
        Assert.False(proof.TryDrop(ground.Space, new NavLink(fromLayer, 2, 1, toLayer, 3, 1) { Kind = NavLinkKind.Drop }));
    }
}
