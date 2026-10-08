using System.Linq;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class CompilerContractInternalTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void LegacyCornerCut_OddAndEvenRimFansMatchCountAndParentPlanes(int segments)
    {
        var (surface, patch) = CompilerFixtures.Row(MapPresencePolicy.LegacyTileWorld,
            (CompilerFixtures.Full with { Overlay = 2, Cut = MapOverlayCut.CornerQuarter }, true));
        patch.Heights = new[] { 101, 239, 367, 503 };
        patch.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.South, segments));
        Assert.Empty(patch.ValidateLocal());
        Assert.Empty(MapTopologyReferenceValidator.Validate(new[] { surface }, new[] { patch }));

        long count = MapSurfaceCompiler.CountFaces(surface, patch, MapSlotCellMask.All);
        MapCompiledPatch mesh = MapSurfaceCompiler.Compile(surface, patch);

        // Each half of the south edge gains one vertex for either subdivision, yielding two four-child fans.
        Assert.Equal(10L, count);
        Assert.Equal(count, (long)mesh.Faces.Count);
        Assert.Equal(new[] { 4, 4, 1, 1 }, Enumerable.Range(0, 4).Select(parent =>
            mesh.Faces.Count(f => f.Key.ParentTriangle == parent)));
        Assert.Equal(10, mesh.VertexIds.Count);
        Assert.Equal(mesh.VertexIds.Count, mesh.VertexIds.Distinct().Count());
        Assert.Equal(mesh.ExactVertices.Count, mesh.ExactVertices.Distinct().Count());
        Assert.Equal(10, mesh.Faces.Select(f => f.Key).Distinct().Count());
        Assert.Equal(new MapExactValue(1, 1), CompilerFixtures.Area(mesh, 0));
        Assert.All(mesh.Faces, face => Assert.Equal(
            CompilerFixtures.ParentPlaneHeight(mesh, face), CompilerFixtures.CentreHeight(mesh, face)));

        MapExactValue sw = MapExactValue.FromSingle(101 * 0.01f);
        MapExactValue se = MapExactValue.FromSingle(239 * 0.01f);
        MapExactValue midpoint = MapExactValue.FromSingle((101 * 0.01f + 239 * 0.01f) * 0.5f);
        var midAddress = MapLatticeAddress.Create(1, 0, 2);
        Assert.Equal(new MapExactPoint(new(1, 2), midpoint, new(0, 1)),
            CompilerFixtures.ExactAt(mesh, surface.Id, midAddress));
        for (int step = 1; step < segments; step++)
        {
            if (2 * step == segments) continue;
            MapExactValue expectedHeight = 2 * step < segments
                ? sw.Add(midpoint.Subtract(sw).Multiply(new(2 * step, segments)))
                : midpoint.Add(se.Subtract(midpoint).Multiply(new(2 * step - segments, segments)));
            var address = MapLatticeAddress.Create(step, 0, segments);
            Assert.Equal(1, mesh.VertexIds.Count(v => v.Address == address));
            Assert.Equal(new MapExactPoint(new(step, segments), expectedHeight, new(0, 1)),
                CompilerFixtures.ExactAt(mesh, surface.Id, address));
        }
    }
}
