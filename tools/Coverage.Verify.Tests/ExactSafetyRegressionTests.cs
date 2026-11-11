using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class ExactSafetyRegressionTests
{
    [Theory]
    [InlineData("shared-corner")]
    [InlineData("touching")]
    [InlineData("shared-edge-crossing")]
    public void Topology_or_baseline_contact_never_waives_a_later_crossing(string kind)
    {
        var (topology, before, command) = Fan(kind);
        var scratch = new CoverageScratch(topology);
        Assert.True(CoverageCertification.ValidateGeometry(topology, before, before, Matrix4x4.Identity, [command],
            scratch, out var baseline), baseline.ToString());
        var after = before.ToArray();
        after[0].Position.Z = kind == "shared-edge-crossing" ? -.25f : -.3f;
        Assert.False(CoverageCertification.ValidateGeometry(topology, before, after, Matrix4x4.Identity, [command],
            scratch, out var certificate, out var failure));
        Assert.Equal(CoverageFailureCode.HostIntersection, failure.Code);
        Assert.Equal(default, certificate);
        Assert.True(before.Length > 1);
        for (int i = 1; i < before.Length; i++) Assert.Equal(before[i], after[i]);
        Assert.Equal(command.Id, failure.CommandId);
        Assert.Equal(command.EquipmentInstanceId, failure.EquipmentInstanceId);
        Assert.Equal(topology.HostIdentity, failure.HostIdentity);
        Assert.True(failure.Count > 0);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("corner-contact")]
    [InlineData("edge-contact")]
    [InlineData("corner-final-contact")]
    public void Legitimate_manifold_and_common_topology_contact_remains_valid(string kind)
    {
        var (topology, before, command) = Fan(kind);
        var scratch = new CoverageScratch(topology);
        Assert.True(CoverageCertification.ValidateGeometry(topology, before, before, Matrix4x4.Identity, [command],
            scratch, out var baseline), baseline.ToString());
        var after = before.ToArray();
        after[0].Position.Z = -.3f;
        Assert.True(CoverageCertification.ValidateGeometry(topology, before, after, Matrix4x4.Identity, [command],
            scratch, out var failure), failure.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Projected_injectivity_rejects_thin_overlap_including_affine_sum_tails(bool translated)
    {
        const float e = 1e-11f;
        Vector3[] points = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, e, 0), new(1, -1, 0), new(1, e, 0)];
        AssertProjected(points, false);
        // Keep both projected orientations positive, with only a shared corner between the triangles.
        points[3] = new(1, 0, 0); points[4] = new(1, -1, 0); points[5] = new(2, 0, 0);
        AssertProjected(points, true);

        void AssertProjected(Vector3[] positions, bool expected)
        {
            var topology = Host(positions, [0, 1, 2, 3, 4, 5], 2, -1);
            var before = positions.Select(Vertex).ToArray();
            var actor = translated ? Matrix4x4.CreateTranslation(1e8f, 1e8f, 0) : Matrix4x4.Identity;
            var command = Command(topology);
            if (translated)
            {
                var shell = ExactGeometryFixtures.Gabled(0, 0, 1, 1e9f, 1e9f, outward: true);
                var support = CoverageSupportSurface.Compile(ExactGeometryFixtures.Solid("large-closed-shell", shell.Positions, shell.Indices),
                    [new(0, 0, 0, 4)], Matrix4x4.Identity);
                var equipment = Matrix4x4.CreateTranslation(0, 0, 1e6f);
                command = command with
                {
                    Support = support,
                    EquipmentWorld = equipment,
                    CoverageWorld = equipment,
                    MinZ = -2e6f,
                    MaxZ = 2e6f,
                    Clearance = 20000,
                    MaxWorldRecession = 0
                };
                // The ordinary rounded sum loses the overlap's positive Y offset.
                Assert.Equal((double)actor.M42, (double)positions[3].Y + actor.M42);
            }
            bool accepted = CoverageCertification.ValidateGeometry(topology, before, before, actor, [command],
                new CoverageScratch(topology), out var failure);
            Assert.Equal(expected, accepted);
            Assert.Equal(expected ? CoverageFailureCode.None : CoverageFailureCode.FoldedPatch, failure.Code);
            if (!expected)
            {
                Assert.Equal(topology.HostIdentity, failure.HostIdentity);
                Assert.Equal(command.Id, failure.CommandId);
                Assert.Equal(command.EquipmentInstanceId, failure.EquipmentInstanceId);
                Assert.True(failure.Count > 0);
            }
        }
    }

    static (CoverageRefinement Topology, ModelVertex[] Before, CoverageCommandPose Command) Fan(string kind)
    {
        var positions = new List<Vector3> { new(0, 0, 0), new(1, 1, 0), new(-1, 1, 0), new(-1, -1, 0), new(1, -1, 0) };
        var indices = new List<uint> { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1 };
        switch (kind)
        {
            // The protected bridge connects the corner one-ring without changing the reviewed crossing face.
            // The fan is at z=-.15 at both interior corners, so these depths straddle it.
            case "shared-corner": positions.AddRange([new(.3f, .5f, -.05f), new(.5f, .3f, -.25f)]); indices.AddRange([1, 6, 5, 1, 4, 6]); break;
            case "corner-final-contact": positions.AddRange([new(.3f, .5f, -.05f), new(.5f, .3f, -.05f)]); indices.AddRange([1, 6, 5, 1, 4, 6]); break;
            case "touching": positions.AddRange([new(.2f, .7f, 0), new(-.4f, .7f, -.25f), new(.2f, .3f, -.25f)]); indices.AddRange([5, 6, 7]); break;
            case "shared-edge-crossing": positions.Add(new(0, .5f, -.125f)); indices.AddRange([2, 1, 5]); break;
            case "corner-contact": positions.AddRange([new(1.3f, 1.5f, -.05f), new(1.5f, 1.3f, -.05f)]); indices.AddRange([1, 6, 5, 1, 4, 6]); break;
            case "edge-contact": positions.Add(new(0, 1.5f, -.05f)); indices.AddRange([2, 1, 5]); break;
        }
        var topology = Host(positions.ToArray(), indices.ToArray(), 4, 0);
        return (topology, positions.Select(Vertex).ToArray(), Command(topology));
    }

    static CoverageRefinement Host(Vector3[] positions, uint[] indices, int regionFaces, int movable)
    {
        var skin = positions.Select(p => new SkinnedVertex { Position = p, Normal = Vector3.UnitZ, Color = Vector4.One }).ToArray();
        var source = new CoverageSourceTopology("contact-host", CoverageFixture.Hash, positions,
            Enumerable.Range(0, positions.Length).Select(i => new CoverageSourceVertex(0, 0, i, -1, 0, i)),
            Enumerable.Range(0, indices.Length / 3).Select(t => new CoverageSourceTriangle(0, 0, t, -1, 0,
                (int)indices[t * 3], (int)indices[t * 3 + 1], (int)indices[t * 3 + 2])), skin);
        var region = new HostCoverageRegion("r0", [new(new(0, 0, 0, regionFaces), CoverageTriangleRole.Collar)],
            indices.Take(regionFaces * 3).Distinct().Select(i => new CoverageSurfaceStrength((int)i, i == movable ? .5f : 0)));
        var map = new HostCoverageMap(CoverageFixture.Hash, Enumerable.Range(0, positions.Length).Select(i => new CoverageTopologyEntry(0, 0, i, i)),
            [region], [], new("synthetic", "r0"));
        return CoverageRefinement.Build(source, map);
    }

    static CoverageCommandPose Command(CoverageRefinement topology)
    {
        var shell = ExactGeometryFixtures.Gabled(0, 0, 1, 2, 2, outward: true);
        var support = CoverageSupportSurface.Compile(ExactGeometryFixtures.Solid("contact-equipment", shell.Positions, shell.Indices),
            [new(0, 0, 0, 4)], Matrix4x4.Identity);
        var placed = Matrix4x4.CreateTranslation(0, 0, 1);
        return new(1, 1, topology, 0, support, placed, placed, -2, 2, .4f, .02f, .5f);
    }

    static ModelVertex Vertex(Vector3 p) => new(p, Vector3.UnitZ, Vector4.One);
}
