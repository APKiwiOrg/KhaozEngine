using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Gpu.CoverageGpuConformTestData;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageComputeEquivalenceGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Every_production_fixture_matches_every_reference_word()
    {
        new CoverageReferenceOutputTests(output).Every_fixture_reference_output_matches_the_hash_manifest();
        var hashes = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "coverage-reference-v1", "outputs.sha256"))
            .Select(line => line.Split(' ', 2)).ToDictionary(parts => parts[1], parts => parts[0]);
        var cases = CoverageReferenceCases.All();
        var scratch = cases.Select(c => Prepare(c)).ToArray();
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline, output);
        using var session = Session(gpu.GpuDevice, cases, scratch);
        CoverageFilterNativeEvidence.Batch(gpu.GpuDevice, pipeline, session.Batch, "frozen72", output);
        session.PoisonNormalBases(0x7fc00001u);
        for (int actor = 0; actor < cases.Length; actor++) session.UploadPosed(cases[actor].Posed, actor);
        Execute(gpu.GpuDevice, pipeline, session, CoverageGpuPhase.Select);
        int comparedProductionCases = 0, comparedCertificationNegativeCases = 0, differingVertexWords = 0;
        for (int actor = 0; actor < cases.Length; actor++)
        {
            var c = cases[actor];
            var expected = new ModelVertex[c.Topology.VertexCount];
            Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands, scratch[actor], expected, out var failure), $"{c.Id}: {failure}");
            Assert.Equal(failure, session.ReadFailure(actor));
            ModelVertex[] actual = session.ReadFinal(actor, sourceFallback: false);
            uint[] expectedWords = Words(expected), actualWords = Words(actual);
            int differences = expectedWords.Zip(actualWords).Count(p => p.First != p.Second);
            differingVertexWords += differences;
            Assert.True(differences == 0, $"{c.Id}: {differences} differing vertex words");
            string expectedProductionHash = hashes[c.Id + "-0.bin"], actualGpuHash = CoverageReferenceOutputs.Hash(actual);
            Assert.Equal(expectedProductionHash, actualGpuHash);
            Assert.Equal(0u, session.ReadRefusedFlag(actor));
            comparedProductionCases++;
            var spec = CoverageReferenceCases.Spec(c.Id);
            if (spec.Challenging && spec.Weight == 1) comparedCertificationNegativeCases++;
            output.WriteLine($"FROZEN {c.Id} {actualGpuHash} words={actualWords.Length} differences={differences}");
        }
        Assert.Equal(72, comparedProductionCases);
        Assert.Equal(6, comparedCertificationNegativeCases);
        Assert.Equal(0, differingVertexWords);
        output.WriteLine("72 production cases, 6 certification-negative constructions, 0 differing vertex words, 0 unexpected GPU skips");
    }

    [GpuFact]
    public void Complete_skin_through_select_matches_the_same_uploaded_input_words()
    {
        var cases = CoverageReferenceCases.All();
        var palettes = cases.Select(c => Compose(c.Palette, c.InverseBind)).ToArray();
        var posed = cases.Select(c => new ModelVertex[c.Topology.SourceVertexCount]).ToArray();
        var scratch = cases.Select(c => new CoverageReferenceScratch(c.Topology)).ToArray();
        for (int actor = 0; actor < cases.Length; actor++)
        {
            var c = cases[actor];
            Assert.True(CoverageReferenceSkinning.TrySkin(new(), new(c.Topology.Source, palettes[actor].Length),
                c.Topology.Source, palettes[actor], posed[actor], out _));
            Assert.True(CoverageReferenceFrames.TryPrepare(c.Topology, c.Model, c.Commands, scratch[actor], out _));
        }
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline, output);
        using var session = new CoverageGpuTestSession(gpu.GpuDevice, cases.Select(c => c.Topology).ToArray(), palettes,
            cases.Select(c => c.Commands).ToArray(), scratch);
        using var commands = gpu.GpuDevice.Factory.CreateCommandList();
        commands.Begin(); pipeline.Record(commands, session.Batch); session.RecordReadback(commands);
        commands.End(); gpu.GpuDevice.Submit(commands); gpu.GpuDevice.WaitForIdle(); session.CompleteReadback();
        int compared = 0;
        for (int actor = 0; actor < cases.Length; actor++)
        {
            var c = cases[actor]; var expected = new ModelVertex[c.Topology.VertexCount];
            bool accepted = CoverageReferenceConstructor.TryConstruct(c.Topology, posed[actor], c.Model, c.Commands, scratch[actor], expected, out var failure);
            Assert.Equal(failure, session.ReadFailure(actor));
            Assert.Equal(accepted ? 0u : 1u, session.ReadRefusedFlag(actor));
            Assert.Equal(Words(accepted ? expected : posed[actor]), Words(session.ReadFinal(actor, sourceFallback: !accepted)));
            output.WriteLine($"SKIN {c.Id} {CoverageReferenceOutputs.Hash(accepted ? expected : posed[actor])} {failure.Code}");
            compared++;
        }
        Assert.Equal(72, compared);
    }

    static Matrix4x4[] Compose(Matrix4x4[] palette, Matrix4x4[] inverseBind)
    {
        var composed = new Matrix4x4[palette.Length];
        CoverageReferenceSkinning.Compose(palette, inverseBind, composed);
        return composed;
    }
}
