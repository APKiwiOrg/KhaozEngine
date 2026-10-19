using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class TemporalVisibilityComparisonGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
{
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
        output.WriteLine($"entry {entry}, ridge {ridge}: stored lock {state[At(3, 3) * 2 + 1]}, confidence {state[At(3, 3) * 2]}");
        Assert.Equal(0, state[At(3, 3) * 2 + 1]);
        if (!ridge)
        {
            Assert.Equal(1, state[At(3, 3) * 2]);
            Assert.InRange(rig.ReadColor()[At(3, 3) * 4], 0.19f, 0.21f);
        }
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
        Assert.True(rig.ReadState()[At(3, 3) * 2 + 1] >= 0.875f);
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
            Assert.True(changedState[At(3, 3) * 2 + 1] >= 0.875f, invalid);
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
        foreach (float value in new[] { 0.09f, 25.49f / 255, 25.5f / 255, 25.51f / 255, 0.099f, 0.1f, 0.101f, 0.11f, 0.5f, 0.75f })
        {
            byte converted = probe.ReadR8Conversion(value);
            Assert.InRange(MathF.Abs(converted / 255f - value), 0, 0.5f / 255 + 1e-7f);
            var c = new VisibilityCase("format", value, 0);
            Array.Fill(c.Depth, 2); c.Depth[5] = 1;
            Vector3 r8 = probe.Run(c, true), r32 = probe.Run(c);
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
            output.WriteLine($"input {value:R}, R8 byte {converted}, signed {r8.X:R}/{r32.X:R}, release {r8.Z}/{r32.Z}, colour mean {mean:R} max {peak:R}, lock {a.State[At(3, 3) * 2 + 1]}/{b.State[At(3, 3) * 2 + 1]}");
            if (value == 0.1f)
                Assert.True(peak > 0, "The boundary control must exercise clipping as well as stored lock release.");
            if (MathF.Abs(value - 0.1f) > 1f / 255)
            {
                Assert.Equal(a.Colour, b.Colour); Assert.Equal(a.State, b.State);
            }
        }
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
