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

public sealed partial class CoverageComputeGuardGpuTests
{
    [GpuFact]
    public void Matching_counts_saturate_without_counting_other_codes_or_slots()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 3, secondRegion: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var poses = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        poses[0] = poses[0] with { Id = 20, EquipmentInstanceId = 0x1234567800000020 };
        poses[1] = poses[1] with { Id = 10, EquipmentInstanceId = 0x8765432100000010 };
        var c = new CoverageReferenceCase("saturated", topology, [], [], fixture.Posed, Matrix4x4.Identity, poses);
        Assert.True(topology.PositionGroupCount > 192);
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var session = Session(gpu.GpuDevice, [c, c], [Prepare(c), Prepare(c)]);
        var factory = gpu.GpuDevice.Factory;
        using var layout = factory.CreateResourceLayout(new(
            new("CovStaticBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new("CovFrameBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new("CovWorkBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
            new("CovCurrentBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
            new("CovPreviousBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
            new("CovResultsBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
        var batch = session.Batch;
        using var set = factory.CreateResourceSet(new(layout, batch.StaticData, batch.FrameData, batch.Work, batch.Current, batch.Previous, batch.Results));
        string seed = "#version 450\nlayout(local_size_x = 64) in;\n" + CoverageGpuGuardGlsl.Bindings
            + CoverageGpuArithmeticGlsl.Source + CoverageGpuGuardGlsl.Source + """
            void main() {
                uint f = covFrame(), w = CovFrameWords[f+1u], g = gl_GlobalInvocationID.x;
                if (g >= CovFrameWords[f+8u] || covCompletedFailure(w)) return;
                uint code = 0u, count = 0u, element = g; int slot = 1;
                if (g == 0u) { code = 7u; count = 2147483640u; element = 100u; }
                if (g == 64u) { code = 7u; count = 100u; element = 4u; }
                if (g == 128u) { code = 4u; count = 2147483647u; element = 99u; }
                if (g == 192u) { code = 7u; count = 2147483647u; element = 101u; slot = 0; }
                if (gl_GlobalInvocationID.y == 1u && g == 1u) { code = 6u; count = 2u; element = 1u; slot = 0; }
                covNote(w,g,code,slot,element,0u,count);
            }
            """;
        using var resetShader = factory.CreateComputeShaderFromSpirv(CoverageGpuSkinRefineGlsl.Source(CoverageGpuPhase.Reset), new(PreciseMath: true));
        using var seedShader = factory.CreateComputeShaderFromSpirv(seed, new(PreciseMath: true));
        using var reduceShader = factory.CreateComputeShaderFromSpirv(CoverageGpuSkinRefineGlsl.Source(CoverageGpuPhase.ReduceConform), new(PreciseMath: true));
        using var reset = factory.CreateComputePipeline(new(resetShader, layout));
        using var producer = factory.CreateComputePipeline(new(seedShader, layout));
        using var reduce = factory.CreateComputePipeline(new(reduceShader, layout));
        foreach (var p in new[] { reset, producer, reduce })
            if (OperatingSystem.IsMacOS() && p is MetalComputePipeline metal)
            {
                output.WriteLine($"Guard pipeline maxTotalThreadsPerThreadgroup={metal.State.MaxTotalThreadsPerThreadgroup}, threadExecutionWidth={metal.State.ThreadExecutionWidth}");
                Assert.True(metal.State.MaxTotalThreadsPerThreadgroup >= 64);
            }
        using var commands = factory.CreateCommandList();
        commands.Begin();
        commands.SetComputePipeline(reset); commands.SetComputeResourceSet(0, set);
        commands.Dispatch((batch.Elements(CoverageGpuPhase.Reset) + 63) / 64, 2, 1);
        commands.SetComputePipeline(producer); commands.SetComputeResourceSet(0, set);
        commands.Dispatch(((uint)topology.PositionGroupCount + 63) / 64, 2, 1);
        commands.SetComputePipeline(reduce); commands.SetComputeResourceSet(0, set); commands.Dispatch(1, 2, 1);
        session.RecordReadback(commands); commands.End();
        gpu.GpuDevice.Submit(commands); gpu.GpuDevice.WaitForIdle(); session.CompleteReadback();
        Assert.Equal(2147483647u, session.ReadHeader(0)[28]);
        Assert.Equal(1u, session.ReadHeader(0)[27]);
        Assert.Equal(0u, session.ReadHeader(1)[27]);
        Failure(new(CoverageFailureCode.RecessionExceeded, topology.HostIdentity, poses[0].EquipmentInstanceId, 20, int.MaxValue, 0), session, 0);
        Failure(new(CoverageFailureCode.CapContact, topology.HostIdentity, poses[1].EquipmentInstanceId, 10, 2, 0), session, 1);
        Assert.Equal(1u, session.ReadHeader(0)[30]);
        Assert.Equal(1u, session.ReadHeader(1)[30]);
        uint[] events = session.ReadWords(0, 24);
        Assert.Equal(4, Enumerable.Range(0, topology.PositionGroupCount).Count(g => events[g * 6] != 0));
    }
}
