using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

// Public synthetic command inputs shared by the evaluator and certification tests.
internal static class CoverageCommands
{
    internal static CoverageSourceTopology Solid(string identity, Vector3[] positions, uint[] indices) => new(identity, CoverageFixture.Hash, positions,
        Enumerable.Range(0, positions.Length).Select(v => new CoverageSourceVertex(0, 0, v, -1, 0, v)),
        Enumerable.Range(0, indices.Length / 3).Select(t => new CoverageSourceTriangle(0, 0, t, -1, 0, (int)indices[t * 3], (int)indices[t * 3 + 1], (int)indices[t * 3 + 2])));

    internal static CoverageSupportSurface Plate(CoverageFixture fixture) => CoverageSupportSurface.Compile(
        Solid("synthetic-plate", fixture.RigidPositions, fixture.RigidIndices), [new(0, 0, 0, fixture.SupportIndices.Length / 3)], Matrix4x4.Identity);

    // The fixture places equipment relative to the posed host, so its world draw follows the actor model.
    internal static CoverageCommandPose[] Poses(CoverageFixture fixture, CoverageRefinement topology, CoverageSupportSurface support,
        Matrix4x4 actor, float transition = 1) => fixture.Commands.Select(c =>
        {
            Matrix4x4 equipment = c.CoverageLocal * actor;
            int region = fixture.Map.Regions.Select((r, i) => (r.Key, i)).Single(p => p.Key == c.Region).i;
            return new CoverageCommandPose(c.Id, c.EquipmentInstanceId, topology, region, support, equipment, support.CoverageLocal * equipment,
                c.MinZ, c.MaxZ, c.CollarWidth, c.Clearance, c.MaxWorldRecession, transition);
        }).ToArray();

    internal static ModelVertex[] Surface(CoverageRefinement topology, ModelVertex[] posed)
    {
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, posed, scratch, out var failure), failure.ToString());
        return scratch.Before.ToArray();
    }

    internal static CoverageGeometryOracle.Result Witness(CoverageFixture fixture, CoverageRefinement topology, ModelVertex[] before,
        ModelVertex[] after, uint[]? indices = null) => CoverageGeometryOracle.Validate(before, after, fixture,
        indices ?? topology.Indices.ToArray(), topology.Vertices.Select(v => v.Strength == 0).ToArray());

    internal static ModelVertex[] Place(ModelVertex[] vertices, Matrix4x4 matrix) =>
        vertices.Select(v => { v.Position = Vector3.Transform(v.Position, matrix); return v; }).ToArray();

    // The same scene expressed directly in world space, for the oracle and for direct-world comparison.
    internal static CoverageFixture World(CoverageFixture fixture, Matrix4x4 actor) => fixture with
    {
        Posed = Place(fixture.Posed, actor),
        Commands = fixture.Commands.Select(c => c with { CoverageLocal = c.CoverageLocal * actor }).ToArray(),
    };

    /// <summary>A closed thick shell whose inward sheet is a concave gable h = ridge - drop |x| / halfX over the full
    /// XY footprint, with a flat back. Support triangles come first. Either winding sign describes the same solid.</summary>
    internal static (Vector3[] Positions, uint[] Indices) Gabled(float ridge, float drop, float back, float halfX, float halfY, bool outward)
    {
        var positions = new Vector3[12];
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 3; i++)
            {
                float x = (i - 1) * halfX, y = (j * 2 - 1) * halfY;
                positions[i + 3 * j] = new(x, y, ridge - drop * Math.Abs(i - 1));
                positions[6 + i + 3 * j] = new(x, y, back);
            }
        var indices = new System.Collections.Generic.List<uint>();
        void Add(int a, int b, int c)
        {
            if (outward) indices.AddRange(new[] { (uint)a, (uint)c, (uint)b });
            else indices.AddRange(new[] { (uint)a, (uint)b, (uint)c });
        }
        for (int i = 0; i < 2; i++) { Add(i, i + 1, i + 4); Add(i, i + 4, i + 3); }
        for (int i = 0; i < 2; i++) { Add(6 + i, 6 + i + 4, 6 + i + 1); Add(6 + i, 6 + i + 3, 6 + i + 4); }
        int[] rim = [0, 1, 2, 5, 4, 3];
        for (int k = 0; k < rim.Length; k++)
        {
            int a = rim[k], b = rim[(k + 1) % rim.Length];
            Add(b, a, a + 6); Add(b, a + 6, b + 6);
        }
        return (positions, indices.ToArray());
    }
}
