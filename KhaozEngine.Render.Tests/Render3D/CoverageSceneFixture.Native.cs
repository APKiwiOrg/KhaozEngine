using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render3D;

internal sealed partial class CoverageSceneFixture
{
    readonly GpuDeviceContext? nativeContext;
    GpuFrameSubmission? nativeSubmission;
    internal bool NativeEquipmentShadowOnly;
    internal bool SubmitNative { get; set; } = true;
    internal Action<RecordingGpuCommandList.DrawBindings>? BeforeNonIndexedDraw;
    internal readonly List<NativeHistoryDraw> NativeDraws = new();
    internal SkinnedCoverageFrame LastCoverageFrame;
    internal sealed record NativeHistoryDraw(CoverageGpuSurfaceView Surface, uint[] DrawWords, Matrix4x4 PreviousModel, int FirstCorner, int CornerCount);
    internal IGpuDevice NativeDevice => nativeContext!.GpuDevice;

    internal (IGpuTexture Coverage, IGpuTexture Ordinary) BoundRejectionInputs
    {
        get
        {
            var resolve = Scene.TemporalResolveRendererForTests!;
            var set = resolve.LastEntry == TemporalResolveEntry.Split
                ? resolve.SplitResolveForTests!.CoverageAccumulateSetForTests(Scene.TemporalHistory.ReadIndex)
                : resolve.CurrentSet!;
            var resources = tracker.ResourcesOf(set).Resources;
            return ((IGpuTexture)resources[^2], (IGpuTexture)resources[^1]);
        }
    }

    internal float[] Mask()
    {
        var texture = Scene.CoverageTransitionForTests.Texture!;
        using var staging = NativeDevice.Factory.CreateTexture(GpuTextureDescription.Texture2D(texture.Width,
            texture.Height, GpuPixelFormat.R8UNorm, GpuTextureUsage.Staging));
        using var commands = NativeDevice.Factory.CreateCommandList();
        using (GpuRecording.Open(NativeDevice, commands, "Coverage.HistoryMask")) commands.CopyTexture(texture, staging);
        NativeDevice.Submit(commands); NativeDevice.WaitForIdle();
        var map = NativeDevice.Map(staging, GpuMapMode.Read);
        var values = new float[texture.Width * texture.Height];
        try
        {
            for (int y = 0; y < texture.Height; y++)
                for (int x = 0; x < texture.Width; x++)
                    values[y * (int)texture.Width + x] = Marshal.ReadByte(map.Data + y * (int)map.RowPitch + x) / 255f;
        }
        finally { NativeDevice.Unmap(staging); }
        return values;
    }

    internal Vector3[] FinalPositions(bool previous = false)
    {
        var draw = NativeDraws.First(d => d.Surface.CurrentBase == LastCoverageFrame.Preparation!.GpuReservation.CurrentBase);
        uint[] current = CoverageSelectedReadback.Read(NativeDevice, draw.Surface.Current);
        bool seed = current[draw.Surface.CurrentBase + 15] != 0;
        uint[] words = previous && !seed ? CoverageSelectedReadback.Read(NativeDevice, draw.Surface.Previous) : current;
        uint start = previous && !seed ? draw.Surface.PreviousBase : draw.Surface.CurrentBase;
        bool source = words[start + 14] != 0;
        int count = source ? LastCoverageFrame.Preparation!.GpuReservation.SourceCount : draw.Surface.VertexCount;
        int offset = checked((int)start + 16 + (source ? 0 : LastCoverageFrame.Preparation!.GpuReservation.SourceCount * 16));
        var values = new Vector3[count];
        Matrix4x4 model = previous && !seed ? draw.PreviousModel : LastCoverageFrame.Preparation!.Model;
        if (!(previous && !seed)) model.Translation -= Scene.CurrentFrameView.RenderOrigin;
        for (int i = 0; i < count; i++)
        {
            var position = new Vector3(BitConverter.UInt32BitsToSingle(words[offset + i * 16]),
                BitConverter.UInt32BitsToSingle(words[offset + i * 16 + 1]), BitConverter.UInt32BitsToSingle(words[offset + i * 16 + 2]));
            values[i] = previous ? Vector3.Transform(position, model) : position;
        }
        return values;
    }

    void RenderNative(int width, int height, bool prepare)
    {
        if (prepare) Scene.PrepareFrame(width, height);
        nativeSubmission ??= new(device);
        nativeSubmission.Begin();
        Record?.Dispose();
        NativeDraws.Clear();
        Record = new(device.Factory.CreateCommandList())
        {
            CapturePayloads = true,
            CaptureBindings = true,
            UniformWindowsOfSet = tracker.WindowsOf,
            BeforeIndexedDraw = BeforeDraw,
        };
        Record.BeforeNonIndexedDraw = (bindings, corners, _, _, _) =>
        {
            if (bindings.Pipeline is not { } pipeline || tracker.ProgramOf(pipeline)?.Vertex != ShaderSources.CoverageModelMotionVert) return;
            var bound = bindings.Sets[2];
            var resources = tracker.ResourcesOf(bound.Set).Resources;
            var range = Assert.IsType<GpuBufferRange>(resources[2]);
            var window = Assert.Single(tracker.WindowsOf(bound.Set)).Rebased(bound.DynamicOffset);
            Assert.Equal(80u, window.Bytes);
            var upload = Record.Uploads.Last(u => ReferenceEquals(u.Buffer, range.Buffer) && u.Offset <= window.Offset
                && (ulong)u.Offset + u.Bytes >= (ulong)window.Offset + 80);
            var bytes = upload.Data!.AsSpan((int)(window.Offset - upload.Offset), 80);
            var words = MemoryMarshal.Cast<byte, uint>(bytes[..16]).ToArray();
            var reservation = Scene.CoverageGpuBatchForTests!.Actors.ToArray().Single(r => r.CurrentBase == words[0]);
            Assert.Same(Scene.CoverageGpuBatchForTests!.Current, resources[0]);
            Assert.Same(Scene.CoverageGpuBatchForTests!.Previous, resources[3]);
            Assert.Equal(reservation.CurrentBase, words[0]);
            Assert.Equal(reservation.PreviousBase, words[3]);
            var surface = new CoverageGpuSurfaceView((IGpuBuffer)resources[0], (IGpuBuffer)resources[3], (IGpuBuffer)resources[1],
                words[0], words[3], words[1], reservation.VertexCount, reservation.TriangleCount);
            NativeDraws.Add(new(surface, words, MemoryMarshal.Read<Matrix4x4>(bytes[16..]), (int)words[2], (int)corners));
        };
        try
        {
            using (GpuRecording.Open(device, Record, "Coverage.History"))
            {
                Scene.RenderInternal(Record, width, height, target);
                Scene.RecordCoverageReadbacks(Record, nativeSubmission);
            }
            if (SubmitNative) nativeSubmission.Submit(Record.Inner);
            else nativeSubmission.Abandon();
            NativeDevice.WaitForIdle();
            NativeDevice.Present();
        }
        catch { nativeSubmission.Abandon(); throw; }
    }
}
