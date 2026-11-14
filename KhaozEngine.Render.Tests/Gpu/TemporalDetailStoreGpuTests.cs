using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class TemporalDetailStoreGpuTests(TemporalDetailSceneFixture fixture, ITestOutputHelper output)
    : IClassFixture<TemporalDetailSceneFixture>
{
    [GpuFact]
    public void Replication_copies_hdr_channels_and_each_alpha_marker_without_arithmetic()
    {
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        var history = new TemporalHistory();
        try
        {
            history.EnsureTargets(gpu.GpuDevice, 2, 2, 2, 2, detail: TemporalHistoryDetail.Fine);
            TemporalTextureIo.Upload(gpu.GpuDevice, history.Color(history.WriteIndex),
                [0.125f, 4f, 0.5f, 0f, 2f, 0.25f, 0.75f, 0.25f,
                 8f, 0.5f, 0.125f, 0.5f, 16f, 0.25f, 1f, 1f]);
            using IGpuCommandList cl = gpu.GpuDevice.Factory.CreateCommandList();
            using (GpuRecording.Open(gpu.GpuDevice, cl, "TemporalDetailStoreGpuTests.Replication"))
            {
                history.DetailStore!.Prepare(cl);
                history.DetailStore.Replicate(cl, history.WriteIndex);
            }
            gpu.GpuDevice.Submit(cl);
            gpu.GpuDevice.WaitForIdle();
            AssertReplicated(gpu.GpuDevice, history);
        }
        finally { history.ReleaseTargets(); }
    }

    [GpuTheory]
    [InlineData(TemporalUpscale.Native, false)]
    [InlineData(TemporalUpscale.Native, true)]
    [InlineData(TemporalUpscale.Quality, false)]
    [InlineData(TemporalUpscale.Quality, true)]
    public void Fine_presents_every_still_and_moving_fence_frame_identically_and_replicates_history(
        TemporalUpscale preset, bool moving)
    {
        Compare(preset, moving, assertIdentity: true);
        if (preset == TemporalUpscale.Quality) Compare(TemporalUpscale.Balanced, moving, assertIdentity: false);
    }

    void Compare(TemporalUpscale preset, bool moving, bool assertIdentity)
    {
        fixture.Reset(preset);
        var fine = fixture.Fine;
        var display = fixture.Display;
        int frames = TemporalJitter.PhaseCount(TemporalSettings.DisplayOverInternal(preset)) + 1;
        int different = 0;
        for (int n = 0; n < frames; n++)
        {
            float pan = moving ? n * 0.35f : 0f;
            byte[] expected = display.Draw(pan), actual = fine.Draw(pan);
            Assert.Equal(TemporalHistoryDetail.Fine, fine.Frame.Scene.TemporalHistoryDetailInEffect);
            Assert.Null(display.Frame.Scene.TemporalHistory.DetailStore);
            for (int i = 0; i < actual.Length; i++) if (actual[i] != expected[i]) different++;
            if (!assertIdentity) continue;
            Assert.Equal(expected, actual);
            var a = fine.Frame.Scene.TemporalHistory;
            var b = display.Frame.Scene.TemporalHistory;
            Assert.Equal(TemporalTextureIo.Read(display.Frame.Device, b.Color(b.WriteIndex)),
                TemporalTextureIo.Read(fine.Frame.Device, a.Color(a.WriteIndex)));
            Assert.Equal(TemporalTextureIo.Read(display.Frame.Device, b.Confidence(b.WriteIndex)),
                TemporalTextureIo.Read(fine.Frame.Device, a.Confidence(a.WriteIndex)));
            AssertReplicated(fine.Frame);
        }
        output.WriteLine($"{preset}, moving {moving}, {frames} frames, differing presented bytes {different}");
    }

    [GpuTheory]
    [InlineData(TemporalUpscale.Native)]
    [InlineData(TemporalUpscale.Quality)]
    public void Resets_resize_captures_fallback_and_mode_changes_follow_the_history_lifetime(TemporalUpscale preset)
    {
        fixture.Reset(preset);
        var view = fixture.Fine;
        var h = view.Frame;
        var scene = h.Scene;
        view.Draw();
        view.Draw();
        var history = scene.TemporalHistory;
        var store = history.DetailStore;
        Assert.NotNull(store);
        int generation = history.TargetGeneration;
        scene.CameraCut();
        view.Draw();
        Assert.False(history.IsValid);
        Assert.Equal(TemporalResetReason.CameraCutRequested, history.LastReset);
        Assert.Same(store, history.DetailStore);
        Assert.Equal(generation, history.TargetGeneration);
        AssertReplicated(h);
        view.Draw();
        Assert.True(history.IsValid);

        scene.Post.Hdr.Enabled = !scene.Post.Hdr.Enabled;
        view.Draw();
        Assert.Equal(TemporalHistoryDetail.Fine, scene.TemporalHistoryDetailInEffect);
        Assert.Equal(TemporalResetReason.DeviceReset, history.LastReset);
        Assert.False(history.IsValid);
        AssertReplicated(h);
        view.Draw();
        Assert.True(history.IsValid);

        int write = history.WriteIndex;
        float[] before0 = TemporalTextureIo.Read(h.Device, store.Color(0));
        float[] before1 = TemporalTextureIo.Read(h.Device, store.Color(1));
        scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Display;
        scene.CameraOverride = new FlyCamera3D { Position = new Vector3(4f, 3f, 6f), AspectRatio = 1.5f };
        h.RenderSecond(96, 64);
        h.RenderSecond(48, 32);
        scene.CameraOverride = null;
        Assert.Equal(TemporalHistoryDetail.Fine, scene.TemporalHistoryDetailInEffect);
        Assert.Equal(write, history.WriteIndex);
        Assert.Equal(generation, history.TargetGeneration);
        Assert.Equal(before0, TemporalTextureIo.Read(h.Device, store.Color(0)));
        Assert.Equal(before1, TemporalTextureIo.Read(h.Device, store.Color(1)));

        view.Draw();
        Assert.Equal(TemporalHistoryDetail.Display, scene.TemporalHistoryDetailInEffect);
        Assert.Equal(TemporalResetReason.HistoryDetail, history.LastReset);
        Assert.False(history.IsValid);
        Assert.Null(history.DetailStore);
        view.Draw();
        Assert.True(history.IsValid);
        scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Fine;
        view.Draw();
        Assert.False(history.IsValid);
        Assert.Equal(TemporalResetReason.HistoryDetail, history.LastReset);
        Assert.NotSame(store, history.DetailStore);
        AssertReplicated(h);
        scene.Post.Temporal.HistoryDetailPixelLimit = 128L * 80;
        h.Resize(128, 80);
        view.Draw();
        Assert.Equal(TemporalResetReason.Resize, history.LastReset);
        Assert.False(history.IsValid);
        Assert.Equal((256u, 160u), (history.DetailStore!.Color(0).Width, history.DetailStore.Color(0).Height));
        AssertReplicated(h);
        view.Draw();
        Assert.True(history.IsValid);
        scene.Post.Temporal.HistoryDetailPixelLimit--;
        view.Draw();
        Assert.Equal(TemporalHistoryDetail.Display, scene.TemporalHistoryDetailInEffect);
        Assert.Equal(TemporalResetReason.HistoryDetail, history.LastReset);
        Assert.Null(history.DetailStore);
        view.Draw();
        Assert.True(history.IsValid);
        scene.Post.Temporal.HistoryDetailPixelLimit = long.MaxValue;
        h.Resize(checked((int)(h.Device.Capabilities.MaxRenderTexture2DDimension / 2) + 1), 1);
        view.Draw();
        Assert.Equal(TemporalHistoryDetail.Display, scene.TemporalHistoryDetailInEffect);
        Assert.Null(history.DetailStore);
        scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        view.Draw();
        Assert.False(history.TargetsAllocated);
        Assert.Null(history.DetailStore);
        Assert.Equal(TemporalHistoryDetail.Display, scene.TemporalHistoryDetailInEffect);
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replication_preserves_history_space_with_hdr_bloom_and_distortion(bool hdr)
    {
        fixture.Reset(TemporalUpscale.Quality, hdr);
        var view = fixture.Fine;
        view.Frame.Scene.Post.Bloom.Enabled = true;
        view.Frame.Frame((s, _) =>
        {
            view.Fence.DrawOverWall(s, 0f);
            s.DrawDistortion(new DistortionSprite
            {
                Position = new Vector3(0f, 0f, 1.2f), Size = 2.2f,
                Shape = DistortionShape.Ripple, Strength = 2.5f, Seed = 0.31f,
            });
        });
        AssertReplicated(view.Frame);
    }

    [GpuFact]
    public void The_aged_shared_view_matches_a_fresh_view_at_the_same_phase_after_a_reset()
    {
        fixture.Reset(TemporalUpscale.Native);
        var shared = fixture.Fine;
        shared.Draw(0f);
        shared.Frame.Scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
        shared.Draw(0.35f);
        shared.Frame.Scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Display;
        shared.Draw(0.7f);
        shared.Frame.Scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Fine;
        shared.Frame.Scene.Post.Temporal.Upscale = TemporalUpscale.Native;
        shared.Frame.Scene.CameraCut();
        using var fresh = new TemporalDetailSceneFixture.View();
        fresh.Frame.Scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Fine;
        fresh.Frame.SkipFrames(checked((int)shared.Frame.Scene.CurrentFrameView.FrameIndex));
        Assert.Equal(fresh.Draw(), shared.Draw());
        AssertReplicated(shared.Frame);
    }

    static void AssertReplicated(TemporalFixture h) => AssertReplicated(h.Device, h.Scene.TemporalHistory);

    static void AssertReplicated(IGpuDevice device, TemporalHistory history)
    {
        var store = history.DetailStore;
        Assert.NotNull(store);
        IGpuTexture source = history.Color(history.WriteIndex), target = store.Color(history.WriteIndex);
        Assert.Equal((2 * source.Width, 2 * source.Height, source.Format), (target.Width, target.Height, target.Format));
        float[] display = TemporalTextureIo.Read(device, source), fine = TemporalTextureIo.Read(device, target);
        Assert.NotEmpty(display);
        Assert.Equal(checked((int)(source.Width * source.Height * 4)), display.Length);
        Assert.Equal(display.Length * 4, fine.Length);
        for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
                for (int c = 0; c < 4; c++)
                {
                    int pixel = checked(((y / 2) * (int)source.Width + x / 2) * 4 + c);
                    int texel = checked((y * (int)target.Width + x) * 4 + c);
                    Assert.Equal(BitConverter.HalfToInt16Bits((Half)display[pixel]),
                        BitConverter.HalfToInt16Bits((Half)fine[texel]));
                }
    }
}
