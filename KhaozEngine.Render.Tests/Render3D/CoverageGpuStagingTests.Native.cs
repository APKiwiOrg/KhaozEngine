using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("NativeDeviceLifecycle")]
public sealed class CoverageGpuStagingTestsCompute
{
    [GpuFact]
    public unsafe void Scene_staging_executes_complete_final_words_and_keeps_gpu_refusal_asynchronous()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        using var h = new CoverageSceneFixture(nativeDevice: gpu.GpuDevice);
        using var submission = new GpuFrameSubmission(gpu.GpuDevice);
        foreach (bool refuse in new[] { false, true })
        {
            h.Begin();
            var frame = h.Stage(recession: refuse ? 0 : null);
            var p = frame.Preparation!;
            var composed = new Matrix4x4[h.Palette.Length];
            CoverageReferenceSkinning.Compose(h.Palette, p.Owner.InverseBind, composed);
            var source = new ModelVertex[p.Owner.Topology.SourceVertexCount];
            Assert.True(CoverageReferenceSkinning.TrySkin(new(), new(p.Owner.Topology.Source, composed.Length),
                p.Owner.Topology.Source, composed, source, out _));
            var expected = new ModelVertex[p.Owner.Topology.VertexCount];
            bool accepted = CoverageReferenceConstructor.TryConstruct(p.Owner.Topology, source, p.Model,
                p.Commands.AsSpan(0, p.CommandCount), new(p.Owner.Topology), expected, out var failure);
            Assert.Equal(!refuse, accepted);
            h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
            h.SubmitEquipment();
            h.Scene.PrepareFrame(32, 24);
            var batch = h.Scene.CoverageGpuBatchForTests!;
            using var staging = gpu.GpuDevice.Factory.CreateBuffer(new(batch.Current.SizeInBytes, GpuBufferUsage.Staging));
            using var commands = gpu.GpuDevice.Factory.CreateCommandList();
            submission.Begin();
            commands.Begin();
            h.RecordUncaptured(commands, submission);
            commands.CopyBuffer(batch.Current, 0, staging, 0, batch.Current.SizeInBytes);
            commands.End();
            Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(frame).Presentation);
            submission.Submit(commands);
            // Headless assertion boundary only. Production resolution never drains or reads a final buffer.
            gpu.GpuDevice.WaitForIdle();
            var mapped = gpu.GpuDevice.Map(staging, GpuMapMode.Read);
            uint[] words;
            try { words = new ReadOnlySpan<uint>((void*)mapped.Data, (int)staging.SizeInBytes / 4).ToArray(); }
            finally { gpu.GpuDevice.Unmap(staging); }
            int start = (int)p.GpuReservation.CurrentBase;
            var header = words.AsSpan(start, 16).ToArray();
            Assert.Equal((uint)failure.Code, header[8]);
            Assert.Equal(refuse ? 1u : 0u, header[14]);
            Assert.Equal(p.ResultIdentity, header[..8]);
            int payload = start + 16 + (accepted ? source.Length * 16 : 0);
            uint[] actual = words.AsSpan(payload, (accepted ? expected.Length : source.Length) * 16).ToArray();
            uint[] reference = CoverageGpuConformTestData.Words(accepted ? expected : source);
            Assert.Equal(reference, actual);
            if (refuse)
            {
                Assert.Equal(failure.EquipmentInstanceId, header[9] | ((ulong)header[10] << 32));
                Assert.Equal(failure.CommandId, header[11]);
                Assert.All(words.Skip(start + 16 + source.Length * 16).Take(expected.Length * 16), word => Assert.Equal(0u, word));
            }
            Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(frame).Presentation);
        }
    }
}
