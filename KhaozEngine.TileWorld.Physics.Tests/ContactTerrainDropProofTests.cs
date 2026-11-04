using System.Linq;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainDropProofTests
{
    // #1697. Acceptance must use the bake proof, including both real modes and their terminal Holds.
    [Theory]
    [InlineData(1.3f)]
    [InlineData(4f)]
    [InlineData(6f)]
    public void TerrainCertificateProvesTheNamedAnchorWithoutPromotingTheFringe(float height)
    {
        using var scene = new ContactTerrainScene(height);
        var options = new PhysicsNavBakeOptions(0, -3, 4, 0, 0.25f, 10, 14,
            ContactTerrainScene.Tuning.MaxSlopeRadians, 256, 1024);
        using var capture = PhysicsNavBake.CaptureDescent(scene.Context, options, _ => 0);
        var nav = capture.BuildProfile(ContactTerrainScene.Tuning, default);
        var evidence = capture.DescentEvidence!;
        var sources = new ContactTerrainProfileEvidence(evidence, nav);
        var grid = nav.Space.Layers[0];
        var (sx, sz) = grid.CellOf(scene.From.X, scene.From.Z);
        var (tx, tz) = grid.CellOf(scene.To.X, scene.To.Z);
        int from = Enumerable.Range(0, nav.Space.Layers.Count).Single(i => nav.Graph.Layers[i].IsAccepted(sx, sz));
        int to = Enumerable.Range(0, nav.Space.Layers.Count).Single(i => nav.Graph.Layers[i].IsAccepted(tx, tz));
        var link = new NavLink(from, sx, sz, to, tx, tz) { Kind = NavLinkKind.Drop };
        using var binding = scene.Context.CreateNavigationBinding(ContactTerrainScene.Tuning);
        using var proof = new ContactNavigationProof(capture, scene.Context, binding, nav.Footprint, maxDropMetres: 6);
        Assert.False(proof.TryDrop(nav.Space, link));
        proof.EnableTerrainDescents(evidence, sources);
        Assert.True(proof.TryDrop(nav.Space, link));
        Assert.False(nav.AllowsSegment(scene.From, scene.To));
        Assert.DoesNotContain(nav.Graph.Links, l => l.Kind == NavLinkKind.Drop);
    }
}
