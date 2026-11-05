using System;
using System.Linq;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public partial class ContactTerrainDescentCaptureTests
{
    [Theory]
    [InlineData(1.3f, true)]
    [InlineData(4f, true)]
    [InlineData(6f, true)]
    [InlineData(8f, false)]
    public void PhysicalTerrainEvidenceFindsTheAdmittedRimWithoutChangingSharpDefaults(float height, bool permitted)
    {
        using var scene = new ContactTerrainScene(height);
        var options = new PhysicsNavBakeOptions(0, -3, 4, 0, 0.25f, 10, 14, ContactTerrainScene.Tuning.MaxSlopeRadians, 256, 1024);
        using var capture = PhysicsNavBake.CaptureDescent(scene.Context, options, _ => 0);
        var evidence = Assert.IsType<PhysicsNavDescentEvidence>(capture.DescentEvidence);
        var nav = capture.BuildProfile(ContactTerrainScene.Tuning, default);
        var sources = new ContactTerrainProfileEvidence(evidence, nav);
        Assert.True(sources.Count > 0);
        Assert.Empty(ContactDropCandidates.Generate(nav.Space.Layers, nav.Graph.Layers, ContactTerrainScene.Tuning, 6));
        var grid = nav.Space.Layers[0];
        var (sx, sz) = grid.CellOf(scene.From.X, scene.From.Z);
        var (tx, tz) = grid.CellOf(scene.To.X, scene.To.Z);
        int from = Enumerable.Range(0, nav.Space.Layers.Count).Single(i => nav.Graph.Layers[i].IsAccepted(sx, sz));
        int to = Enumerable.Range(0, nav.Space.Layers.Count).Single(i => nav.Graph.Layers[i].IsAccepted(tx, tz));
        var expected = new NavLink(from, sx, sz, to, tx, tz) { Kind = NavLinkKind.Drop };
        var links = ContactTerrainDropCandidates.Generate(nav, evidence, sources, ContactTerrainScene.Tuning, 6);
        if (permitted)
        {
            Assert.Contains(expected, links);
            Assert.Equal(expected, Assert.Single(ContactTerrainDropCandidates.Generate(nav, evidence, sources, ContactTerrainScene.Tuning, 6, (from, sx, sz))));
        }
        else Assert.DoesNotContain(expected, links);
        Assert.Equal(permitted, new ContactTerrainDropCorridor(evidence, nav.Footprint, ContactTerrainScene.Tuning, ContactDropProof.Feet(nav.Space.Layers[from], sx, sz), ContactDropProof.Feet(nav.Space.Layers[to], tx, tz), 6).Certify());
    }
}
