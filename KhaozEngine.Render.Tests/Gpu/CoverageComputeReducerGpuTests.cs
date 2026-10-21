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

[Collection("NativeDeviceLifecycle")]
public sealed class CoverageComputeReducerGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Every_reducer_preserves_order_saturation_stage_precedence_and_input_rows()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 3, secondRegion: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var poses = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        poses[0] = poses[0] with { Id = 20, EquipmentInstanceId = 0x1234567800000020 };
        poses[1] = poses[1] with { Id = 10, EquipmentInstanceId = 0x8765432100000010 };
        var c = new CoverageReferenceCase("reducer-boundaries", topology, [], [], fixture.Posed, Matrix4x4.Identity, poses);
        using var gpu = GpuDeviceContext.CreateHeadless();
        var device = gpu.GpuDevice;
        var factory = device.Factory;
        using var layout = factory.CreateResourceLayout(new(
            new("CovStaticBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new("CovFrameBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new("CovWorkBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
            new("CovCurrentBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
            new("CovPreviousBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
            new("CovResultsBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
        int compared = 0;
        foreach (var phase in Enum.GetValues<CoverageGpuPhase>().Where(CoverageGpuSkinRefineGlsl.IsReducer))
        {
            CoverageReducerCase[] cases = CoverageReducerCase.For(phase);
            using var session = Session(device, cases.Select(_ => c).ToArray(), cases.Select(_ => Prepare(c)).ToArray());
            var batch = session.Batch;
            uint[] header = CoverageGpuLayout.WorkHeader(CoverageGpuUpload.Host(topology, 2));
            Assert.All(cases, item =>
            {
                Assert.True(header[25] >= item.Events.Length / 6);
                Assert.True(header[23] >= item.Faces.Length / 2);
            });
            (int countWord, uint stage) = Shape(phase);
            for (int actor = 0; actor < cases.Length; actor++)
            {
                var item = cases[actor];
                uint w = batch.Actors[actor].WorkBase;
                uint[] state = [item.Completed ? 3u : 0u, item.Completed ? 1u : uint.MaxValue,
                    item.Completed ? 17u : 0u, item.Completed ? 6u : 0u, item.Completed || Enumerable.Range(0, item.NoteCount).Any(i => item.Events[i * 6] != 0) ? 1u : 0u];
                device.UpdateBuffer(batch.Work, CoverageGpuLayout.Bytes(w + 26), state);
                device.UpdateBuffer(batch.Work, CoverageGpuLayout.Bytes(w + header[24]), item.Events);
                device.UpdateBuffer(batch.Work, CoverageGpuLayout.Bytes(w + header[22]), item.Faces);
                uint[] result = item.Completed ? new uint[] { 3, 0x20, 0x12345678, 20, 17, 0, 1, 0 } : new uint[8];
                device.UpdateBuffer(batch.Results, CoverageGpuLayout.Bytes((uint)actor * 16 + 8), result);
                // Only the reducer runs. Synthetic extents stay within the allocated event/face rows.
                device.UpdateBuffer(batch.FrameData, CoverageGpuLayout.Bytes(batch.Actors[actor].FrameBase + (uint)countWord),
                    new uint[] { (uint)item.TopologyCount });
            }
            using var set = factory.CreateResourceSet(new(layout, batch.StaticData, batch.FrameData, batch.Work, batch.Current, batch.Previous, batch.Results));
            using var shader = factory.CreateComputeShaderFromSpirv(CoverageGpuSkinRefineGlsl.Source(phase), new(PreciseMath: true));
            using var pipeline = factory.CreateComputePipeline(new(shader, layout));
            using var commands = factory.CreateCommandList();
            commands.Begin(); commands.SetComputePipeline(pipeline); commands.SetComputeResourceSet(0, set);
            commands.Dispatch(1, (uint)cases.Length, 1);
            session.RecordReadback(commands); commands.End(); device.Submit(commands); device.WaitForIdle(); session.CompleteReadback();
            for (int actor = 0; actor < cases.Length; actor++)
            {
                var item = cases[actor];
                var expected = CoverageReducerReference.Evaluate(phase, item);
                uint expectedStage = item.Completed ? 6u : expected.Code == 0 ? 0u : stage;
                uint[] state = session.ReadHeader(actor);
                Assert.Equal(expectedStage, state[29]);
                Assert.Equal(expected.Slot, state[27]);
                ulong equipment = expected.Slot == uint.MaxValue ? 0 : expected.Slot == 0 ? poses[1].EquipmentInstanceId : poses[0].EquipmentInstanceId;
                uint commandId = expected.Slot == uint.MaxValue ? 0 : expected.Slot == 0 ? 10u : 20u;
                Failure(expected.Failure(topology.HostIdentity, equipment, commandId), session, actor);
                Assert.Equal(item.Events, session.ReadWords(actor, 24).Take(item.Events.Length));
                Assert.Equal(item.Faces, session.ReadWords(actor, 22).Take(item.Faces.Length));
                compared++;
            }
            output.WriteLine($"{phase}: {cases.Length} actor streams matched serial reference outcomes");
        }
        Assert.Equal(Enum.GetValues<CoverageGpuPhase>().Where(CoverageGpuSkinRefineGlsl.IsReducer)
            .Sum(phase => CoverageReducerCase.For(phase).Length), compared);
    }

    static (int CountWord, uint Stage) Shape(CoverageGpuPhase phase) => phase switch
    {
        CoverageGpuPhase.ReduceSkin => (5, 1),
        CoverageGpuPhase.ReduceSource => (5, 2),
        CoverageGpuPhase.ReduceAliases => (9, 3),
        CoverageGpuPhase.ReduceGroups => (8, 4),
        CoverageGpuPhase.ReduceAttributes => (6, 5),
        CoverageGpuPhase.ReduceConform => (8, 6),
        CoverageGpuPhase.ReduceFaces => (7, 7),
        CoverageGpuPhase.ReduceFullTargets => (8, 8),
        CoverageGpuPhase.ReduceVertices => (6, 9),
        CoverageGpuPhase.ReduceFacesOrientation => (7, 10),
        CoverageGpuPhase.ReduceAttributesFinal => (6, 11),
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };
}
