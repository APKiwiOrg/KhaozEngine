using System;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainOptInTests
{
    [Theory]
    [InlineData(1.3f)]
    [InlineData(4f)]
    [InlineData(6f)]
    public void PublicFlagsPublishTheOriginalTerrainDropOnlyForTheEnabledProfile(float height)
    {
        using var scene = new ContactTerrainScene(height);
        var options = Options with { CaptureTerrainDescentEvidence = true };
        using var capture = PhysicsNavBake.Capture(scene.Context, options, _ => 0);
        Assert.NotNull(capture.DescentEvidence);
        var ordinary = capture.BuildProfile(ContactTerrainScene.Tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.DoesNotContain(ordinary.Graph.Links, link => link.Kind == NavLinkKind.Drop);
        var terrain = capture.BuildProfile(ContactTerrainScene.Tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6, AllowTerrainDescents = true });
        var grid = terrain.Space.Layers[0];
        var (sx, sz) = grid.CellOf(scene.From.X, scene.From.Z);
        var (tx, tz) = grid.CellOf(scene.To.X, scene.To.Z);
        Assert.Contains(terrain.Graph.Links, link => link.Kind == NavLinkKind.Drop && link.FromX == sx &&
            link.FromZ == sz && link.ToX == tx && link.ToZ == tz);
        Assert.False(terrain.AllowsSegment(scene.From, scene.To));
    }

    [Fact]
    public void TerrainProfileCannotBorrowMissingCaptureEvidenceOrZeroPermission()
    {
        using var scene = new ContactTerrainScene(4);
        using var capture = PhysicsNavBake.Capture(scene.Context, Options, _ => 0);
        Assert.Throws<PhysicsNavDescentEvidenceException>(() => capture.BuildProfile(ContactTerrainScene.Tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6, AllowTerrainDescents = true }));
        var refusal = Assert.Throws<ArgumentException>(() => capture.BuildProfile(ContactTerrainScene.Tuning, default,
            new GroundProfileOptions { AllowTerrainDescents = true }));
        Assert.Equal(nameof(GroundProfileOptions.AllowTerrainDescents), refusal.ParamName);
    }
    internal static PhysicsNavBakeOptions Options => new(0, -3, 5, 0, 0.25f, 10, 14,
        ContactTerrainScene.Tuning.MaxSlopeRadians, 256, 1024);
}
