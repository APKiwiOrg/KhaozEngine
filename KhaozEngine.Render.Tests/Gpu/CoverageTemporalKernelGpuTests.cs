using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageTemporalKernelGpuTests(CoverageGpuScene scene) : IClassFixture<CoverageGpuScene>
{
    [GpuTheory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void Every_precision_and_entry_rejects_masked_history(int entry, bool half)
    {
        const int w = 16, h = 12;
        int dw = entry == 1 ? w * 2 : w, dh = entry == 1 ? h * 2 : h;
        IGpuDevice device = scene.Fixture.Device;
        using var renderer = new TemporalResolveRenderer(device);
        using var retired = GpuRetireQueue.Create(device);
        renderer.SelectEntry(entry == 0 ? TemporalResolveEntry.Fused : TemporalResolveEntry.Split, retired);
        var history = new TemporalHistory();
        history.EnsureTargets(device, dw, dh, w, h);
        using var colour = Input(GpuPixelFormat.R16G16B16A16Float);
        using var depth = Input(GpuPixelFormat.R32Float);
        using var motion = Input(GpuPixelFormat.R16G16Float);
        using var mask = Input(GpuPixelFormat.R8UNorm);
        using var commands = device.Factory.CreateCommandList();
        var current = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            current[i * 4] = current[i * 4 + 1] = current[i * 4 + 2] = .15f + (i % w) * .025f;
            current[i * 4 + 3] = 1;
        }
        TemporalTextureIo.Upload(device, colour, current);
        TemporalTextureIo.Upload(device, motion, new float[w * h * 2]);
        var rejection = Enumerable.Range(0, w * h).Select(i => i % w < w / 2 ? (byte)255 : (byte)0).ToArray();
        device.UpdateTexture(mask, rejection, 0, 0, w, h);
        var projection = Matrix4x4.CreateOrthographic(4, 3, .1f, 10);
        var view = new TemporalViewInput(Matrix4x4.Identity, projection);
        Vector4 clip = Vector4.Transform(new Vector4(0, 0, -2, 1), projection);
        TemporalTextureIo.Upload(device, depth, Enumerable.Repeat(clip.Z / clip.W, w * h).ToArray());
        float[][] results = new float[2][], states = new float[2][];
        try
        {
            for (int control = 0; control < 2; control++)
            {
                history.BeginResolve(control);
                var old = new float[dw * dh * 4];
                var state = new float[dw * dh * 2];
                for (int y = 0; y < dh; y++)
                    for (int x = 0; x < dw; x++)
                    {
                        int i = y * dw + x;
                        float grey = .15f + ((x + .5f) * w / dw - .5f) * .025f + .01f;
                        old[i * 4] = old[i * 4 + 1] = old[i * 4 + 2] = grey;
                        old[i * 4 + 3] = 1;
                        state[i * 2] = 3f / 15f;
                    }
                TemporalTextureIo.Upload(device, history.Color(history.ReadIndex), old);
                TemporalTextureIo.Upload(device, history.Confidence(history.ReadIndex), state);
                TemporalTextureIo.Upload(device, history.PreviousDepth(history.ReadIndex), Enumerable.Repeat(2f, w * h).ToArray());
                renderer.BindInputs(new TemporalResolveInputs(colour, colour, depth, motion)
                { CoverageRejection = mask }, history);
                var uniforms = TemporalResolveMath.BuildUniforms(view, view, Vector2.Zero, w, h, dw, dh, control == 0);
                using (GpuRecording.Open(device, commands, "Coverage.KernelPrepare"))
                {
                    renderer.PrepareUniforms(commands, uniforms, new TemporalDepthStoreUniforms { CurrentDepth = uniforms.CurrentDepth });
                    renderer.Run(commands, history);
                }
                device.Submit(commands);
                device.WaitForIdle();
                var precision = half ? TemporalResolvePrecision.Half : TemporalResolvePrecision.Full;
                using var shader = device.Factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert,
                    entry == 0 ? ShaderSources.TemporalResolveFragment(precision)
                        : ShaderSources.TemporalAccumulateFragment(precision, entry == 1));
                var layout = entry == 0 ? renderer.ResolveLayout : renderer.SplitResolveForTests!.LayoutsForTests.Accumulate;
                var set = entry == 0 ? renderer.CurrentSet! : renderer.SplitResolveForTests!.CoverageAccumulateSetForTests(history.ReadIndex);
                using var pipeline = TemporalResolveRenderer.Fullscreen(device.Factory, shader, layout,
                    new GpuOutputDescription(null, TemporalFormats.HistoryColor, TemporalFormats.HistoryConfidence));
                using (GpuRecording.Open(device, commands, "Coverage.KernelPrecision"))
                {
                    renderer.PrepareUniforms(commands, uniforms, new TemporalDepthStoreUniforms { CurrentDepth = uniforms.CurrentDepth });
                    commands.SetFramebuffer(history.ResolveFramebuffer(history.WriteIndex));
                    commands.SetPipeline(pipeline);
                    commands.SetGraphicsResourceSet(0, set);
                    commands.Draw(3);
                }
                device.Submit(commands);
                device.WaitForIdle();
                results[control] = TemporalTextureIo.Read(device, history.Color(history.WriteIndex));
                states[control] = TemporalTextureIo.Read(device, history.Confidence(history.WriteIndex));
                string name = $"coverage-kernel-{Guid.NewGuid():N}-entry{entry}-half{half}-control{control}-{device.Backend}";
                CoverageArtifacts.WriteRaw(name + ".colour.f32", results[control]);
                CoverageArtifacts.WriteRaw(name + ".confidence.f32", states[control]);
                Console.WriteLine($"coverage kernel={name} backend={device.Backend} device={device.Capabilities.DeviceName} display={dw}x{dh}");
            }
            int masked = 0, accumulated = 0;
            for (int y = 2; y < dh - 2; y++)
                for (int x = 2; x < dw - 2; x++)
                {
                    int i = y * dw + x;
                    Assert.InRange(states[1][i * 2], .001f, 1f / 15f + .001f);
                    if (entry != 1) Assert.InRange(states[1][i * 2], 1f / 15f - .001f, 1f / 15f + .001f);
                    if (x < dw / 2 - 2)
                    {
                        Assert.InRange(MathF.Abs(states[0][i * 2] - states[1][i * 2]), 0, .001f);
                        Assert.InRange(MathF.Abs(results[0][i * 4] - results[1][i * 4]), 0, .001f);
                        masked++;
                    }
                    if (x > dw / 2 + 2)
                    {
                        Assert.InRange(states[0][i * 2] - states[1][i * 2], 3f / 15f - .001f, 3f / 15f + .001f);
                        Assert.True(results[0][i * 4] > results[1][i * 4] + .001f);
                        accumulated++;
                    }
                }
            Assert.True(masked > 0 && accumulated > 0);
        }
        finally { history.ReleaseTargets(); }
        IGpuTexture Input(GpuPixelFormat format) => device.Factory.CreateTexture(GpuTextureDescription.Texture2D(w, h, format, GpuTextureUsage.Sampled));
    }
}
