using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainBandGapTests
{
    // #1697 F2. The two arms are genuinely connected around the bend, but not along this ray.
    [Fact]
    public void ConnectedBentBandStopsAtItsFirstDownstreamApronCell()
    {
        var doc = TileWorldMovementNavigationTests.Drawn(0, 0, 8, 6);
        for (int z = 0; z <= 6; z++)
            for (int x = 0; x <= 8; x++)
                doc.SetCornerHeightCm(x, z, 0, (short)(x <= 1 || z <= 1 ? 600 : x >= 6 ? 0 : 300));
        using var scene = new ContactTerrainScene(6, document: doc, boundary: MovementBoundary.Rectangle(0, -6, 8, 0));
        var options = new PhysicsNavBakeOptions(0, -6, 8, 0, 0.25f, 10, 14,
            ContactTerrainScene.Tuning.MaxSlopeRadians, 1024, 4096);
        using var capture = PhysicsNavBake.CaptureDescent(scene.Context, options, _ => 0);
        var e = capture.DescentEvidence!;
        var steep = Enumerable.Range(0, e.FaceCount).Where(i => e.Face(i).Analytic &&
            e.Face(i).Normal.Y > 0 && e.Face(i).Normal.Y < MathF.Cos(ContactTerrainScene.Tuning.MaxSlopeRadians)).ToHashSet();
        Assert.NotEmpty(steep);
        var connected = new HashSet<int> { steep.First() };
        var queue = new Queue<int>(connected);
        while (queue.TryDequeue(out int current))
            foreach (int joined in e.Joins(current))
                if (steep.Contains(joined) && connected.Add(joined)) queue.Enqueue(joined);
        Assert.Equal(steep.Count, connected.Count);
        var index = new ContactTerrainBandIndex(e, [steep.ToArray()], ContactTerrainScene.Tuning);
        int sourceX = (int)((0.625f - options.MinX) / options.CellSize);
        int zCell = (int)((-3.375f - options.MinZ) / options.CellSize);
        int firstApron = (int)((2.625f - options.MinX) / options.CellSize);
        // Exit bit zero is +X in the documented traversal ordering.
        Assert.Equal(firstApron - sourceX, index.Reach(sourceX, zCell, 0));

        var ordinary = capture.BuildProfile(ContactTerrainScene.Tuning, default);
        int targetX = (int)(6.375f / options.CellSize);
        var layers = ordinary.Graph.Layers.Select(layer =>
        {
            var accepted = new bool[e.Width * e.Height];
            foreach (int endpoint in new[] { sourceX, targetX })
                accepted[zCell * e.Width + endpoint] = layer.IsAccepted(endpoint, zCell);
            return new NavTraversalLayer(e.Width, e.Height, accepted, new byte[accepted.Length]);
        }).ToArray();
        var isolated = new GroundNavigation(new NavTraversalGraph(ordinary.Space, ordinary.AgentRadius,
            ordinary.AgentHeight, layers, []), ContactTerrainScene.Tuning, ordinary.Footprint);
        Assert.Contains(layers, layer => layer.IsAccepted(sourceX, zCell));
        Assert.Contains(layers, layer => layer.IsAccepted(targetX, zCell));
        var bindings = new ContactTerrainProfileEvidence(e, isolated);
        Assert.Empty(ContactTerrainDropCandidates.Generate(isolated, e, bindings, ContactTerrainScene.Tuning, 6));
    }
}
