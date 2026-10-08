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
        patch.Heights = new[] { 367, 503, 101, 239 };
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

        MapExactValue sw = new(367, 100);
        MapExactValue se = new(503, 100);
        MapExactValue midpoint = new(87, 20);
        MapExactValue submittedSw = MapExactValue.FromSingle(367 * 0.01f);
        MapExactValue submittedSe = MapExactValue.FromSingle(503 * 0.01f);
        float submittedMidpoint = (367 * 0.01f + 503 * 0.01f) * 0.5f;
        Assert.Equal(new MapExactValue(9122611, 2097152), MapExactValue.FromSingle(submittedMidpoint));
        Assert.NotEqual(submittedSw.Add(submittedSe).Divide(new(2, 1)), MapExactValue.FromSingle(submittedMidpoint));
        Assert.NotEqual(midpoint, MapExactValue.FromSingle(submittedMidpoint));
        var midAddress = MapLatticeAddress.Create(1, 0, 2);
        // Exact geometry follows authored centimetres. Submission keeps the released rounded midpoint.
        Assert.Equal(new MapExactPoint(new(1, 2), midpoint, new(0, 1)),
            CompilerFixtures.ExactAt(mesh, surface.Id, midAddress));
        Assert.Equal(submittedMidpoint, mesh.Offsets[mesh.VertexIds.ToList().FindIndex(v => v.Address == midAddress)].Y);
        Assert.Equal(new MapExactPoint(new(0, 1), sw, new(0, 1)),
            CompilerFixtures.ExactAt(mesh, surface.Id, MapLatticeAddress.Corner(0, 0)));
        Assert.Equal(new MapExactPoint(new(1, 1), se, new(0, 1)),
            CompilerFixtures.ExactAt(mesh, surface.Id, MapLatticeAddress.Corner(1, 0)));
        for (int step = 1; step < segments; step++)
        {
            if (2 * step == segments) continue;
            MapExactValue expectedHeight = new(367L * segments + 136L * step, 100L * segments);
            MapExactValue submittedMid = MapExactValue.FromSingle(submittedMidpoint);
            MapExactValue submittedHeight = 2 * step < segments
                ? submittedSw.Add(submittedMid.Subtract(submittedSw).Multiply(new(2 * step, segments)))
                : submittedMid.Add(submittedSe.Subtract(submittedMid).Multiply(new(2 * step - segments, segments)));
            Assert.NotEqual(submittedHeight, expectedHeight);
            var address = MapLatticeAddress.Create(step, 0, segments);
            Assert.Equal(1, mesh.VertexIds.Count(v => v.Address == address));
            Assert.Equal(new MapExactPoint(new(step, segments), expectedHeight, new(0, 1)),
                CompilerFixtures.ExactAt(mesh, surface.Id, address));
            Assert.Equal(submittedHeight.ToSingle(), mesh.Offsets[mesh.VertexIds.ToList().FindIndex(v => v.Address == address)].Y);
        }
        // The two south fans retain the old centroid offsets as well as canonical parent planes.
        MapExactValue submittedNe = MapExactValue.FromSingle(239 * 0.01f);
        MapExactValue submittedWest = MapExactValue.FromSingle((367 * 0.01f + 101 * 0.01f) * 0.5f);
        var centres = new[]
        {
            (Address: MapLatticeAddress.Create(1, 1, 6), Height: submittedSw.Add(MapExactValue.FromSingle(submittedMidpoint)).Add(submittedWest).Divide(new(3, 1))),
            (Address: MapLatticeAddress.Create(5, 2, 6), Height: MapExactValue.FromSingle(submittedMidpoint).Add(submittedSe).Add(submittedNe).Divide(new(3, 1))),
        };
        Assert.All(centres, centre => Assert.Equal(centre.Height.ToSingle(),
            mesh.Offsets[mesh.VertexIds.ToList().FindIndex(v => v.Address == centre.Address)].Y));

        // The north midpoint distinguishes released float submission from canonical rounding.
        var (northSurface, northPatch) = CompilerFixtures.Row(MapPresencePolicy.LegacyTileWorld,
            (patch.Cells[0] with { Rotation = 1 }, true));
        northPatch.Heights = patch.Heights.ToArray();
        Assert.Empty(northPatch.ValidateLocal());
        Assert.Empty(MapTopologyReferenceValidator.Validate(new[] { northSurface }, new[] { northPatch }));
        MapCompiledPatch northMesh = MapSurfaceCompiler.Compile(northSurface, northPatch);
        float submittedNorthMidpoint = (101 * 0.01f + 239 * 0.01f) * 0.5f;
        float canonicalNorthMidpoint = (float)((101 + 239) / 200m);
        Assert.NotEqual(canonicalNorthMidpoint, submittedNorthMidpoint);
        int northIndex = northMesh.VertexIds.ToList().FindIndex(v => v.Address == MapLatticeAddress.Create(1, 2, 2));
        Assert.Equal(submittedNorthMidpoint, northMesh.Offsets[northIndex].Y);
        Assert.Equal(new MapExactPoint(new(1, 2), new(101 + 239, 200), new(-1, 1)), northMesh.ExactVertices[northIndex]);
    }
}
