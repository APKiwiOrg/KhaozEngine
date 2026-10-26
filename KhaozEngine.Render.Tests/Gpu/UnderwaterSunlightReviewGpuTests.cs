using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class UnderwaterSunlightReviewGpuTests(UnderwaterSunlightReviewScenes scenes, ITestOutputHelper output)
    : IClassFixture<UnderwaterSunlightReviewScenes>
{
    [GpuFact]
    public void ThinRoofAcrossFarToNearCascadeTransitionGolden()
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.CascadeRoof, Depth: 3f,
            Absorption: false, Caster: false, PenumbraPerMetre: 0f, CascadeBlend: 0f);
        UnderwaterSunlightImage open = scenes.Cascaded.Capture(settings);
        Matrix4x4[] matrices = scenes.Cascaded.CascadeMatrices;
        var gap = FindTransitionGap(open, matrices);
        output.WriteLine($"far-to-near band gap={gap.Gap:F6} m, roof top={gap.RoofY:F6} m, probe=({gap.X},{gap.Y})");
        Assert.True(gap.Gap > 0.01f, "The two fitted cascades must expose a measurable bias gap in the old bands.");
        settings = settings with { Caster = true, SlabBottom = gap.RoofY };
        UnderwaterSunlightImage closed = scenes.Cascaded.Capture(settings);
        Dump("cascade-roof-unblended", closed);
        Dump("cascade-roof-open", open);
        AssertDark(closed, open, gap.X, gap.Y, "unblended, zero widening");
        UnderwaterSunlightImage widened = scenes.Cascaded.Capture(settings with
        {
            PenumbraPerMetre = UnderwaterSunlightScenes.PenumbraPerMetre, CascadeBlend = 0.15f
        });
        Dump("cascade-roof-blended-widened", widened);
        AssertDark(widened, open, gap.X, gap.Y, "blended and widened");
    }

    void AssertDark(UnderwaterSunlightImage closed, UnderwaterSunlightImage open, int x, int y, string label)
    {
        int i = y * UnderwaterSunlightScenes.Size + x;
        int maximum = 0;
        for (int py = 192; py < 320; py++)
            for (int px = 192; px < 320; px++)
                maximum = Math.Max(maximum, closed.Visibility[py * UnderwaterSunlightScenes.Size + px]);
        output.WriteLine($"{label}: target visibility={closed.Visibility[i]}/255, interior maximum={maximum}/255, open={open.Visibility[i]}/255");
        Assert.Equal(255, open.Visibility[i]);
        Assert.True(closed.Visibility[i] <= 89 && maximum <= 89,
            $"The thin roof must keep the dry floor dark ({label}). Target {closed.Visibility[i]}/255, interior maximum {maximum}/255.");
    }

    // Place a one-millimetre roof strictly inside an old gap, using the live fitted cascades and a real
    // receiver pixel. This is geometry construction only. Darkness is asserted from GPU readback.
    static (int X, int Y, float RoofY, float Gap) FindTransitionGap(UnderwaterSunlightImage image, Matrix4x4[] matrices)
    {
        float Offset(int c) => 2f * 2.5f * MathF.Cos(35f * MathF.PI / 180f)
            / (2048f * new Vector3(matrices[c].M11, matrices[c].M21, matrices[c].M31).Length());
        int Select(Vector3 p)
        {
            for (int c = 0; c < matrices.Length; c++)
            {
                Vector4 clip = Vector4.Transform(new Vector4(p + Vector3.UnitY * Offset(c), 1f), matrices[c]);
                float margin = 4f / 2048f;
                if (MathF.Abs(clip.X) <= 1f - margin && MathF.Abs(clip.Y) <= 1f - margin && clip.Z >= 0f && clip.Z <= 1f)
                    return c;
            }
            return -1;
        }
        for (int y = 192; y < 320; y += 8)
            for (int x = 192; x < 320; x += 8)
            {
                Vector3 receiver = image.BedPoint(x, y);
                Vector3 entry = receiver + new Vector3(0f, image.Settings.Depth,
                    -image.Settings.Depth * UnderwaterSunlightScenes.WaterTangent);
                Vector2 pixel = new(x + 0.5f, y + 0.5f);
                float inner = Vector2.Dot(pixel, new Vector2(0.06711056f, 0.00583715f));
                float noise = 52.9829189f * (inner - MathF.Floor(inner));
                float jitter = noise - MathF.Floor(noise) - 0.5f;
                for (int slice = 0; slice < 3; slice++)
                {
                    float end = (slice + 1f + jitter) / 4f;
                    float nextEnd = slice == 2 ? 1f : (slice + 2f + jitter) / 4f;
                    Vector3 boundary = Vector3.Lerp(entry, receiver, end);
                    int previous = Select(boundary), next = Select(Vector3.Lerp(entry, receiver, nextEnd));
                    if (previous != 1 || next != 0) continue;
                    float roof = boundary.Y + (Offset(previous) + Offset(next)) * 0.5f;
                    if (roof <= -2.05f) continue; // Camera stays below the roof, inside the dry space.
                    return (x, y, roof, Offset(previous) - Offset(next));
                }
            }
        throw new InvalidOperationException("The underwater camera must cross a far-to-near cascade boundary above its eye.");
    }

    [GpuFact]
    public void UnsaturatedPenumbraFollowsPathLengthAcrossSunElevationsGolden()
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.PenumbraSlab,
            Depth: 2f, Absorption: false, PenumbraPerMetre: 0.018f, SunElevationDegrees: 75f);
        float Width(UnderwaterSunlightCase c)
        {
            UnderwaterSunlightImage image = scenes.Single.Capture(c);
            Dump($"penumbra-{c.SunElevationDegrees}", image);
            float elevation = c.SunElevationDegrees * MathF.PI / 180f;
            float sine = MathF.Cos(elevation) / 1.333f;
            // Measure through the slab's analytic shadow centre at both elevations.
            float z = (c.SlabBottom + 0.01f) / MathF.Tan(elevation)
                + c.Depth * sine / MathF.Sqrt(1f - sine * sine);
            return image.EdgeWidth10To90(z, keyVisibility: true);
        }
        float high = Width(settings), low = Width(settings with { SunElevationDegrees = 5f });
        float texel = UnderwaterSunlightScenes.ShadowTexelWorld(settings);
        float highRadius = UnderwaterSunlightScenes.KernelRadiusTexels(2f, texel, 75f, settings.PenumbraPerMetre);
        float lowRadius = UnderwaterSunlightScenes.KernelRadiusTexels(2f, texel, 5f, settings.PenumbraPerMetre);
        float predicted = lowRadius / highRadius, measured = low / high;
        output.WriteLine($"same-depth widths high={high:F6} m, low={low:F6} m, ratio={measured:F6}, path prediction={predicted:F6}, vertical prediction=1, radii={highRadius:F6},{lowRadius:F6}");
        Assert.True(highRadius > 2f && lowRadius < 8f, "Both captures must use the unsaturated disc kernel.");
        Assert.True(UnderwaterSunlightAcceptance.AcceptsPenumbraRatio(measured, predicted),
            $"Measured ratio {measured:F6} must follow path prediction {predicted:F6}.");
        Assert.False(UnderwaterSunlightAcceptance.AcceptsPenumbraRatio(measured, 1f),
            $"Measured ratio {measured:F6} must reject the vertical-depth prediction of no change.");
    }

    [Fact]
    public void UnsaturatedPenumbraWindowRejectsVerticalDepth()
    {
        var settings = new UnderwaterSunlightCase(Depth: 2f);
        double texel = UnderwaterSunlightScenes.ShadowTexelWorld(settings);
        double sine = Math.Cos(5d * Math.PI / 180d) / 1.333d;
        double highSine = Math.Cos(75d * Math.PI / 180d) / 1.333d;
        double high = 1d + 2d / Math.Sqrt(1d - highSine * highSine) * 0.018d / texel;
        double low = 1d + 2d / Math.Sqrt(1d - sine * sine) * 0.018d / texel;
        float predicted = (float)(low / high);
        Assert.InRange(high, 2d, 7.99d);
        Assert.InRange(low, 2d, 7.99d);
        Assert.True(UnderwaterSunlightAcceptance.AcceptsPenumbraRatio(predicted, predicted));
        Assert.False(UnderwaterSunlightAcceptance.AcceptsPenumbraRatio(1f, predicted));
        Assert.False(UnderwaterSunlightAcceptance.AcceptsPenumbraRatio(predicted, 1f));
    }

    [GpuFact]
    public void SharedCascadeSceneMatchesFreshAfterChangingConfigurations()
    {
        scenes.Cascaded.Capture(new UnderwaterSunlightCase(UnderwaterSunlightGeometry.CascadeRoof, Depth: 3f, SlabBottom: -1.5f));
        scenes.Cascaded.Capture(new UnderwaterSunlightCase(Caster: false, SunElevationDegrees: 5f));
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.CascadeRoof, Depth: 3f, SlabBottom: -1.5f);
        UnderwaterSunlightImage reused = scenes.Cascaded.Capture(settings);
        using var fresh = new UnderwaterSunlightScenes(2);
        UnderwaterSunlightImage control = fresh.Capture(settings);
        Assert.Equal(control.Rgba, reused.Rgba);
        Assert.Equal(control.Visibility, reused.Visibility);
    }

    static void Dump(string name, UnderwaterSunlightImage image)
    {
        string? directory = Environment.GetEnvironmentVariable("KE_UNDERWATER_FINAL_DUMP");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        PngWriter.Save(Path.Combine(directory, name + ".png"), image.Rgba, UnderwaterSunlightScenes.Size, UnderwaterSunlightScenes.Size);
    }
}

public sealed class UnderwaterSunlightReviewScenes : IDisposable
{
    internal UnderwaterSunlightScenes Single { get; } = new();
    internal UnderwaterSunlightScenes Cascaded { get; } = new(2);
    public void Dispose()
    {
        Cascaded.Dispose();
        Single.Dispose();
    }
}
