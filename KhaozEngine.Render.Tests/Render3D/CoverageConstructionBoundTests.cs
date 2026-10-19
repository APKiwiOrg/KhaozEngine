using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageConstructionBoundTests
{
    [Fact]
    public void A_larger_magnitude_only_raises_the_bound_and_never_turns_refusal_into_acceptance()
    {
        // Each command's patch lies Near from the other command's box, the only place Certify reads the bound.
        var scene = Gap(1f / 1024);
        double[] magnitudes = [1, 64, 512, 2048, 4096];
        var bounds = new double[magnitudes.Length, 2];
        var accepted = new bool[magnitudes.Length];
        for (int m = 0; m < magnitudes.Length; m++)
        {
            var scratch = new CoverageScratch(scene.Topology);
            Assert.True(CoverageFrames.TryPrepare(scene.Topology, Matrix4x4.Identity, scene.Commands, scratch, magnitudes[m], out var failure),
                failure.ToString());
            for (int slot = 0; slot < 2; slot++) bounds[m, slot] = scratch.Frames[slot].ConstructionErrorBound;
            accepted[m] = CoverageCertification.Certify(scene.Topology, scene.Posed, scene.Posed, scene.Commands, scratch, out _, out failure);
            if (!accepted[m]) Assert.Equal(CoverageFailureCode.EquipmentIntersection, failure.Code);
        }
        for (int m = 1; m < magnitudes.Length; m++)
        {
            for (int slot = 0; slot < 2; slot++) Assert.True(bounds[m, slot] >= bounds[m - 1, slot]);
            Assert.True(!accepted[m] || accepted[m - 1]);
        }
        Assert.True(accepted[0]);
        Assert.False(accepted[^1]);
    }

    internal static Scene Gap(float gap, float recession1 = .8f, float recession2 = .8f, float transition = 1)
    {
        var positions = new List<Vector3>();
        var triangles = new List<CoverageSourceTriangle>();
        Patch(1.25f - gap, .125f, 3, 2);
        int split = triangles.Count;
        Patch(.5f + gap, -.375f, 3, 3);
        var skin = positions.Select(p => new SkinnedVertex { Position = p, Normal = Vector3.UnitZ, Color = Vector4.One, BoneWeights = new(1, 0, 0, 0) })
            .ToArray();
        var source = new CoverageSourceTopology("synthetic-gap", CoverageFixture.Hash, positions.ToArray(),
            positions.Select((_, i) => new CoverageSourceVertex(0, 0, i, -1, 0, i)), triangles, skin);
        int first = 6;
        var regions = new[]
        {
            new HostCoverageRegion("r0", [new(new(0, 0, 0, split), CoverageTriangleRole.Collar)],
                Enumerable.Range(0, first).Select(i => new CoverageSurfaceStrength(i, 0))),
            new HostCoverageRegion("r1", [new(new(0, 0, split, triangles.Count - split), CoverageTriangleRole.Collar)],
                Enumerable.Range(first, positions.Count - first).Select(i => new CoverageSurfaceStrength(i, 0))),
        };
        var map = new HostCoverageMap(CoverageFixture.Hash, positions.Select((_, i) => new CoverageTopologyEntry(0, 0, i, i)), regions, [],
            new("synthetic", "r0"));
        var topology = CoverageRefinement.Build(source, map);
        var box = CoverageCommands.Gabled(0, 0, 1, .5f, .5f, outward: true);
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("synthetic-box", box.Positions, box.Indices), [new(0, 0, 0, 4)],
            Matrix4x4.Identity);
        var second = Matrix4x4.CreateTranslation(2, 0, 0);
        CoverageCommandPose[] commands =
        [
            new(1, 1, topology, 0, support, Matrix4x4.Identity, Matrix4x4.Identity, -2, 2, .4f, .02f, recession1, transition),
            new(2, 2, topology, 1, support, second, second, -2, 2, .4f, .02f, recession2),
        ];
        var posed = skin.Select(v => SkinningMath.SkinVertex(v, [Matrix4x4.Identity])).ToArray();
        Assert.Equal(topology.VertexCount, posed.Length);
        return new(topology, posed, commands, box.Positions);

        void Patch(float x, float y, int columns, int rows)
        {
            int start = positions.Count;
            for (int j = 0; j < rows; j++)
                for (int i = 0; i < columns; i++) positions.Add(new(x + i * .125f, y + j * .125f, .5f));
            for (int j = 0; j + 1 < rows; j++)
                for (int i = 0; i + 1 < columns; i++)
                {
                    int a = start + j * columns + i;
                    triangles.Add(new(0, 0, triangles.Count, -1, 0, a, a + 1, a + columns + 1));
                    triangles.Add(new(0, 0, triangles.Count, -1, 0, a, a + columns + 1, a + columns));
                }
        }
    }

    internal sealed record Scene(CoverageRefinement Topology, ModelVertex[] Posed, CoverageCommandPose[] Commands, Vector3[] Box);
}
