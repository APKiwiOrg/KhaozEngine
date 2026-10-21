using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;
using static KhaozEngine.Tests.Gpu.CoverageGpuConformTestData;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageComputeConformGpuTests
{
    [GpuFact]
    public void Skipped_and_failed_faces_never_load_poisoned_continuation_payloads()
    {
        const uint poison = 0x7fc00001;
        var original = CoverageReferenceCases.Get("curved-ordinary-seams-l3-w1");
        var cases = new[] { original, original with { Commands = [] }, original, original, original };
        var scratch = cases.Select(c => Prepare(c)).ToArray();
        scratch[2].Frames[0] = scratch[2].Frames[0] with { CorrectionRate = 0 };
        scratch[3].Frames[0] = scratch[3].Frames[0] with { ActualClearanceCoverage = -1 };
        scratch[4].Frames[0] = scratch[4].Frames[0] with { HostToActualSheet = Matrix4x4.CreateTranslation(10, 0, 0) };
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var pipeline = new CoverageGpuPipeline(gpu.GpuDevice);
        Log(gpu, pipeline, output);
        using var session = Session(gpu.GpuDevice, cases, scratch);
        session.PoisonContinuations(poison);
        for (int actor = 0; actor < cases.Length; actor++)
        {
            session.UploadPosed(cases[actor].Posed, actor);
            gpu.GpuDevice.UpdateBuffer(session.Batch.Work, CoverageGpuLayout.Bytes(session.Batch.Actors[actor].WorkBase + 38u), new uint[4]);
        }
        ExecuteWithFaceReadAudit(gpu.GpuDevice, pipeline, session);
        int coreFaces = original.Topology.Triangles.Count(t => t.Role == CoverageTriangleRole.Core);
        Assert.InRange(coreFaces, 1, original.Topology.TriangleCount - 1);
        for (int actor = 0; actor < cases.Length; actor++)
        {
            var c = cases[actor]; var s = scratch[actor];
            Assert.True(CoverageReferenceConstructor.TryConformGroups(c.Topology, c.Commands, s, out var failure), failure.ToString());
            bool accepted = CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, s, out failure);
            Assert.Equal(actor < 2, accepted);
            if (actor >= 2) Assert.Equal(actor == 4 ? CoverageFailureCode.CoreEscaped : CoverageFailureCode.UnsupportedTransform, failure.Code);
            Failure(failure, session, actor);
            Assert.Equal(Words(s.FaceCorrections), session.ReadWords(actor, 20));
            Assert.Equal(Words(s.FullRecessions), session.ReadWords(actor, 14));
            Assert.Equal(Words(s.Corrections), session.ReadWords(actor, 16));
            Assert.Equal(Words(s.GroupPositions), session.ReadWords(actor, 12));
            if (accepted)
            {
                CoverageReferenceCorrection.ApplyTransition(c.Topology, c.Commands, s);
                Assert.Equal(Words(s.After), session.ReadWords(actor, 8));
            }
            uint[] header = session.ReadHeader(actor);
            Assert.Equal(0u, header[38]); // Overlay loads without overlay state.
            Assert.Equal(0u, header[40]); // Finish loads without finish state.
            Assert.Equal(actor == 0 ? (uint)coreFaces * 4u : 0u, header[39]);
            Assert.Equal(actor == 0 ? (uint)coreFaces : 0u, header[41]);
            uint[] faces = session.ReadWords(actor, 36);
            for (int t = 0; t < c.Topology.TriangleCount; t++)
                if (actor != 0 || c.Topology.Triangles[t].Role != CoverageTriangleRole.Core)
                {
                    Assert.Equal(0u, faces[t * 12 + 11]);
                    Assert.All(faces.AsSpan(t * 12, 11).ToArray(), word => Assert.Equal(poison, word));
                }
            // Groups initialize every payload word even when no command supplies an active representative.
            Assert.DoesNotContain(poison, session.ReadWords(actor, 34));
        }
    }

    void ExecuteWithFaceReadAudit(IGpuDevice device, CoverageGpuPipeline pipeline, CoverageGpuTestSession session)
    {
        var factory = device.Factory;
        using var layout = factory.CreateResourceLayout(new(
            new("CovStaticBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new("CovFrameBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new("CovWorkBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
            new("CovCurrentBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
            new("CovPreviousBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
            new("CovResultsBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
        var batch = session.Batch;
        using var set = factory.CreateResourceSet(new(layout, batch.StaticData, batch.FrameData, batch.Work, batch.Current, batch.Previous, batch.Results));
        using var overlayShader = factory.CreateComputeShaderFromSpirv(AuditedSource(CoverageGpuPhase.CorrectFacesOverlay, 1, 38), new(PreciseMath: true));
        using var finishShader = factory.CreateComputeShaderFromSpirv(AuditedSource(CoverageGpuPhase.CorrectFacesFinish, 2, 40), new(PreciseMath: true));
        using var overlay = factory.CreateComputePipeline(new(overlayShader, layout));
        using var finish = factory.CreateComputePipeline(new(finishShader, layout));
        foreach (var p in new[] { overlay, finish })
            if (OperatingSystem.IsMacOS() && p is MetalComputePipeline metal)
            {
                output.WriteLine($"Continuation audit maxTotalThreadsPerThreadgroup={metal.State.MaxTotalThreadsPerThreadgroup}, threadExecutionWidth={metal.State.ThreadExecutionWidth}");
                Assert.True(metal.State.MaxTotalThreadsPerThreadgroup >= 64);
            }
        using var commands = factory.CreateCommandList();
        commands.Begin();
        pipeline.RecordPhases(commands, batch, CoverageGpuPhase.ValidateSource, CoverageGpuPhase.CorrectFaces);
        foreach (var (phase, standard) in pipeline.Pipelines.Where(p => p.Phase > CoverageGpuPhase.CorrectFaces))
        {
            commands.SetComputePipeline(phase == CoverageGpuPhase.CorrectFacesOverlay ? overlay : phase == CoverageGpuPhase.CorrectFacesFinish ? finish : standard);
            commands.SetComputeResourceSet(0, set);
            commands.Dispatch(CoverageGpuSkinRefineGlsl.IsReducer(phase) ? 1u : (batch.Elements(phase) + 63) / 64,
                (uint)batch.Count, CoverageGpuPipeline.DispatchDepth(phase));
        }
        session.RecordReadback(commands);
        commands.End(); device.Submit(commands); device.WaitForIdle(); session.CompleteReadback();
    }

    static string AuditedSource(CoverageGpuPhase phase, uint validState, uint counter)
    {
        // Observe the production payload accessor, including calls whose numerical result is later discarded.
        // Only these test shaders write the otherwise unused Work header words 38 to 41.
        const string read = "return CovFaceWork(";
        string audit = $"uint w = CovFrameWords[covFrame()+1u]; atomicAdd(CovWorkWords[w+{counter + 1}u],1u); "
            + $"if (CovWorkWords[p+11u] != {validState}u) atomicAdd(CovWorkWords[w+{counter}u],1u); " + read;
        string source = CoverageGpuSkinRefineGlsl.Source(phase);
        Assert.Contains(read, source);
        return source.Replace(read, audit, StringComparison.Ordinal);
    }
}
