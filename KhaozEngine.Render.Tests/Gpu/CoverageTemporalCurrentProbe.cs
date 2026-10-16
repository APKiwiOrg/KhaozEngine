using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageTemporalCurrentProbe
{
    internal static void AssertRejectedPixelsMatchCurrent(CoverageGpuScene proof)
    {
        var fixture = proof.Fixture;
        var scene = fixture.Scene;
        var device = fixture.Device;
        var renderer = scene.TemporalResolveRendererForTests!;
        var history = scene.TemporalHistory;
        float[] actual = TemporalTextureIo.Read(device, history.Color(history.WriteIndex));
        float[] actualState = TemporalTextureIo.Read(device, history.Confidence(history.WriteIndex));
        var mask = proof.ReadMask();
        var maskTexture = scene.CoverageTransitionForTests.Texture!;
        using var colour = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            CoverageGpuScene.Width, CoverageGpuScene.Height, TemporalFormats.HistoryColor, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var state = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            CoverageGpuScene.Width, CoverageGpuScene.Height, TemporalFormats.HistoryConfidence, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var target = device.Factory.CreateFramebuffer(null, colour, state);
        using var shaders = device.Factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert,
            ShaderSources.TemporalResolveFragment(TemporalResolvePrecisionPolicy.For(device)));
        using var pipeline = TemporalResolveRenderer.Fullscreen(device.Factory, shaders, renderer.ResolveLayout, target.Outputs);
        using var commands = device.Factory.CreateCommandList();
        var original = renderer.LastUniforms;
        var reset = original;
        reset.Jitter.W = 0;
        using (GpuRecording.Open(device, commands, "Coverage.ResetCurrentProbe"))
        {
            renderer.PrepareUniforms(commands, reset, new TemporalDepthStoreUniforms { CurrentDepth = reset.CurrentDepth });
            commands.SetFramebuffer(target);
            commands.SetPipeline(pipeline);
            commands.SetGraphicsResourceSet(0, renderer.CurrentSet!);
            commands.Draw(3);
            renderer.PrepareUniforms(commands, original, new TemporalDepthStoreUniforms { CurrentDepth = original.CurrentDepth });
        }
        device.Submit(commands);
        device.WaitForIdle();
        float[] current = TemporalTextureIo.Read(device, colour);
        float[] currentState = TemporalTextureIo.Read(device, state);
        int compared = 0;
        for (int y = 0; y < CoverageGpuScene.Height; y++)
            for (int x = 0; x < CoverageGpuScene.Width; x++)
            {
                int mx = Math.Clamp((int)((x + .5f) * maskTexture.Width / CoverageGpuScene.Width + original.Jitter.X), 0, (int)maskTexture.Width - 1);
                int my = Math.Clamp((int)((y + .5f) * maskTexture.Height / CoverageGpuScene.Height + original.Jitter.Y), 0, (int)maskTexture.Height - 1);
                if (mask[my * (int)maskTexture.Width + mx] == 0) continue;
                int i = y * CoverageGpuScene.Width + x;
                for (int c = 0; c < 4; c++)
                    Assert.InRange(MathF.Abs(actual[i * 4 + c] - current[i * 4 + c]), 0, .001f);
                Assert.InRange(MathF.Abs(actualState[i * 2] - currentState[i * 2]), 0, .001f);
                compared++;
            }
        Assert.True(compared > 100);
        Console.WriteLine($"coverage reset-current compared={compared} frame={scene.LastTemporalDiagnostics.FrameIndex}");
    }
}
