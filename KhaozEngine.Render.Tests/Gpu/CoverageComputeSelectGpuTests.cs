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

public sealed class CoverageComputeSelectGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void A_late_gather_failure_publishes_only_the_complete_fallback()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out _));
        for (int v = 0; v < topology.VertexCount; v++)
        {
            scratch.Before[v].Normal = Vector3.Zero; scratch.Before[v].Tangent = Vector4.Zero;
            scratch.After[v] = scratch.Before[v]; scratch.After[v].Position.Z += .125f;
        }
        scratch.Before[0].Normal = Vector3.UnitZ;
        scratch.Before[^1].Normal = new(float.MaxValue);
        var sentinel = new ModelVertex(new(7, 8, 9), Vector3.UnitY, Vector4.One);
        Array.Fill(scratch.Published, sentinel);
        Assert.False(CoverageReferenceNormals.TryUpdate(topology, scratch, out var referenceFailure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, referenceFailure.Code);
        Assert.NotEqual(sentinel, scratch.Published[0]);
        Assert.Equal(sentinel, scratch.Published[^1]);
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline, output);
        using var session = new CoverageGpuTestSession(gpu.GpuDevice, [topology], [[Matrix4x4.Identity, Matrix4x4.Identity]]);
        session.PoisonNormalBases(0x7fc00001u);
        session.UploadPosed(fixture.Posed, 0);
        session.UploadVertices(0, 4, scratch.Before); session.UploadVertices(0, 8, scratch.After);
        using var commands = gpu.GpuDevice.Factory.CreateCommandList();
        commands.Begin(); pipeline.RecordPhases(commands, session.Batch, CoverageGpuPhase.ValidateVertices, CoverageGpuPhase.Select);
        session.RecordReadback(commands); commands.End(); gpu.GpuDevice.Submit(commands);
        gpu.GpuDevice.WaitForIdle(); session.CompleteReadback();
        Assert.Equal(referenceFailure, session.ReadFailure(0));
        Assert.Equal(1u, session.ReadRefusedFlag(0));
        Assert.Equal(session.ReadResult(0), session.ReadFinalHeader(0));
        Assert.Equal(Words([scratch.Published[0]]), session.ReadWords(0, 10)[..16]);
        Assert.Equal(Words(fixture.Posed), Words(session.ReadFinal(0, sourceFallback: true)));
        Assert.All(Words(session.ReadFinal(0, sourceFallback: false)), word => Assert.Equal(0u, word));
        Assert.Equal(11u, session.ReadHeader(0)[29]);
        output.WriteLine("Late gather refusal preserved the complete source section and published 0 candidate records");
    }

    [GpuFact]
    public void Stored_duplicate_corners_handedness_and_no_motion_records_are_preserved()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 2, degenerateFace: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out _));
        var rows = scratch.ReferenceTopology;
        Assert.True(rows.ShadingCorners.Select(c => rows.Indices[c]).Distinct().Count() < rows.ShadingCorners.Length);
        scratch.Before.CopyTo(scratch.After, 0);
        for (int v = 0; v < topology.VertexCount; v++)
            if (topology.Vertices[v].Strength > 0) scratch.After[v].Position.Z -= .05f;
        int last = topology.VertexCount - 1;
        scratch.Before[last].Position.X = 0f; scratch.After[last].Position.X = -0f;
        scratch.Before[last].Color.X = -0f; scratch.Before[last].Uv.X = -0f;
        Assert.True(CoverageReferenceNormals.TryUpdate(topology, scratch, out var failure), failure.ToString());
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline, output);
        using var session = new CoverageGpuTestSession(gpu.GpuDevice, [topology], [[Matrix4x4.Identity, Matrix4x4.Identity]]);
        session.UploadPosed(fixture.Posed, 0);
        session.UploadVertices(0, 4, scratch.Before); session.UploadVertices(0, 8, scratch.After);
        uint[] identity = [1, 0x12345678, 2, 0x23456789, 3, 0x3456789a, 4, 0x456789ab];
        gpu.GpuDevice.UpdateBuffer(session.Batch.Results, 0, identity);
        ExecuteNormals(gpu.GpuDevice, pipeline, session);
        Assert.Equal(default, session.ReadFailure(0));
        var actual = session.ReadFinal(0, sourceFallback: false);
        Assert.Equal(Words(scratch.Published), Words(actual));
        Assert.Equal(Words([scratch.Before[last]]), Words([actual[last]]));
        Assert.Contains(Enumerable.Range(0, actual.Length), v => actual[v].Position != scratch.Before[v].Position && actual[v].Tangent.W == -1);
        Assert.Equal(session.ReadResult(0), session.ReadFinalHeader(0));
        Assert.Equal(identity, session.ReadFinalHeader(0)[..8]);
        uint[] bases = session.ReadWords(0, 42);
        for (int t = 0; t < topology.TriangleCount; t++)
            Assert.Contains(bases[t * 46 + 45], new uint[] { 0, 2 });
    }

    [GpuFact]
    public void Orientation_disagreement_reduces_before_a_later_face_failure()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var c = new CoverageReferenceCase("orientation", topology, [], [], fixture.Posed, Matrix4x4.Identity, commands);
        var scratch = Prepare(c);
        for (int v = 0; v < topology.VertexCount; v++)
            if (topology.Source.Triangles[topology.Vertices[v].SourceTriangle].MaterialSlice == 1) scratch.Before[v].Position.X *= -1;
        // A later face has a finite but overflowing area. The earlier sign disagreement must win.
        int index = (int)topology.Indices[^1]; scratch.Before[index].Position.Y = float.MaxValue;
        scratch.Before.CopyTo(scratch.After, 0);
        Assert.False(CoverageReferenceNormals.TryUpdate(topology, scratch, out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline, output);
        using var session = Session(gpu.GpuDevice, [c], [scratch]);
        session.UploadPosed(c.Posed, 0);
        session.UploadVertices(0, 4, scratch.Before); session.UploadVertices(0, 8, scratch.After);
        ExecuteNormals(gpu.GpuDevice, pipeline, session);
        Assert.Equal(failure, session.ReadFailure(0));
        Assert.Equal(10u, session.ReadHeader(0)[29]);
        Assert.Equal(1u, session.ReadRefusedFlag(0));
        Assert.Equal(Words(c.Posed), Words(session.ReadFinal(0, sourceFallback: true)));
        Assert.All(Words(session.ReadFinal(0, sourceFallback: false)), word => Assert.Equal(0u, word));
    }

    static void ExecuteNormals(IGpuDevice device, CoverageGpuPipeline pipeline, CoverageGpuTestSession session)
    {
        using var commands = device.Factory.CreateCommandList();
        commands.Begin(); pipeline.RecordPhases(commands, session.Batch, CoverageGpuPhase.ValidateVertices, CoverageGpuPhase.Select);
        session.RecordReadback(commands); commands.End(); device.Submit(commands); device.WaitForIdle(); session.CompleteReadback();
    }

    [GpuFact]
    public void Invalid_vertices_win_before_face_or_gather_failures_with_zero_attribution()
    {
        var c = CoverageReferenceCases.Get("curved-ordinary-seams-l2-w1");
        var scratch = new[] { Prepare(c), Prepare(c) };
        foreach (var s in scratch) s.Before.CopyTo(s.After, 0);
        scratch[0].Before[^1].Color.Y = float.NaN;
        for (int v = 0; v < c.Topology.VertexCount; v++) scratch[0].After[v].Position = Vector3.Zero;
        scratch[1].After[^1].Position.X = float.PositiveInfinity;
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline, output);
        using var session = Session(gpu.GpuDevice, [c, c], scratch);
        for (int actor = 0; actor < 2; actor++)
        {
            session.UploadPosed(c.Posed, actor);
            session.UploadVertices(actor, 4, scratch[actor].Before); session.UploadVertices(actor, 8, scratch[actor].After);
        }
        ExecuteNormals(gpu.GpuDevice, pipeline, session);
        for (int actor = 0; actor < 2; actor++)
        {
            Assert.False(CoverageReferenceNormals.TryUpdate(c.Topology, scratch[actor], out var failure));
            Assert.Equal(failure, session.ReadFailure(actor));
            Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
            Assert.Equal(9u, session.ReadHeader(actor)[29]);
            Assert.Equal(new uint[5], session.ReadResult(actor)[9..14]);
            Assert.Equal(Words(c.Posed), Words(session.ReadFinal(actor, sourceFallback: true)));
        }
    }

    [GpuFact]
    public void All_numerical_construction_failures_keep_reference_codes_counts_and_ids()
    {
        string[] kinds = ["escaped-core", "cap-contact", "budget"];
        var cases = kinds.Select(kind =>
        {
            var (topology, posed, model, commands, _) = CoverageCertificationTests.Scenario(kind);
            return new CoverageReferenceCase(kind, topology, [], [], posed, model, commands);
        }).ToList();
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var boundaryTopology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var boundaryPosed = (ModelVertex[])fixture.Posed.Clone();
        int group = boundaryTopology.Vertices.First(v => v.SourceVertex >= 0 && v.Strength > 0 && v.Strength < 1).PositionGroup;
        foreach (var v in boundaryTopology.Vertices.Where(v => v.PositionGroup == group && v.SourceVertex >= 0))
            boundaryPosed[v.SourceVertex].Position.X = 1e20f;
        cases.Add(new("boundary-overflow", boundaryTopology, [], [], boundaryPosed, Matrix4x4.Identity,
            CoverageCommands.Poses(fixture, boundaryTopology, CoverageCommands.Plate(fixture), Matrix4x4.Identity)));
        var original = CoverageReferenceCases.Get("curved-ordinary-seams-l2-w1");
        var nonfinite = (ModelVertex[])original.Posed.Clone(); nonfinite[0].Color.X = float.NaN;
        var aliases = (ModelVertex[])original.Posed.Clone();
        aliases[original.Topology.SourceAliases[0].SourceVertex].Position.X += .1f;
        cases.Add(original with { Id = "nonfinite", Posed = nonfinite });
        cases.Add(original with { Id = "aliases", Posed = aliases });
        var scratch = cases.Select(c =>
        {
            var s = new CoverageReferenceScratch(c.Topology);
            Assert.True(CoverageReferenceFrames.TryPrepare(c.Topology, c.Model, c.Commands, s, out var failure), failure.ToString());
            return s;
        }).ToArray();
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline, output);
        using var session = Session(gpu.GpuDevice, cases.ToArray(), scratch);
        session.PoisonBefore(0x4f123456u);
        for (int actor = 0; actor < cases.Count; actor++) session.UploadPosed(cases[actor].Posed, actor);
        Execute(gpu.GpuDevice, pipeline, session, CoverageGpuPhase.Select);
        var codes = new System.Collections.Generic.HashSet<CoverageFailureCode>();
        for (int actor = 0; actor < cases.Count; actor++)
        {
            var c = cases[actor]; var final = new ModelVertex[c.Topology.VertexCount];
            Assert.False(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands, scratch[actor], final, out var failure));
            Assert.Equal(failure, session.ReadFailure(actor));
            Failure(failure, session, actor);
            Assert.Equal(session.ReadResult(actor), session.ReadFinalHeader(actor));
            codes.Add(failure.Code);
            uint[] source = Words(c.Posed).Select(word => (word & 0x7fffffffu) <= 0x00800000u ? word & 0x80000000u : word).ToArray();
            Assert.Equal(source, Words(session.ReadFinal(actor, sourceFallback: true)));
            Assert.All(Words(session.ReadFinal(actor, sourceFallback: false)), word => Assert.Equal(0u, word));
            output.WriteLine($"NEGATIVE {c.Id}: {failure}");
        }
        Assert.Equal(new[] { 3, 4, 5, 6, 7, 8 }, codes.Select(c => (int)c).Order().ToArray());
    }
}
