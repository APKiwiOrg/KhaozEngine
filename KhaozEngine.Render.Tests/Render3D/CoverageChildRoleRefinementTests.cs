using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Tests.Render3D.CoverageChildCoreFixture;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Child role overrides in refinement: refined faces take their origin cell's role, and nothing else about the
/// compiled topology changes. All geometry is synthetic.</summary>
public class CoverageChildRoleRefinementTests
{
    // Taken on the refinement code before child roles existed. A map without overrides must keep it.
    const string NoOverrideRefinementSha256 = "0be50d97ebab78b8c97da5e4ca9ed1cc10d6cb3c8da61da78922c67ae2a61a60";

    [Fact]
    public void No_override_refinement_keeps_its_identity() => Assert.Equal(NoOverrideRefinementSha256,
        CoverageTableBinding.RefinementSha256(CoverageRefinement.Build(Source(), Map(CoverageTriangleRole.Core, []))));

    [Fact]
    public void Child_overrides_change_only_refined_role_words()
    {
        CoverageSourceTopology source = Source();
        var plain = CoverageRefinement.Build(source, Map(CoverageTriangleRole.Collar, []));
        var child = CoverageRefinement.Build(source, Map(CoverageTriangleRole.Collar, CoreCells()));

        Assert.Equal(plain.Vertices, child.Vertices);
        Assert.Equal(plain.Indices.ToArray(), child.Indices.ToArray());
        Assert.Equal(plain.PositionGroupCount, child.PositionGroupCount);
        for (int group = 0; group < plain.PositionGroupCount; group++)
            Assert.Equal(plain.GetPositionRecipe(group), child.GetPositionRecipe(group));
        Assert.Equal(plain.EdgeSplits, child.EdgeSplits);
        Assert.Equal(plain.SourceAliases, child.SourceAliases);
        Assert.Equal(plain.MaterialRanges, child.MaterialRanges);
        Assert.Equal(plain.Triangles.Select(t => (t.SourceTriangle, t.RegionIndex)), child.Triangles.Select(t => (t.SourceTriangle, t.RegionIndex)));

        int core = 0;
        for (int face = 0; face < child.TriangleCount; face++)
        {
            int[] cells = child.Triangles[face].SourceTriangle switch { TriangleA => CoreCellsA, TriangleB => CoreCellsB, _ => [] };
            var expected = cells.Contains(child.GetChildCell(face)) ? CoverageTriangleRole.Core : CoverageTriangleRole.Collar;
            Assert.Equal(expected, child.Triangles[face].Role);
            Assert.Equal(CoverageTriangleRole.Collar, plain.Triangles[face].Role);
            if (expected == CoverageTriangleRole.Core) core++;
        }
        Assert.Equal(CoreCellsA.Length + CoreCellsB.Length, core);
        Assert.NotEqual(CoverageTableBinding.RefinementSha256(plain), CoverageTableBinding.RefinementSha256(child));
    }

    [Fact]
    public void Child_cell_numbering_is_the_refinement_numbering()
    {
        CoverageSourceTopology source = Source();
        var topology = CoverageRefinement.Build(source, Map(CoverageTriangleRole.Collar, CoreCells()));
        Matrix4x4[] palette = [Matrix4x4.Identity, Matrix4x4.Identity];
        var posed = new ModelVertex[source.SkinVertices.Length];
        for (int i = 0; i < posed.Length; i++) posed[i] = SkinningMath.SkinVertex(source.SkinVertices[i], palette);
        var output = new ModelVertex[topology.VertexCount];
        topology.Interpolate(posed, output);

        var ta = source.Triangles[TriangleA];
        Vector3[] corners = [source.Positions[ta.A], source.Positions[ta.B], source.Positions[ta.C]];
        var faces = Enumerable.Range(0, topology.TriangleCount).Where(face => topology.Triangles[face].SourceTriangle == TriangleA).ToArray();
        Assert.Equal(Enumerable.Range(0, 16), faces.Select(topology.GetChildCell).Order());
        foreach (int face in faces)
        {
            CoverageChildCell cell = CoverageChildCells.Get(4, topology.GetChildCell(face));
            CoverageLatticePoint[] expected = [cell.A, cell.B, cell.C];
            for (int corner = 0; corner < 3; corner++)
            {
                // Grid spacing 2 at quarter weights puts every point on multiples of 0.5, so x and y compare exactly.
                Vector3 position = output[topology.Indices[face * 3 + corner]].Position;
                Assert.Equal(PlanePoint(corners, expected[corner]), new Vector2(position.X, position.Y));
            }
        }
    }

    [Fact]
    public void Split_pieces_keep_their_cell_role()
    {
        // Tb at level 4 splits the shared diagonal at quarter weights, which cuts Ta's level 2 cell 0 in two.
        var topology = CoverageRefinement.Build(Source(), Map(CoverageTriangleRole.Core,
            [new(0, 0, TriangleA, 2, 0, CoverageTriangleRole.Collar)], levelA: 2, levelB: 4));
        var faces = Enumerable.Range(0, topology.TriangleCount).Where(face => topology.Triangles[face].SourceTriangle == TriangleA).ToArray();
        var cellZero = faces.Where(face => topology.GetChildCell(face) == 0).ToArray();
        Assert.Equal(2, cellZero.Length);
        Assert.All(cellZero, face => Assert.Equal(CoverageTriangleRole.Collar, topology.Triangles[face].Role));
        var others = faces.Except(cellZero).ToArray();
        Assert.NotEmpty(others);
        Assert.All(others, face => Assert.Equal(CoverageTriangleRole.Core, topology.Triangles[face].Role));
    }

    [Fact]
    public void Core_cell_requires_full_parent_corner_strength()
    {
        CoverageSourceTopology source = Source();
        Assert.Equal(CoreCellsA.Length + CoreCellsB.Length, CoverageRefinement.Build(source, Map(CoverageTriangleRole.Collar, CoreCells()))
            .Triangles.Count(t => t.Role == CoverageTriangleRole.Core));

        var refusal = Assert.Throws<HostCoverageDataException>(() =>
            CoverageRefinement.Build(source, Map(CoverageTriangleRole.Collar, CoreCells(), cornerStrength: .5f)));
        Assert.Equal(HostCoverageRefusal.ChildCoreStrength, refusal.Reason);
        Assert.Equal(("synthetic-host", "r0", (int?)CornerSurface), (refusal.Identity, refusal.RegionKey, refusal.SurfaceId));
        Assert.Equal(((int?)0, (int?)0, (int?)TriangleA), (refusal.Mesh, refusal.Primitive, refusal.FirstTriangle));
        Assert.Equal(((int?)4, (int?)CoreCellsA[0]), (refusal.Level, refusal.Cell));
    }

    [Fact]
    public void Child_cell_lookup_refuses_an_index_outside_the_refined_faces()
    {
        var topology = CoverageRefinement.Build(Source(), Map(CoverageTriangleRole.Collar, CoreCells()));
        Assert.InRange(topology.GetChildCell(topology.TriangleCount - 1), 0, CoverageChildCells.CellCount(4) - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => topology.GetChildCell(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => topology.GetChildCell(topology.TriangleCount));
    }

    // Level 2 and 4 weights are exact halves and quarters. Level 3 sums such as fl(4/12) + fl(8/12) round to 1f. Each row
    // also splits the shared diagonal at the neighbour's points: weight 3 from level 4, and 4 and 8 from level 3.
    [Theory]
    [InlineData(2, 4)]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    public void Refined_core_strength_is_exactly_one(int levelA, int levelB)
    {
        var topology = CoverageRefinement.Build(Source(), Map(CoverageTriangleRole.Core, [], levelA: levelA, levelB: levelB));
        var core = Enumerable.Range(0, topology.TriangleCount).Where(face => topology.Triangles[face].Role == CoverageTriangleRole.Core).ToArray();
        Assert.All(core, face => Assert.Contains(topology.Triangles[face].SourceTriangle, new[] { TriangleA, TriangleB }));
        Assert.NotEmpty(core);
        uint[] indices = topology.Indices.ToArray();
        Assert.All(core.SelectMany(face => new[] { face * 3, face * 3 + 1, face * 3 + 2 }),
            index => Assert.Equal(1f, topology.Vertices[(int)indices[index]].Strength));
    }

    [Fact]
    public void Child_roles_resolve_when_another_mesh_is_placed_on_two_nodes()
    {
        var map = Map(CoverageTriangleRole.Collar, CoreCells());
        var expected = CoreFaces(CoverageRefinement.Build(Source(), map));
        Assert.Equal(CoreCellsA.Length + CoreCellsB.Length, expected.Length);

        // Mesh 1 is one triangle placed on nodes 0 and 1 at the same rest position, outside every region, so its
        // triangle ordinal repeats across the two placements.
        CoverageSourceTopology single = Source();
        Vector3[] corners = [new(10, 0, 0), new(11, 0, 0), new(10, 1, 0)];
        int first = single.Vertices.Count;
        var source = new CoverageSourceTopology(single.Identity, single.SourceSha256,
            single.Positions.ToArray().Concat(corners).Concat(corners).ToArray(),
            single.Vertices.Concat(Enumerable.Range(0, 6).Select(i => new CoverageSourceVertex(1, 0, i % 3, i / 3, 0, first + i))),
            single.Triangles.Concat(Enumerable.Range(0, 2).Select(node =>
                new CoverageSourceTriangle(1, 0, 0, node, 0, first + node * 3, first + node * 3 + 1, first + node * 3 + 2))),
            single.SkinVertices.ToArray().Concat(Enumerable.Range(0, 6).Select(i => single.SkinVertices[0] with { Position = corners[i % 3] })).ToArray());
        var placedTwice = new HostCoverageMap(map.HostSha256,
            map.Topology.Concat(Enumerable.Range(0, 3).Select(v => new CoverageTopologyEntry(1, 0, v, 16 + v))),
            map.Regions, map.Refinement, map.Authoring);

        Assert.Equal(expected, CoreFaces(CoverageRefinement.Build(source, placedTwice)));
    }

    static (int SourceTriangle, int Cell)[] CoreFaces(CoverageRefinement topology) => Enumerable.Range(0, topology.TriangleCount)
        .Where(face => topology.Triangles[face].Role == CoverageTriangleRole.Core)
        .Select(face => (topology.Triangles[face].SourceTriangle, topology.GetChildCell(face))).Order().ToArray();

    static Vector2 PlanePoint(Vector3[] corners, CoverageLatticePoint weights) => new(
        (corners[0].X * weights.A + corners[1].X * weights.B + corners[2].X * weights.C) / CoverageChildCells.Denominator,
        (corners[0].Y * weights.A + corners[1].Y * weights.B + corners[2].Y * weights.C) / CoverageChildCells.Denominator);
}
