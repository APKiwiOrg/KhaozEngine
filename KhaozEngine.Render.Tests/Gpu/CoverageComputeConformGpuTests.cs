using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Gpu.CoverageGpuConformTestData;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageComputeConformGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Conformation_and_correction_preserve_reference_order()
    {
        string[] ids = ["curved-ordinary-seams-l3-w0", "curved-ordinary-seams-l3-w05", "curved-ordinary-seams-l3-w1",
            "cost-typical-l2-lifted-w1", "cost-limit-l2-lifted-w1", "curved-challenging-seams-l2-w1"];
        var cases = ids.Select(CoverageReferenceCases.Get).ToArray();
        var scratch = cases.Select(c => Prepare(c)).ToArray();
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline, output);
        using var session = Session(gpu.GpuDevice, cases, scratch);
        for (int actor = 0; actor < cases.Length; actor++) session.UploadPosed(cases[actor].Posed, actor);
        Execute(gpu.GpuDevice, pipeline, session, CoverageGpuPhase.ReduceConform);
        bool idleMovedByFace = false;
        for (int actor = 0; actor < cases.Length; actor++)
        {
            var c = cases[actor]; var s = scratch[actor];
            bool accepted = CoverageReferenceConstructor.TryConformGroups(c.Topology, c.Commands, s, out var failure);
            Failure(failure, session, actor);
            Assert.Equal(Words(s.FullRecessions), session.ReadWords(actor, 14));
            Assert.Equal(Words(s.GroupPositions), session.ReadWords(actor, 12));
            Assert.Equal(Words(s.After), session.ReadWords(actor, 6));
            Assert.Equal(s.ActiveGroupRepresentatives.Select(v => unchecked((uint)v)), session.ReadWords(actor, 18));
            Assert.True(accepted, failure.ToString());
        }
        Execute(gpu.GpuDevice, pipeline, session);
        for (int actor = 0; actor < cases.Length; actor++)
        {
            var c = cases[actor]; var s = scratch[actor];
            float[] point = s.FullRecessions.ToArray();
            bool accepted = CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, s, out var failure);
            Failure(failure, session, actor);
            Assert.Equal(Words(s.FaceCorrections), session.ReadWords(actor, 20));
            if (!accepted)
            {
                Assert.Equal(Words(s.FullRecessions), session.ReadWords(actor, 14));
                Assert.Equal(Words(s.Corrections), session.ReadWords(actor, 16));
                Assert.Equal(Words(s.GroupPositions), session.ReadWords(actor, 12));
                continue;
            }
            idleMovedByFace |= Enumerable.Range(0, point.Length).Any(g => point[g] == 0 && s.Corrections[g] > 0
                && s.GroupPositions[g] != s.Before[s.ActiveGroupRepresentatives[g]].Position);
            Assert.Equal(Words(s.FullRecessions), session.ReadWords(actor, 14));
            Assert.Equal(Words(s.Corrections), session.ReadWords(actor, 16));
            CoverageReferenceCorrection.ApplyTransition(c.Topology, c.Commands, s);
            Assert.Equal(Words(s.GroupPositions), session.ReadWords(actor, 12));
            Assert.Equal(Words(s.After), session.ReadWords(actor, 8));
        }
        Assert.True(idleMovedByFace);
    }

    [GpuFact]
    public void Partial_strength_only_refuses_after_a_positive_deficit()
    {
        var cases = new[] { Partial(false), Partial(true) };
        var scratch = cases.Select(c => Prepare(c)).ToArray();
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline, output);
        using var session = Session(gpu.GpuDevice, cases, scratch);
        for (int actor = 0; actor < cases.Length; actor++) session.UploadPosed(cases[actor].Posed, actor);
        Execute(gpu.GpuDevice, pipeline, session);
        for (int actor = 0; actor < cases.Length; actor++)
        {
            var c = cases[actor]; var s = scratch[actor];
            Assert.True(CoverageReferenceConstructor.TryConformGroups(c.Topology, c.Commands, s, out var failure), failure.ToString());
            bool accepted = CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, s, out failure);
            Failure(failure, session, actor);
            Assert.Equal(actor == 0, accepted);
            Assert.Equal(Words(s.FaceCorrections), session.ReadWords(actor, 20));
            if (actor == 1) Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
            else
            {
                var partial = Enumerable.Range(0, c.Topology.TriangleCount).Where(t => c.Topology.Triangles[t].Role == CoverageTriangleRole.Core
                    && Enumerable.Range(0, 3).Any(k => c.Topology.Vertices[(int)c.Topology.Indices[t * 3 + k]].Strength != 1));
                Assert.All(partial, t => Assert.Equal(0u, session.ReadWords(actor, 20)[t]));
            }
        }
    }

    static CoverageReferenceCase Partial(bool positive)
    {
        var fixture = CoverageFixture.CurvedPanel(seams: false, level: 2);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var entry = fixture.Map.Topology.Single(e => e.SurfaceId == (positive ? 24 : 18));
        int source = Enumerable.Range(0, fixture.Source.Vertices.Count).Single(i =>
            (fixture.Source.Vertices[i].Mesh, fixture.Source.Vertices[i].Primitive, fixture.Source.Vertices[i].Vertex) == (entry.Mesh, entry.Primitive, entry.Vertex));
        var records = (CoverageRefinedVertex[])typeof(System.Collections.ObjectModel.ReadOnlyCollection<CoverageRefinedVertex>)
            .GetField("list", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(topology.Vertices)!;
        int group = records.First(r => r.SourceVertex == source).PositionGroup;
        for (int v = 0; v < records.Length; v++) if (records[v].PositionGroup == group) records[v] = records[v] with { Strength = .5f };
        return new("partial", topology, [], [], fixture.Posed, Matrix4x4.Identity,
            CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity));
    }
}
