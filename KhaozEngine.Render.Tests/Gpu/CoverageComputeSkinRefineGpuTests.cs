using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageComputeSkinRefineGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Skin_and_refinement_match_reference_words()
    {
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline);
        var cases = CoverageReferenceCases.All();
        var palettes = cases.Select(c => Compose(c.Palette, c.InverseBind)).ToArray();
        using var session = new CoverageGpuTestSession(gpu.GpuDevice, cases.Select(c => c.Topology).ToArray(), palettes);
        Assert.True(cases.Select(c => c.Topology.VertexCount).Distinct().Count() > 1);
        Execute(gpu.GpuDevice, pipeline, session, CoverageGpuPhase.Skin);
        for (int actor = 0; actor < cases.Length; actor++)
        {
            var c = cases[actor];
            var source = new ModelVertex[c.Topology.SourceVertexCount];
            Assert.True(CoverageReferenceSkinning.TrySkin(new(), new(c.Topology.Source, palettes[actor].Length),
                c.Topology.Source, palettes[actor], source, out _));
            AssertWords(c.Id + " source", source, session.ReadSource(actor));
            var scratch = new CoverageReferenceScratch(c.Topology);
            Assert.True(CoverageReferenceConstructor.TryInterpolate(c.Topology, source, scratch, out var failure), failure.ToString());
            AssertWords(c.Id + " before", scratch.Before, session.ReadBefore(actor));
            Assert.Equal(0u, session.ReadHeader(actor)[26]);
            Assert.Equal(1u, session.ReadHeader(actor)[31]);
        }
        // The frozen posed inputs exercise the bounded seam independently of stage 1.
        for (int actor = 0; actor < cases.Length; actor++) session.UploadPosed(cases[actor].Posed, actor);
        Execute(gpu.GpuDevice, pipeline, session, CoverageGpuPhase.ValidateSource);
        for (int actor = 0; actor < cases.Length; actor++)
        {
            var scratch = new CoverageReferenceScratch(cases[actor].Topology);
            Assert.True(CoverageReferenceConstructor.TryInterpolate(cases[actor].Topology, cases[actor].Posed, scratch, out _));
            AssertWords(cases[actor].Id + " frozen before", scratch.Before, session.ReadBefore(actor));
        }
        output.WriteLine($"Matched source and Before words for all {cases.Length} frozen actors in mixed-size Y batches");
    }

    [GpuFact]
    public void Refusal_before_Before_keeps_the_original_pose_available()
    {
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline);
        var fixtures = Enumerable.Range(0, 8).Select(_ => CoverageFixture.CurvedPanel(level: 3)).ToArray();
        var topologies = fixtures.Select(f => CoverageRefinement.Build(f.Source, f.Map)).ToArray();
        var alias = topologies[0].SourceAliases[0];
        fixtures[0].Posed[alias.Representative].Position.X = +0f;
        fixtures[0].Posed[alias.SourceVertex].Position = fixtures[0].Posed[alias.Representative].Position;
        fixtures[0].Posed[alias.SourceVertex].Position.X = -0f;
        fixtures[1].Posed[alias.SourceVertex].Position.X += .1f;
        var generated = topologies[2].Vertices.First(v => v.A == 4 && v.B == 4 && v.C == 4);
        var triangle = topologies[2].Source.Triangles[generated.SourceTriangle];
        foreach (var (v, value) in new[] { (triangle.A, 1f), (triangle.B, -1f), (triangle.C, 1e-8f) })
            fixtures[2].Posed[v].Normal = new(value, 0, 0);
        fixtures[3].Posed[0].Color.X = float.NaN;
        fixtures[3].Posed[alias.SourceVertex].Position.X += .1f;
        foreach (var (v, value) in new[] { (triangle.A, 1f), (triangle.B, -1f), (triangle.C, 1e-8f) })
            fixtures[5].Posed[v].Tangent = new(value, 0, 0, 1);
        foreach (ref var v in fixtures[6].Posed.AsSpan()) v.Position = new(float.MaxValue);
        foreach (ref var v in fixtures[7].Posed.AsSpan()) v.Color = new(float.MaxValue);
        foreach (ref var v in fixtures[4].Posed.AsSpan())
        {
            v.Color = new(float.Epsilon, -float.Epsilon, MathF.ScaleB(1, -126), 1);
            v.Uv = new(-float.Epsilon, float.Epsilon);
            v.Tangent = new(float.Epsilon, -float.Epsilon, 0, -float.Epsilon);
        }
        using var session = new CoverageGpuTestSession(gpu.GpuDevice, topologies,
            fixtures.Select(_ => new[] { Matrix4x4.Identity, Matrix4x4.Identity }).ToArray());
        for (int actor = 0; actor < fixtures.Length; actor++) session.UploadPosed(fixtures[actor].Posed, actor);
        session.PoisonBefore(0x4f123456u);
        Execute(gpu.GpuDevice, pipeline, session, CoverageGpuPhase.ValidateSource);
        for (int actor = 0; actor < fixtures.Length; actor++)
        {
            var scratch = new CoverageReferenceScratch(topologies[actor]);
            bool accepted = CoverageReferenceConstructor.TryInterpolate(topologies[actor], fixtures[actor].Posed, scratch, out var failure);
            uint[] header = session.ReadHeader(actor);
            Assert.Equal((uint)failure.Code, header[26]);
            Assert.Equal(accepted ? 1u : 0u, header[31]);
            Assert.Equal(accepted ? 0u : 1u, header[30]);
            Assert.Equal(0u, header[28]);
            Assert.Equal(uint.MaxValue, header[27]);
            uint[] result = session.ReadResult(actor);
            Assert.Equal((uint)failure.Code, result[8]);
            Assert.Equal(new uint[5], result[9..14]);
            Assert.Equal(accepted ? 0u : 1u, result[14]);
            Assert.Equal(topologies[actor].SourceVertexCount * 16, Words(session.ReadSource(actor)).Length);
            uint[] sourceWords = Words(fixtures[actor].Posed).Select(word => (word & 0x7fffffffu) <= 0x00800000u ? word & 0x80000000u : word).ToArray();
            Assert.Equal(sourceWords, Words(session.ReadSource(actor)));
            if (accepted) AssertWords("actor " + actor, scratch.Before, session.ReadBefore(actor));
            else Assert.All(Words(session.ReadBefore(actor)), w => Assert.Equal(0x4f123456u, w));
        }
        Assert.Equal((uint)CoverageFailureCode.FoldedPatch, session.ReadHeader(1)[26]);
        Assert.Equal((uint)CoverageFailureCode.UnsupportedTransform, session.ReadHeader(2)[26]);
        Assert.Equal(2u, session.ReadHeader(3)[29]);
        Assert.Equal(4u, session.ReadHeader(6)[29]);
        Assert.Equal(5u, session.ReadHeader(7)[29]);
        int negative = Enumerable.Range(0, topologies[0].VertexCount).First(v => topologies[0].Vertices[v].SourceVertex == alias.SourceVertex);
        int positive = Enumerable.Range(0, topologies[0].VertexCount).First(v => topologies[0].Vertices[v].SourceVertex == alias.Representative);
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(session.ReadBefore(0)[negative].Position.X));
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(session.ReadBefore(0)[positive].Position.X));
        uint[] untouched = Words(session.ReadBefore(4));
        // Refusing one actor on a subsequent recording cannot alter another actor's surface.
        fixtures[1].Posed[0].Position.X += 1;
        session.UploadPosed(fixtures[1].Posed, 1);
        Execute(gpu.GpuDevice, pipeline, session, CoverageGpuPhase.ValidateSource);
        int crossActorWordChanges = untouched.Zip(Words(session.ReadBefore(4))).Count(p => p.First != p.Second);
        Assert.Equal(0, crossActorWordChanges);
        fixtures[1].Posed[alias.SourceVertex].Position = fixtures[1].Posed[alias.Representative].Position;
        session.UploadPosed(fixtures[1].Posed, 1);
        Execute(gpu.GpuDevice, pipeline, session, CoverageGpuPhase.ValidateSource);
        Assert.Equal(0u, session.ReadHeader(1)[26]);
        Assert.Equal(0u, session.ReadHeader(1)[30]);
        Assert.Equal(1u, session.ReadHeader(1)[31]);
    }

    [GpuFact]
    public void Skin_thresholds_flush_copied_fields_and_complete_nonfinite_source_rows()
    {
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline);
        float[] weights = [0, MathF.BitDecrement(1e-8f), 1e-8f, MathF.BitIncrement(1e-8f), 1];
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topologies = weights.Select(w => WithWeights(fixture, w)).ToArray();
        Matrix4x4 invalid = Matrix4x4.Identity;
        invalid.M11 = float.PositiveInfinity;
        Matrix4x4[][] palettes = weights.Select((_, i) => new[] { i == 4 ? invalid : Matrix4x4.CreateTranslation(10, 20, 30), Matrix4x4.Identity }).ToArray();
        using var session = new CoverageGpuTestSession(gpu.GpuDevice, topologies, palettes);
        Execute(gpu.GpuDevice, pipeline, session, CoverageGpuPhase.Skin);
        for (int actor = 0; actor < topologies.Length; actor++)
        {
            var topology = topologies[actor];
            var source = new ModelVertex[topology.SourceVertexCount];
            bool accepted = CoverageReferenceSkinning.TrySkin(new(), new(topology.Source, 2), topology.Source, palettes[actor], source, out var code);
            Assert.Equal((uint)code, session.ReadHeader(actor)[26]);
            if (accepted) AssertWords("weight " + weights[actor], source, session.ReadSource(actor));
            else
            {
                Assert.Equal(0u, session.ReadHeader(actor)[31]);
                Assert.All(session.ReadSource(actor), v => Assert.False(float.IsFinite(v.Position.X)));
            }
        }
    }

    static CoverageRefinement WithWeights(CoverageFixture fixture, float weight)
    {
        var skin = fixture.Source.SkinVertices.ToArray();
        for (int v = 0; v < skin.Length; v++)
        {
            skin[v].BoneIndices = Vector4.Zero;
            skin[v].BoneWeights = new(weight, 0, 0, 0);
            skin[v].Color = new(float.Epsilon, -float.Epsilon, MathF.ScaleB(1, -126), 1);
            skin[v].Uv = new(-float.Epsilon, float.Epsilon);
            skin[v].Tangent = new(float.Epsilon, -float.Epsilon, 0, -float.Epsilon);
        }
        var source = new CoverageSourceTopology(fixture.Source.Identity, fixture.Source.SourceSha256, fixture.Source.Positions,
            fixture.Source.Vertices, fixture.Source.Triangles, skin);
        return CoverageRefinement.Build(source, fixture.Map);
    }

    static Matrix4x4[] Compose(Matrix4x4[] palette, Matrix4x4[] inverse)
    {
        var result = new Matrix4x4[palette.Length];
        CoverageReferenceSkinning.Compose(palette, inverse, result);
        return result;
    }

    static void Execute(IGpuDevice device, CoverageGpuPipeline pipeline, CoverageGpuTestSession session, CoverageGpuPhase first)
    {
        using IGpuCommandList commands = device.Factory.CreateCommandList();
        commands.Begin();
        pipeline.RecordPhases(commands, session.Batch, first, CoverageGpuPhase.FillBefore);
        session.RecordReadback(commands);
        commands.End();
        device.Submit(commands);
        device.WaitForIdle();
        session.CompleteReadback();
    }

    void Log(GpuDeviceContext gpu, CoverageGpuPipeline pipeline)
    {
        output.WriteLine($"Native backend {gpu.Backend}, adapter {gpu.GpuDevice.Capabilities.DeviceName}");
        foreach (var (phase, p) in pipeline.Pipelines)
            if (OperatingSystem.IsMacOS() && p is MetalComputePipeline metal)
            {
                output.WriteLine($"{phase}: maxTotalThreadsPerThreadgroup={metal.State.MaxTotalThreadsPerThreadgroup}, threadExecutionWidth={metal.State.ThreadExecutionWidth}");
                Assert.True(metal.State.MaxTotalThreadsPerThreadgroup >= 64);
            }
    }

    static uint[] Words(ModelVertex[] vertices) => vertices.SelectMany(v => CoverageReferenceBits.Fields(v)
        .Select(BitConverter.SingleToUInt32Bits)).ToArray();

    static void AssertWords(string label, ModelVertex[] expected, ModelVertex[] actual)
    {
        uint[] a = Words(expected), b = Words(actual);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            Assert.True(a[i] == b[i], $"{label}, vertex {i / 16}, field {i % 16}: expected {a[i]:x8}, actual {b[i]:x8}");
    }
}
