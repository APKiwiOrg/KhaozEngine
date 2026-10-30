using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Game;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ReleaseProbePreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProbedHostPreparesBoundWaterOnceAtTheActualViewportAcrossResize(bool split)
    {
        using var gpu = new FakeGpuDevice(GpuBackendKind.MetalNative);
        using var output = new PostChainRig.Output(gpu, 64, 40);
        using var device = new FrameOutputDevice(gpu, output);
        using var scene = new Scene3D(device, output.Framebuffer.Outputs);
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Starfield = false;
        scene.Post.Water.SwellAmplitude = 0;
        var handle = scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [2f, 2f, 2f, 2f]));
        var plane = new WaterPlane(0, 0, 0, 5, optics: new(handle, new(-5, 0, -5), new(10)));
        using var prefix = new PreparePhaseSceneSubmission(device, scene);
        using var commands = device.Factory.CreateCommandList();
        var frame = new Frame { Width = 64, Height = 40, Commands = commands };
        try
        {
            for (int i = 1; i <= 2; i++)
            {
                frame.Width = 64 * i;
                frame.Height = 40 * i;
                frame.BeginShadowStageCallback(new(Guid.NewGuid(), i, ShadowStageProbeMode.Counts));
                KhaozEngine.Tests.Windowing.RendererFramePolicy.Run(frame, true, device, commands,
                    KhaozEngine.Primitives.Color.Black, f =>
                    {
                        GameApp3D.PrepareProbedScene(scene, s => s.DrawWater(plane), f);
                        Assert.Equal((uint)f.Width, scene.ResolvedDepthTarget.Width);
                        Assert.Equal((uint)f.Height, scene.ResolvedDepthTarget.Height);
                        long uploads = scene.WaterOpticsStats.UploadCount;
                        scene.PrepareFrame(f.Width, f.Height);
                        Assert.Equal(uploads, scene.WaterOpticsStats.UploadCount);
                        if (split) prefix.Prepare(f);
                    }, f => Render3DSurface.RecordSceneOutput(scene, f, split ? prefix : null, output.Framebuffer),
                    null, true);
                frame.CompleteShadowStageCallback();
                Assert.True(frame.CompletedShadowStageCallback.Snapshot!.Value.Inputs!.Value.Complete);
                Assert.Equal(i, device.Presents);
            }
            Assert.Equal(1, scene.WaterOpticsStats.UploadCount);
        }
        finally { frame.DisposeSubmission(); }
    }

    [Theory]
    [InlineData(ShadowStageProbeMode.Counts)]
    [InlineData(ShadowStageProbeMode.Timings)]
    public void ObservedCascadeEyeIsConsumedRenderRelativeAndAbsoluteFitsRetainTheirMeaning(ShadowStageProbeMode mode)
    {
        using var off = new ShadowStageProbeFixture(directional: true);
        using var on = new ShadowStageProbeFixture(directional: true);
        var origin = new Vector3(100000, 0, -200000);
        foreach (var fixture in new[] { off, on })
        {
            fixture.Scene.RenderOrigin = origin;
            fixture.Scene.Camera.Frame(origin, new(10, 20, 10));
        }
        off.Render(ShadowStageProbeMode.Off, point: false);
        var input = on.Render(mode, point: false).Inputs!.Value;
        Assert.True(input.Complete);
        Assert.Equal(on.Scene.Camera.Eye - on.Scene.CurrentFrameView.RenderOrigin, input.Camera.Eye);
        Assert.NotEqual(on.Scene.Camera.Eye, input.Camera.Eye);
        var relative = Fits(on.Scene, "_cascadeCpuVps");
        var absolute = Fits(on.Scene, "_cascadeCpuVpsAbsolute");
        Assert.Equal(Fits(off.Scene, "_cascadeCpuVps"), relative);
        Assert.Equal(off.Order(), on.Order());
        Assert.Equal(Matrix4x4.CreateTranslation(-origin) * relative[0], absolute[0]);
        Assert.Equal(absolute[0], input.Fits.Fit0);
    }

    static Matrix4x4[] Fits(Scene3D scene, string name) => (Matrix4x4[])typeof(Scene3D)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scene)!;
}
