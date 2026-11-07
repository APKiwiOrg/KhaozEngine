using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public partial class ContactTerrainDescentCaptureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BandExtentComesFromGeometryIncludingAnObliqueDescent(bool steep, bool oblique)
    {
        float height = steep ? 6 : 1;
        var doc = TileWorldMovementNavigationTests.Drawn(0, 0, 8, 12);
        for (int z = 0; z <= 12; z++) for (int x = 0; x <= 8; x++)
            doc.SetCornerHeightCm(x, z, 0, (short)MathF.Round(height * 100 * Math.Clamp((6 - x) / 5f, 0, 1)));
        using var scene = new ContactTerrainScene(height, document: doc, boundary: MovementBoundary.Rectangle(0, -12, 8, 0));
        var options = new PhysicsNavBakeOptions(0, -12, 8, 0, 0.25f, 10, 14, ContactTerrainScene.Tuning.MaxSlopeRadians, 2048, 8192);
        using var capture = PhysicsNavBake.CaptureDescent(scene.Context, options, _ => 0);
        var e = capture.DescentEvidence!;
        var nav = capture.BuildProfile(ContactTerrainScene.Tuning, default);
        var bindings = new ContactTerrainProfileEvidence(e, nav);
        var candidates = ContactTerrainDropCandidates.Generate(nav, e, bindings, ContactTerrainScene.Tuning, 6);
        if (!steep) { Assert.Empty(candidates); return; }
        Vector3 from = new(0.625f, height, -8.625f), to = new(6.375f, 0, oblique ? -2.875f : -8.625f);
        var grid = nav.Space.Layers[0]; var (sx, sz) = grid.CellOf(from.X, from.Z); var (tx, tz) = grid.CellOf(to.X, to.Z);
        int sl = Enumerable.Range(0, nav.Space.Layers.Count).Single(l => nav.Graph.Layers[l].IsAccepted(sx, sz));
        int tl = Enumerable.Range(0, nav.Space.Layers.Count).Single(l => nav.Graph.Layers[l].IsAccepted(tx, tz));
        Assert.Contains(new NavLink(sl, sx, sz, tl, tx, tz) { Kind = NavLinkKind.Drop }, candidates);
        Assert.True(new ContactTerrainDropCorridor(e, nav.Footprint, ContactTerrainScene.Tuning, from, to, 6).Certify());
    }
}
