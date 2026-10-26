using System;
using System.Linq;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Acceptance for underwater key lighting. Ratios use the same water draw and look in both
/// captures. Shadow contrast is normalized by an adjacent lit patch's on/off ratio so attenuation
/// cannot count as occlusion. The measurement controls keep the water pass.</summary>
public sealed class UnderwaterSunlightGpuTests(UnderwaterSunlightScenes scenes, ITestOutputHelper output)
    : IClassFixture<UnderwaterSunlightScenes>
{
    [GpuFact]
    public void FloaterShadowFollowsTheRefractedRay()
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.ThinSlab);
        UnderwaterSunlightImage image = scenes.Capture(settings);
        UnderwaterSunlightImage lit = scenes.Capture(settings with { Enabled = false });
        var measured = image.ShadowCentroid(lit);
        float expected = 1.0f * UnderwaterSunlightScenes.WaterTangent;
        float airLimit = 0.75f * 1.0f * UnderwaterSunlightScenes.AirTangent;
        output.WriteLine($"centroid={measured.Offset:F6} m, weight={measured.Weight:F3}, expected={expected:F6} +/-15%, air limit={airLimit:F6}");
        Assert.True(MathF.Abs(measured.Offset - expected) <= expected * 0.15f && measured.Offset < airLimit,
            $"Floater shadow centroid {measured.Offset:F6} m, expected {expected:F6} +/-15% and below {airLimit:F6} m. The air ray lands near 1.428148 m.");
    }

    [GpuFact]
    public void SubmergedKeyIsAttenuatedAlongThePath() => AssertAttenuation(new UnderwaterSunlightCase());

    [GpuFact]
    public void WaterlineHasNoSeam()
    {
        UnderwaterSunlightImage image = scenes.Capture(new UnderwaterSunlightCase(UnderwaterSunlightGeometry.Bank));
        var step = image.AdjacentRowStep();
        output.WriteLine($"waterline step={step.AtWaterline:F6}, adjacent maximum={step.Beside:F6}, allowance={2f / 255f:F6}");
        Assert.True(step.AtWaterline <= step.Beside + 2f / 255f,
            $"Waterline step {step.AtWaterline:F6} exceeds adjacent-row maximum {step.Beside:F6} plus 2/255.");
    }

    [GpuFact]
    public void ShadowsOffStillAttenuates()
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.ThinSlab, ShadowStrength: 0f);
        UnderwaterSunlightImage image = scenes.Capture(settings);
        byte min = image.Visibility.Min(), max = image.Visibility.Max();
        output.WriteLine($"shadows-off visibility R8 range={min}..{max}, expected=255..255");
        Assert.True(min == 255 && max == 255, $"ShadowStrength=0 must export exactly one, got R8 {min}..{max}.");
        AssertAttenuation(settings, image);
    }

    [GpuFact]
    public void OrthographicCameraAttenuates() => AssertAttenuation(new UnderwaterSunlightCase(Orthographic: true));

    // R4 compares two live frames. There is no committed hash or pre-task reference image.
    [GpuFact]
    public void OptInDoesNotChangeReceiversAboveTheWater()
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.AboveWater);
        UnderwaterSunlightImage on = scenes.Capture(settings);
        UnderwaterSunlightImage off = scenes.Capture(settings with { Enabled = false });
        int different = on.Rgba.Zip(off.Rgba).Count(p => p.First != p.Second);
        output.WriteLine($"above-water differing bytes={different}, expected=0");
        Assert.Equal(off.Rgba, on.Rgba);
    }

    [GpuFact]
    public void StraddlingFloaterShadowHasNoHoleUnderTheHull()
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.StraddlingHull);
        UnderwaterSunlightImage image = scenes.Capture(settings);
        UnderwaterSunlightImage lit = scenes.Capture(settings with { Enabled = false });
        var measured = image.HullInteriorRatio(lit);
        output.WriteLine($"eroded hull pixels={measured.Count}, maximum shadow/lit={measured.Maximum:F6}, limit=0.35");
        Assert.True(measured.Maximum <= 0.35f,
            $"A hole remains under the hull. Maximum shadow/lit ratio {measured.Maximum:F6} over {measured.Count} interior pixels exceeds 0.35.");
    }

    [GpuFact]
    public void SubmergedRockShadowsTheBed()
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.SubmergedRock);
        UnderwaterSunlightImage image = scenes.Capture(settings);
        UnderwaterSunlightImage lit = scenes.Capture(settings with { Enabled = false });
        var measured = image.ShadowCentroid(lit, minZ: 0.15f);
        float expected = 0.5f * UnderwaterSunlightScenes.WaterTangent;
        Vector3 probe = new(0f, settings.BedY, expected);
        Vector2 pixel = image.Project(probe);
        float ratio = image.ShadowRatio(lit, (int)pixel.X, (int)pixel.Y);
        output.WriteLine($"rock centroid={measured.Offset:F6} m, expected near={expected:F6} +/-0.15 m, local shadow/lit={ratio:F6}, limit=0.65");
        Assert.True(MathF.Abs(measured.Offset - expected) < 0.15f && ratio < 0.65f,
            $"Submerged rock shadow is absent at its refracted position. Centroid {measured.Offset:F6}, expected near {expected:F6}, shadow/lit {ratio:F6}.");
    }

    [GpuFact]
    public void DryCaveUnderALakeStaysDark()
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.Cave);
        UnderwaterSunlightImage on = scenes.Capture(settings);
        UnderwaterSunlightImage off = scenes.Capture(settings with { Enabled = false });
        Vector3 probe = new(0f, settings.BedY, 0f);
        float wet = UnderwaterSunlightImage.Brightness(on.Sample(probe));
        float dry = UnderwaterSunlightImage.Brightness(off.Sample(probe));
        UnderwaterSunlightImage open = scenes.Capture(settings with { Enabled = false, Caster = false });
        float openBrightness = UnderwaterSunlightImage.Brightness(open.Sample(probe));
        output.WriteLine($"closed-room brightness on={wet:F6}, off={dry:F6}, difference={MathF.Abs(wet - dry):F6}, allowance=0.02, roof-removed control={openBrightness:F6}");
        Assert.True(openBrightness > 0.2f, $"Removing the room must expose a lit floor, got {openBrightness:F6}.");
        Assert.True(dry > 0.005f && dry < 0.05f, $"The closed-room control must show its dimly ambient-lit floor, got {dry:F6}.");
        Assert.True(MathF.Abs(wet - dry) <= 0.02f, $"Cave brightness changed from {dry:F6} to {wet:F6} with FloorY=null.");
    }

    [GpuFact]
    public void PenumbraWidensWithDepth()
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.ThinSlab, Depth: 0.5f, Absorption: false);
        float Width(UnderwaterSunlightCase c)
        {
            UnderwaterSunlightImage image = scenes.Capture(c);
            UnderwaterSunlightImage lit = scenes.Capture(c with { Enabled = false });
            float z = image.ShadowCentroid(lit).Offset;
            return image.EdgeWidth10To90(lit, z);
        }
        float shallow = Width(settings), deep = Width(settings with { Depth = 2.0f });
        float texel = UnderwaterSunlightScenes.ShadowTexelWorld(settings);
        float Radius(float depth) => MathF.Min(8f, 1f + depth * UnderwaterSunlightScenes.PathPerMetre * 0.03f / texel);
        float predicted = Radius(2.0f) / Radius(0.5f), ratio = deep / shallow;
        output.WriteLine($"10-90 widths shallow={shallow:F6} m, deep={deep:F6} m, ratio={ratio:F6}, predicted={predicted:F6} +/-25%, texel={texel:F6} m");
        Assert.True(deep > shallow && MathF.Abs(ratio - predicted) <= predicted * 0.25f,
            $"Penumbra width ratio {ratio:F6}, expected {predicted:F6} +/-25%. Shallow {shallow:F6} m, deep {deep:F6} m.");
    }

    [GpuFact]
    public void ShallowKernelMatchesTheShore()
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.ThinSlab,
            Depth: 0.01f, Absorption: false, SlabBottom: 1f);
        UnderwaterSunlightImage on = scenes.Capture(settings);
        UnderwaterSunlightImage dryKernel = scenes.Capture(settings with { Enabled = false });
        // The transverse X edge is inside both shadow footprints. Refraction moves the shadow along Z,
        // so comparing this common interior isolates the kernel without moving either the camera or caster.
        float centerZ = settings.SlabBottom * UnderwaterSunlightScenes.AirTangent
            + 0.01f * UnderwaterSunlightScenes.WaterTangent;
        int maximum = 0, count = 0, darkest = 255, brightest = 0;
        for (int y = 2; y < UnderwaterSunlightScenes.Size - 2; y++)
            for (int x = 2; x < UnderwaterSunlightScenes.Size - 2; x++)
            {
                Vector3 p = on.BedPoint(x, y);
                if (MathF.Abs(p.X) > 1.4f || MathF.Abs(p.Z - centerZ) > 0.05f) continue;
                int i = y * UnderwaterSunlightScenes.Size + x;
                maximum = Math.Max(maximum, Math.Abs(on.Visibility[i] - dryKernel.Visibility[i]));
                darkest = Math.Min(darkest, on.Visibility[i]);
                brightest = Math.Max(brightest, on.Visibility[i]);
                count++;
            }
        Assert.True(count > 1000, $"The shallow bed comparison must contain the shadow edge, got {count} pixels.");
        output.WriteLine($"0.01 m maximum key-visibility difference={maximum}/255, allowance=1/255, edge range={darkest}..{brightest}, pixels={count}");
        Assert.True(darkest < 26 && brightest > 229, $"The shallow comparison must contain lit and shadowed bed pixels, got {darkest}..{brightest}.");
        Assert.True(maximum <= 1, $"Shallow kernel differs from the dry kernel by {maximum}/255 at 0.01 m.");
    }

    [GpuFact]
    public void SharedSceneMatchesFreshAfterChangingConfigurations()
    {
        scenes.Capture(new UnderwaterSunlightCase(UnderwaterSunlightGeometry.Cave));
        scenes.Capture(new UnderwaterSunlightCase(UnderwaterSunlightGeometry.Bank, Enabled: false));
        scenes.Capture(new UnderwaterSunlightCase(Depth: 2f, Orthographic: true, ShadowStrength: 0f));
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.ThinSlab);
        UnderwaterSunlightImage reused = scenes.Capture(settings);
        using var fresh = new UnderwaterSunlightScenes();
        UnderwaterSunlightImage control = fresh.Capture(settings);
        output.WriteLine($"shared/fresh differing color bytes={reused.Rgba.Zip(control.Rgba).Count(p => p.First != p.Second)}, visibility bytes={reused.Visibility.Zip(control.Visibility).Count(p => p.First != p.Second)}");
        Assert.Equal(control.Rgba, reused.Rgba);
        Assert.Equal(control.Visibility, reused.Visibility);
    }

    void AssertAttenuation(UnderwaterSunlightCase settings, UnderwaterSunlightImage? on = null)
    {
        on ??= scenes.Capture(settings);
        UnderwaterSunlightImage off = scenes.Capture(settings with { Enabled = false });
        // This clear patch is under the same zero-opacity water in both frames, away from any caster.
        Vector3 probe = new(-2f, settings.BedY, 1f);
        Vector3 ratio = on.ChannelRatio(off, probe);
        float path = settings.Depth * UnderwaterSunlightScenes.PathPerMetre;
        Vector3 expected = new(MathF.Exp(-0.6f * path), MathF.Exp(-0.3f * path), MathF.Exp(-0.15f * path));
        Vector3 error = Vector3.Abs(ratio - expected);
        output.WriteLine($"RGB on/off={ratio}, expected={expected}, errors={error}, L={path:F6} m, allowance=0.03 per channel");
        // Also distinguish a water-pass contribution from the receiver's lighting in the report.
        UnderwaterSunlightImage noWater = scenes.Capture(settings with { Enabled = false, Water = false });
        output.WriteLine($"same-look off versus no-water probe RGB difference={Vector3.Abs(off.Sample(probe) - noWater.Sample(probe))}");
        Assert.True(error.X <= 0.03f && error.Y <= 0.03f && error.Z <= 0.03f,
            $"Underwater key RGB ratios {ratio}, expected {expected} within 0.03 per channel along L={path:F6} m.");
    }
}
