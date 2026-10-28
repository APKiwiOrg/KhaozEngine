using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class TemporalVisibilityComparisonGpuTests(GameShadowGrainScene grainScene, ITestOutputHelper output)
    : TemporalResolveGpuFacts, IClassFixture<GameShadowGrainScene>
{
    [GpuFact]
    public void TexturedRidgesMeetTaskOneGrainGuard() => TexturedShadowGrainCase.AssertGrain(grainScene, output);

    [GpuFact]
    public void SharedHelperMatchesScalarFootprintsAndStrictReleaseBoundaries()
    {
        using var probe = new TemporalVisibilityProbe();
        output.WriteLine($"{probe.Device.Backend} on {probe.Device.Capabilities.DeviceName}");
        foreach (VisibilityCase c in TemporalVisibilityReference.Cases())
        {
            (bool valid, float change) = TemporalVisibilityReference.Compare(c);
            Vector3 actual = probe.Run(c);
            output.WriteLine($"{c.Name}: valid {actual.Y}, signed {actual.X:R}, release {actual.Z}");
            Assert.Equal(valid ? 1 : 0, actual.Y);
            Assert.InRange(MathF.Abs(change - actual.X), 0, 1e-6f);
            Assert.Equal(valid && MathF.Abs(change) > 0.1f ? 1 : 0, actual.Z);
        }
    }

    public static IEnumerable<object[]> NonFiniteDepthCases()
        => TemporalVisibilityReference.NonFiniteCases().Select(c => new object[] { c.Name });

    [GpuTheory]
    [MemberData(nameof(NonFiniteDepthCases))]
    public void NonFiniteDepthsCannotQualify(string name)
    {
        VisibilityCase c = TemporalVisibilityReference.NonFiniteCases().Single(c => c.Name == name);
        using var probe = new TemporalVisibilityProbe();
        Vector3 actual = probe.Run(c);
        output.WriteLine($"{probe.Device.Backend} on {probe.Device.Capabilities.DeviceName}");
        output.WriteLine($"{c.Name}: valid {actual.Y}, signed {actual.X:R}, release {actual.Z}");
        Assert.Equal(c.Valid ? 1 : 0, actual.Y);
        Assert.InRange(MathF.Abs(c.Want - actual.X), 0, 1e-6f);
        Assert.Equal(c.Valid && MathF.Abs(c.Want) > 0.1f ? 1 : 0, actual.Z);
    }

    [GpuTheory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void VisibilityChangeReleasesCarriedLockWithoutRequiringRidge(int entry, bool ridge)
    {
        using var rig = new Rig();
        using GpuRetireQueue retired = GpuRetireQueue.Create(rig.Device);
        rig.Renderer.SelectEntry((TemporalResolveEntry)entry, retired);
        using IGpuTexture signal = Signal(rig.Device, 255);
        float[] color = Grey((x, _) => ridge && x == 3 ? 0.7f : 0.2f);
        rig.Fill(color, color, Motion((_, _) => Vector2.Zero));
        rig.BeginFrame(); rig.Resolve(Uniforms(false), signal);
        rig.History.MarkValidAfterFrame(); rig.BeginFrame();
        rig.FillHistory(Grey((_, _) => 0.4f), State((_, _) => new Vector2(1, 1)), SceneLinear);
        rig.Device.UpdateTexture(signal, new byte[N * N], 0, 0, N, N);
        rig.Resolve(Uniforms(true), signal);
        float[] state = rig.ReadState();
        output.WriteLine($"entry {entry}, ridge {ridge}: stored lock {state[At(3, 3) * 4 + 1]}, confidence {state[At(3, 3) * 4]}");
        Assert.Equal(0, state[At(3, 3) * 4 + 1]);
        if (!ridge)
        {
            Assert.Equal(1, state[At(3, 3) * 4]);
            Assert.InRange(rig.ReadColor()[At(3, 3) * 4], 0.19f, 0.21f);
        }
    }

    [GpuTheory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void LargeVisibilityStepsReleaseLocksWithoutChangingAccumulation(int entry, bool rising)
    {
        using var rig = new Rig();
        using GpuRetireQueue retired = GpuRetireQueue.Create(rig.Device);
        rig.Renderer.SelectEntry((TemporalResolveEntry)entry, retired);
        using IGpuTexture signal = Signal(rig.Device, rising ? (byte)0 : (byte)255);
        float[] color = Grey((_, _) => 0.2f);
        rig.Fill(color, color, Motion((_, _) => Vector2.Zero));
        rig.BeginFrame(); rig.Resolve(Uniforms(false), signal);
        rig.History.MarkValidAfterFrame(); rig.BeginFrame();
        rig.FillHistory(Grey((_, _) => 0.4f), State((_, _) => new Vector2(1, 1)), SceneLinear);
        byte[] next = new byte[N * N];
        Array.Fill(next, rising ? (byte)255 : (byte)0);
        rig.Device.UpdateTexture(signal, next, 0, 0, N, N);
        rig.Resolve(Uniforms(true), signal);
        float[] state = rig.ReadState();
        const float expected = 1;
        output.WriteLine($"ACCUMULATION|{entry}|{rising}|{expected:R}|{state[At(3, 3) * 4]:R}");
        Assert.InRange(MathF.Abs(state[At(3, 3) * 4] - expected), 0, 0.0001f);
        Assert.Equal(0, state[At(3, 3) * 4 + 1]);
        Assert.InRange(rig.ReadColor()[At(3, 3) * 4], 0.19f, 0.21f);
    }

    [GpuFact]
    public void VisibilityReleaseIsStrictAtBothSignedBoundariesAndLargeChanges()
    {
        using var probe = new TemporalVisibilityProbe();
        foreach (float magnitude in new[] { MathF.BitDecrement(0.1f), 0.1f, MathF.BitIncrement(0.1f), MathF.BitDecrement(0.5f), 0.5f, MathF.BitIncrement(0.5f), 1f })
            foreach (float sign in new[] { -1f, 1f })
            {
                float change = sign * magnitude;
                var c = new VisibilityCase("large change", change, 0);
                Array.Fill(c.Depth, 2); c.Depth[5] = 1;
                (bool valid, float oracle) = TemporalVisibilityReference.Compare(c);
                Vector3 actual = probe.Run(c);
                float expected = magnitude > 0.1f ? 1 : 0;
                output.WriteLine($"RELEASE|{change:R}|{actual.X:R}|{expected:R}|{actual.Z:R}");
                Assert.True(valid);
                Assert.Equal(change, oracle);
                Assert.Equal(change, actual.X);
                Assert.Equal(expected, actual.Z);
            }
        foreach (VisibilityCase c in TemporalVisibilityReference.Cases())
        {
            if (c.Valid) continue;
            Assert.Equal(0, probe.Run(c).Z);
        }
    }

    [GpuTheory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void LargeVisibilityStepsKeepDisplayShareInBothDirections(int entry, bool rising)
    {
        const int display = 2 * N;
        using var rig = new Rig(N, N, display, display, SceneNdc);
        using GpuRetireQueue retired = GpuRetireQueue.Create(rig.Device);
        rig.Renderer.SelectEntry((TemporalResolveEntry)entry, retired);
        using IGpuTexture signal = Signal(rig.Device, rising ? (byte)0 : (byte)255);
        float[] color = Grey((x, _) => 0.2f + 0.01f * (x % 3));
        rig.Fill(color, color, Motion((_, _) => Vector2.Zero));
        TemporalResolveUniforms cold = TemporalResolveMath.BuildUniforms(Still, Still, Vector2.Zero,
            N, N, display, display, false);
        TemporalResolveUniforms warm = TemporalResolveMath.BuildUniforms(Still, Still, Vector2.Zero,
            N, N, display, display, true);
        rig.BeginFrame(); rig.Resolve(cold, signal);
        float[] fresh = rig.ReadColor();
        rig.History.MarkValidAfterFrame(); rig.BeginFrame();
        float[] state = new float[display * display * 4];
        for (int i = 0; i < state.Length; i += 4) state[i] = 1;
        rig.FillHistory(fresh, state, SceneLinear);
        rig.Resolve(warm, signal);
        float[] unchanged = rig.ReadColor();
        Assert.NotEqual(fresh, unchanged);
        byte[] next = new byte[N * N];
        Array.Fill(next, rising ? (byte)255 : (byte)0);
        rig.Device.UpdateTexture(signal, next, 0, 0, N, N);
        rig.Resolve(warm, signal);
        float[] actual = rig.ReadColor();
        float[] expected = unchanged;
        float maximum = 0;
        for (int i = 0; i < expected.Length; i++) maximum = MathF.Max(maximum, MathF.Abs(expected[i] - actual[i]));
        output.WriteLine($"DISPLAY|{entry}|{rising}|maximum {maximum:R}");
        Assert.Equal(expected, actual);
    }

    [GpuTheory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    public void JitterOnlyVisibilityDoesNotReleaseThinDetail(int entry, int variation)
    {
        using var rig = new Rig();
        using GpuRetireQueue retired = GpuRetireQueue.Create(rig.Device);
        rig.Renderer.SelectEntry((TemporalResolveEntry)entry, retired);
        using IGpuTexture signal = Signal(rig.Device, 153);
        float[] color = Grey((x, _) => x == 3 ? 0.7f : 0.2f);
        rig.Fill(color, color, Motion((_, _) => Vector2.Zero));
        Vector2 priorJitter = variation == 0 ? Vector2.Zero : new(0.25f, -0.25f);
        rig.BeginFrame(); rig.Resolve(Uniforms(false, priorJitter), signal);
        rig.History.MarkValidAfterFrame(); rig.BeginFrame();
        rig.FillHistory(color, State((_, _) => new Vector2(1, 1)), SceneLinear);
        if (variation != 1) rig.Fill(color, color, Motion((_, _) => new Vector2(0.25f / N, 0)));
        rig.Resolve(Uniforms(true, variation == 0 ? Vector2.Zero : new(-0.25f, 0.25f)), signal);
        Assert.True(rig.ReadState()[At(3, 3) * 4 + 1] >= 0.875f);
        Assert.True(rig.ReadColor()[At(3, 3) * 4] > 0.6f);
    }

    [GpuTheory]
    [InlineData(0, "unseeded")]
    [InlineData(1, "unseeded")]
    [InlineData(0, "other surface")]
    [InlineData(1, "other surface")]
    [InlineData(0, "offscreen")]
    [InlineData(1, "offscreen")]
    [InlineData(0, "background")]
    [InlineData(1, "background")]
    public void RealResolveGatesVisibilityRelease(int entry, string invalid)
    {
        using var rig = new Rig();
        using GpuRetireQueue retired = GpuRetireQueue.Create(rig.Device);
        rig.Renderer.SelectEntry((TemporalResolveEntry)entry, retired);
        using IGpuTexture signal = Signal(rig.Device, 255);
        float[] color = Grey((x, _) => x == 3 ? 0.7f : 0.2f);
        rig.Fill(color, color, Motion((_, _) => Vector2.Zero));
        rig.BeginFrame();
        if (invalid != "unseeded")
        {
            rig.Resolve(Uniforms(false), signal);
            rig.History.MarkValidAfterFrame(); rig.BeginFrame();
        }
        rig.FillHistory(color, State((_, _) => new Vector2(1, 1)), invalid == "other surface" ? 3 : SceneLinear);
        Vector2 motion = invalid == "background" ? new(MotionMath.Sentinel) : invalid == "offscreen" ? new(2, 0) : Vector2.Zero;
        rig.Fill(color, color, Motion((_, _) => motion));
        rig.Device.UpdateTexture(signal, new byte[N * N], 0, 0, N, N);
        rig.Resolve(Uniforms(true), signal);
        float[] changedState = rig.ReadState(), changedColour = rig.ReadColor();
        byte[] unchanged = new byte[N * N]; Array.Fill(unchanged, (byte)255);
        rig.Device.UpdateTexture(signal, unchanged, 0, 0, N, N);
        rig.Resolve(Uniforms(true), signal);
        Assert.Equal(changedState, rig.ReadState());
        Assert.Equal(changedColour, rig.ReadColor());
        if (invalid == "unseeded" || invalid == "other surface")
            Assert.True(changedState[At(3, 3) * 4 + 1] >= 0.875f, invalid);
    }

    [GpuFact]
    public void OtherSurfaceAndEmptyFootprintsDoNotRelease()
    {
        using var probe = new TemporalVisibilityProbe();
        foreach (VisibilityCase c in TemporalVisibilityReference.Cases())
        {
            if (c.Valid) continue;
            Vector3 actual = probe.Run(c);
            Assert.Equal(0, actual.Y); Assert.Equal(0, actual.Z);
        }
    }

    [GpuFact]
    public void R8ConversionBoundariesAndMatchedR32ControlReportDecisionAndRenderedDifferences()
    {
        using var probe = new TemporalVisibilityProbe();
        using var render = new TemporalVisibilityFormatControl(probe.Device, Uniforms(true), SceneNdc);
        output.WriteLine($"{probe.Device.Backend} on {probe.Device.Capabilities.DeviceName}");
        foreach (float value in new[] { 0.09f, 25.49f / 255, 25.5f / 255, 25.51f / 255, 0.099f, 0.1f, 0.101f, 0.11f, 127.49f / 255, 0.5f, 127.51f / 255, 0.51f, 0.75f })
        {
            byte converted = probe.ReadR8Conversion(value);
            Assert.InRange(MathF.Abs(converted / 255f - value), 0, 0.5f / 255 + 1e-7f);
            var c = new VisibilityCase("format", value, 0);
            Array.Fill(c.Depth, 2); c.Depth[5] = 1;
            Vector3 r8 = probe.Run(c, true);
            Vector3 r32 = probe.Run(c);
            Assert.InRange(MathF.Abs(r8.X - r32.X), 0, 0.5f / 255 + 1e-6f);
            Assert.InRange(MathF.Abs(r8.X - converted / 255f), 0, 1e-7f);
            if (MathF.Abs(value - 0.1f) > 1f / 255) Assert.Equal(r32.Z, r8.Z);
            var a = render.Render(value, 0, GpuPixelFormat.R8UNorm);
            var b = render.Render(value, 0, GpuPixelFormat.R32Float);
            float mean = 0, peak = 0;
            for (int i = 0; i < a.Colour.Length; i++)
            {
                float difference = MathF.Abs(a.Colour[i] - b.Colour[i]);
                mean += difference / a.Colour.Length; peak = MathF.Max(peak, difference);
            }
            output.WriteLine($"input {value:R}, R8 byte {converted}, signed {r8.X:R}/{r32.X:R}, release {r8.Z}/{r32.Z}, colour mean {mean:R} max {peak:R}, lock {a.State[At(3, 3) * 4 + 1]}/{b.State[At(3, 3) * 4 + 1]}");
            // Quantization may cross the strict release threshold. A changed decision must remain observable.
            if (r8.Z != r32.Z)
            {
                Assert.True(peak > 0, "The boundary control must exercise the changed lock release.");
                Assert.NotEqual(a.State, b.State);
            }
            else
            {
                Assert.Equal(a.Colour, b.Colour); Assert.Equal(a.State, b.State);
            }
        }
    }

    [GpuTheory]
    [InlineData(0)]
    [InlineData(1)]
    public void DebugAndCountsReadTheSamePreWriteVisibilityAsTheResolve(int entry)
    {
        using var rig = new Rig();
        using GpuRetireQueue retired = GpuRetireQueue.Create(rig.Device);
        rig.Renderer.SelectEntry((TemporalResolveEntry)entry, retired);
        using IGpuTexture signal = Signal(rig.Device, 0);
        float[] current = Grey((_, _) => 0.2f);
        rig.Fill(current, current, Motion((_, _) => Vector2.Zero));
        rig.BeginFrame(); rig.Resolve(Uniforms(false, new Vector2(0.125f, -0.25f)), signal);
        rig.History.MarkValidAfterFrame(); rig.BeginFrame();
        rig.FillHistory(Grey((_, _) => 0.4f), State((_, _) => Vector2.One), SceneLinear);
        byte[] lit = new byte[N * N]; Array.Fill(lit, (byte)255);
        rig.Device.UpdateTexture(signal, lit, 0, 0, N, N);
        rig.Renderer.ArmCounts();
        rig.Resolve(Uniforms(true), signal);
        float[] resolved = rig.ReadState();
        Assert.Equal(lit, TemporalVisibilityHistoryGpuTests.Read(rig.Device,
            rig.Renderer.VisibilityHistory.Texture(rig.History.WriteIndex)));
        Assert.Equal(new byte[N * N], TemporalVisibilityHistoryGpuTests.Read(rig.Device,
            rig.Renderer.VisibilityHistory.Texture(rig.History.ReadIndex)));
        using IGpuTexture debug = rig.Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(N, N,
            GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using IGpuFramebuffer target = rig.Device.Factory.CreateFramebuffer(null, debug);
        using IGpuCommandList commands = rig.Device.Factory.CreateCommandList();
        using (GpuRecording.Open(rig.Device, commands, nameof(DebugAndCountsReadTheSamePreWriteVisibilityAsTheResolve)))
        {
            // A new recording needs its own ring-buffer uniform slice, while visibility retains the pre-write jitter.
            rig.Renderer.PrepareUniforms(commands, Uniforms(true), new TemporalDepthStoreUniforms { CurrentDepth = Depth });
            rig.Renderer.DrawDebugView(commands, SceneDebugView.History, target, target.Outputs);
        }
        rig.Device.Submit(commands); rig.Device.WaitForIdle();
        float[] view = TemporalTextureIo.Read(rig.Device, debug);
        for (int i = 0; i < N * N; i++)
        {
            Assert.Equal(1, resolved[i * 4]);
            Assert.Equal(0, resolved[i * 4 + 1]);
            for (int c = 0; c < 3; c++) Assert.Equal(resolved[i * 4], view[i * 4 + c]);
        }
        Assert.True(rig.Renderer.TryHarvestCounts(out int disoccluded, out int reactive, out int clipped));
        Assert.Equal((0, 0, TemporalCountProbe.TotalSamples), (disoccluded, reactive, clipped));
        output.WriteLine($"{rig.Device.Backend} on {rig.Device.Capabilities.DeviceName}, entry {entry}: "
            + $"debug confidence {resolved[0]:R}, lock {resolved[1]:R}, clipped {clipped}, old R8 remains zero");
    }

    static IGpuTexture Signal(IGpuDevice device, byte value)
    {
        IGpuTexture signal = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(N, N,
            GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
        byte[] bytes = new byte[N * N]; Array.Fill(bytes, value);
        device.UpdateTexture(signal, bytes, 0, 0, N, N);
        return signal;
    }
}
