using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Synthetic flat panel whose core region ends inside two source triangles. A 4 by 4 vertex grid at spacing 2
/// makes 18 triangles. The centre square, triangles 8 (Ta) and 9 (Tb), has full strength corners and refines at level 4,
/// where the listed child cells are exactly those under a small plate's support square.</summary>
internal static class CoverageChildCoreFixture
{
    internal const int TriangleA = 8, TriangleB = 9, CornerSurface = 10, EscapeCellA = 5;
    internal static readonly int[] CoreCellsA = [2, 3, 4, 9], CoreCellsB = [7, 8, 9, 12];

    /// <summary>One mesh, primitive and material slice. Surface ID equals the source vertex, and the identity palette
    /// makes the posed source equal the rest source.</summary>
    internal static CoverageSourceTopology Source()
    {
        var positions = new Vector3[16];
        var skin = new SkinnedVertex[16];
        var vertices = new CoverageSourceVertex[16];
        for (int v = 0; v < 16; v++)
        {
            positions[v] = new(-3 + 2 * (v % 4), -3 + 2 * (v / 4), .07f);
            skin[v] = new SkinnedVertex
            {
                Position = positions[v],
                Normal = Vector3.UnitZ,
                Tangent = new(1, 0, 0, 1),
                Color = Vector4.One,
                BoneIndices = new(0, 1, 0, 0),
                BoneWeights = new(1, 0, 0, 0),
            };
            vertices[v] = new(0, 0, v, -1, 0, v);
        }
        return new("synthetic-host", CoverageFixture.Hash, positions, vertices, Triangles(), skin);
    }

    /// <summary>Region r0 owns all 18 triangles. <paramref name="centreRole"/> is the role of Ta and Tb, the rest are
    /// collar. Surfaces 5, 6, 9 and 10 have strength 1, except that <paramref name="cornerStrength"/> sets surface 10,
    /// and every boundary surface has strength 0.</summary>
    internal static HostCoverageMap Map(CoverageTriangleRole centreRole, IEnumerable<CoverageChildRole> childRoles,
        float cornerStrength = 1f, int levelA = 4, int levelB = 4)
    {
        HostCoverageRegionTriangles[] triangles = [new(new(0, 0, 0, TriangleA), CoverageTriangleRole.Collar),
            new(new(0, 0, TriangleA, 2), centreRole), new(new(0, 0, TriangleB + 1, 8), CoverageTriangleRole.Collar)];
        var strengths = Enumerable.Range(0, 16).Select(v => new CoverageSurfaceStrength(v,
            v == CornerSurface ? cornerStrength : v is 5 or 6 or 9 ? 1f : 0f));
        return new(CoverageFixture.Hash, Enumerable.Range(0, 16).Select(v => new CoverageTopologyEntry(0, 0, v, v)),
            [new HostCoverageRegion("r0", triangles, strengths, childRoles)],
            [new HostCoverageRefinement(new(0, 0, TriangleA, 1), levelA), new HostCoverageRefinement(new(0, 0, TriangleB, 1), levelB)],
            new("synthetic", "r0"));
    }

    /// <summary>The Ta and Tb cells whose three corners lie under the plate's support square, role core at level 4.</summary>
    internal static IEnumerable<CoverageChildRole> CoreCells() =>
        CoreCellsA.Select(cell => new CoverageChildRole(0, 0, TriangleA, 4, cell, CoverageTriangleRole.Core))
            .Concat(CoreCellsB.Select(cell => new CoverageChildRole(0, 0, TriangleB, 4, cell, CoverageTriangleRole.Core)));

    /// <summary>The curved panel plate scaled by 0.6 in x and y. Its support square spans [-0.6, 0.6].</summary>
    internal static Vector3[] PlatePositions { get; } = [new(-.6f, -.6f, .1f), new(.6f, -.6f, .1f), new(.6f, .6f, .1f),
        new(-.6f, .6f, .1f), new(0, 0, .02f), new(-.6f, -.6f, .2f), new(.6f, -.6f, .2f), new(.6f, .6f, .2f), new(-.6f, .6f, .2f)];

    /// <summary>Rigid plate triangles. The first four are the support faces.</summary>
    internal static uint[] PlateIndices { get; } = [0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4,
        5, 7, 6, 5, 8, 7, 0, 5, 6, 0, 6, 1, 1, 6, 7, 1, 7, 2, 2, 7, 8, 2, 8, 3, 3, 8, 5, 3, 5, 0];

    static IEnumerable<CoverageSourceTriangle> Triangles()
    {
        int ordinal = 0;
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                int a = row * 4 + col, b = a + 1, c = a + 4, d = c + 1;
                yield return new(0, 0, ordinal++, -1, 0, a, b, d);
                yield return new(0, 0, ordinal++, -1, 0, a, d, c);
            }
    }
}
