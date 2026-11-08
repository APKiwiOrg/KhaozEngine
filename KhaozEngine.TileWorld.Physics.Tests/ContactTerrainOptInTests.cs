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
        var retained = terrain.Terrain!.Evidence;
        var excluded = Enumerable.Range(0, retained.Width * retained.Height).First(i =>
            retained.StoredCoverage(i % retained.Width, i / retained.Width) == DescentCoverage.Excluded);
        var missing = Assert.Throws<PhysicsNavDescentEvidenceException>(() => retained.Column(excluded % retained.Width, excluded / retained.Width));
        Assert.Equal(PhysicsNavDescentEvidenceReason.DependencyMissing, missing.Reason);
    }

    [Fact]
    public void TerrainProfileCannotBorrowMissingCaptureEvidenceOrZeroPermission()
    {
        using var scene = new ContactTerrainScene(4);
        using var capture = PhysicsNavBake.Capture(scene.Context, Options, _ => 0);
        Assert.Throws<PhysicsNavDescentEvidenceException>(() => capture.BuildProfile(ContactTerrainScene.Tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6, AllowTerrainDescents = true }));
        using var withEvidence = PhysicsNavBake.Capture(scene.Context, Options with { CaptureTerrainDescentEvidence = true }, _ => 0);
        var refusal = Assert.Throws<NavigationProfileException>(() => withEvidence.BuildProfile(ContactTerrainScene.Tuning, default,
            new GroundProfileOptions { AllowTerrainDescents = true }));
        Assert.Equal(nameof(GroundProfileOptions.AllowTerrainDescents), refusal.ParamName);
        Assert.Equal(NavigationProfileRefusal.TerrainDropPermissionRequired, refusal.Reason);
        Assert.Equal(true, refusal.AllowTerrainDescents);
        Assert.Equal(0f, refusal.MaxDropMetres);
        Assert.True(withEvidence.BuildProfile(ContactTerrainScene.Tuning, default,
            new GroundProfileOptions { MaxDropMetres = 1, AllowTerrainDescents = true }).AllowTerrainDescents);
    }
    internal static PhysicsNavBakeOptions Options => new(0, -3, 5, 0, 0.25f, 10, 14,
        ContactTerrainScene.Tuning.MaxSlopeRadians, 256, 1024);
}
