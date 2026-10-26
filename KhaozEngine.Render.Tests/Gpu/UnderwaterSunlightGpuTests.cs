using System;
using System.Linq;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Acceptance for underwater key lighting. Ratios use the same water draw and look in both
/// captures. Exclusive refracted and dry patches assert each frame and their per-pixel change.
/// Within-frame shadow contrast separately measures occlusion without counting attenuation.</summary>
public sealed class UnderwaterSunlightGpuTests(UnderwaterSunlightScenes scenes, ITestOutputHelper output)
    : IClassFixture<UnderwaterSunlightScenes>
{
    [GpuFact]
    public void FloaterShadowFollowsTheRefractedRay()
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.ThinSlab);
        UnderwaterSunlightImage image = scenes.Capture(settings);
        UnderwaterSunlightImage off = scenes.Capture(settings with { Enabled = false });
        var measured = image.ShadowCentroid();
        var dry = off.ShadowCentroid();
        float airSegment = (settings.SlabBottom + 0.01f) * UnderwaterSunlightScenes.AirTangent;
        float inWater = measured.Offset - airSegment;
        float expected = UnderwaterSunlightScenes.WaterTangent;
        float dryExpected = airSegment + UnderwaterSunlightScenes.AirTangent;
        float airLimit = 0.75f * 1.0f * UnderwaterSunlightScenes.AirTangent;
        output.WriteLine($"on centroid={measured.Offset:F6}, off centroid={dry.Offset:F6}, air segment={airSegment:F6}, in-water displacement={inWater:F6} m, expected={expected:F6} +/-{UnderwaterSunlightAcceptance.RayAllowance:F6}, dry expected={dryExpected:F6}");
        Assert.True(MathF.Abs(dry.Offset - dryExpected) <= UnderwaterSunlightAcceptance.RayAllowance,
            $"The off shadow must follow the dry ray, got {dry.Offset:F6} m, expected {dryExpected:F6}.");
        AssertShadowSwitch(image, off, airSegment + expected - 0.04f, airSegment + expected + 0.04f,
            dryExpected - 0.04f, dryExpected + 0.04f);
        Assert.True(UnderwaterSunlightAcceptance.AcceptsRay(inWater) && inWater < airLimit,
            $"In-water displacement {inWater:F6} m, expected {expected:F6} +/-{UnderwaterSunlightAcceptance.RayAllowance:F6}, after subtracting {airSegment:F6} m of air travel.");
    }

    [GpuFact]
    public void SubmergedKeyIsAttenuatedAlongThePath() =>
        AssertAttenuation(new UnderwaterSunlightCase(Depth: UnderwaterSunlightAcceptance.AttenuationDepth));

    [GpuFact]
    public void SubmergedKeySpecularIsAttenuatedAlongThePath()
    {
        // Black albedo removes diffuse, so an unattenuated specular term cannot hide behind it.
        var settings = new UnderwaterSunlightCase(Depth: UnderwaterSunlightAcceptance.AttenuationDepth,
            Caster: false, SpecularOnly: true);
        UnderwaterSunlightImage on = scenes.Capture(settings);
        UnderwaterSunlightImage off = scenes.Capture(settings with { Enabled = false });
        UnderwaterSunlightImage matte = scenes.Capture(settings with { Enabled = false, SpecularStrength = 0f });
        Vector3 probe = new(-2f, settings.BedY, 1f);
        Vector3 control = off.Sample(probe), ratio = on.ChannelRatio(off, probe);
        Assert.True(control.X > 0.5f && control.Y > 0.5f && control.Z > 0.5f,
            $"The black specular receiver must show a strong key highlight, got {control}.");
        Assert.True(matte.Sample(probe).Length() <= 1f / 255f, "Black albedo without specular must stay black.");
        output.WriteLine($"specular-only RGB on/off={ratio}, expected={UnderwaterSunlightAcceptance.ExpectedTransmittance(settings.Depth)}, matte reference={matte.Sample(probe)}");
        Assert.True(UnderwaterSunlightAcceptance.AcceptsAttenuation(ratio, settings.Depth),
            $"Key specular RGB ratios {ratio} must match path transmittance within {UnderwaterSunlightAcceptance.RatioAllowance:F6} per channel.");
    }

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
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.ThinSlab,
            Depth: UnderwaterSunlightAcceptance.AttenuationDepth, ShadowStrength: 0f);
        UnderwaterSunlightImage image = scenes.Capture(settings);
        byte min = image.Visibility.Min(), max = image.Visibility.Max();
        output.WriteLine($"shadows-off visibility R8 range={min}..{max}, expected=255..255");
        Assert.True(min == 255 && max == 255, $"ShadowStrength=0 must export exactly one, got R8 {min}..{max}.");
        AssertAttenuation(settings, image);
    }

    [GpuFact]
    public void OrthographicCameraAttenuates() => AssertAttenuation(new UnderwaterSunlightCase(
        Depth: UnderwaterSunlightAcceptance.AttenuationDepth, Orthographic: true));

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
        UnderwaterSunlightImage off = scenes.Capture(settings with { Enabled = false });
        var measured = image.HullInteriorRatio();
        output.WriteLine($"eroded hull pixels={measured.Count}, maximum shadow/lit={measured.Maximum:F6}, limit=0.35");
        // These strips are exclusive to one footprint. The complete hull check below still includes
        // the overlap, where the dry frame is legitimately shadowed and cannot serve as a lit control.
        AssertShadowSwitch(image, off, 0.66f, 0.71f, 1.75f, 1.85f);
        Assert.True(measured.Maximum <= 0.35f,
            $"A hole remains under the hull. Maximum shadow/lit ratio {measured.Maximum:F6} over {measured.Count} interior pixels exceeds 0.35.");
    }

    [GpuFact]
    public void SubmergedRockShadowsTheBed()
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.SubmergedRock);
        UnderwaterSunlightImage image = scenes.Capture(settings);
        UnderwaterSunlightImage off = scenes.Capture(settings with { Enabled = false });
        var measured = image.ShadowCentroid(minZ: 0.15f);
        float expected = 0.5f * UnderwaterSunlightScenes.WaterTangent;
        Vector3 probe = new(0f, settings.BedY, expected);
        Vector2 pixel = image.Project(probe);
        float ratio = image.ShadowContrast((int)pixel.X, (int)pixel.Y);
        output.WriteLine($"rock centroid={measured.Offset:F6} m, expected near={expected:F6} +/-0.15 m, local shadow/lit={ratio:F6}, limit=0.65");
        AssertShadowSwitch(image, off, 0.365f, 0.415f, 0.84f, 0.88f);
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
        output.WriteLine($"closed-room brightness on={wet:F6}, off={dry:F6}, difference={MathF.Abs(wet - dry):F6}, allowance={2f / 255f:F6}, roof-removed control={openBrightness:F6}");
        Assert.True(openBrightness > 0.2f, $"Removing the room must expose a lit floor, got {openBrightness:F6}.");
        Assert.True(dry > 0.005f && dry < 0.05f, $"The closed-room control must show its dimly ambient-lit floor, got {dry:F6}.");
        Assert.True(MathF.Abs(wet - dry) <= 2f / 255f, $"Cave brightness changed from {dry:F6} to {wet:F6} with FloorY=null, exceeding 2/255.");
    }

    [GpuFact]
    public void AmbientIsNotAttenuatedUnderWater()
    {
        var settings = new UnderwaterSunlightCase(Depth: 3f, AmbientOnly: true);
        UnderwaterSunlightImage on = scenes.Capture(settings);
        UnderwaterSunlightImage off = scenes.Capture(settings with { Enabled = false });
        Vector3 probe = new(-2f, settings.BedY, 1f);
        Vector3 lit = off.Sample(probe), wet = on.Sample(probe);
        Vector3 difference = Vector3.Abs(wet - lit);
        output.WriteLine($"ambient-only RGB on={wet}, off={lit}, difference={difference}, allowance={1f / 255f:F6}, depth=3 m, ambient=(0.4,0.3,0.2), key=(0,0,0)");
        Assert.True(lit.X > 0.2f && lit.Y > 0.15f && lit.Z > 0.1f, $"The ambient-only bed must be visibly lit, got {lit}.");
        Assert.True(difference.X <= 1f / 255f && difference.Y <= 1f / 255f && difference.Z <= 1f / 255f,
            $"Ambient was attenuated underwater, on {wet}, off {lit}, differences {difference}.");
    }

    [GpuFact]
    public void PenumbraWidensWithDepth()
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.ThinSlab, Depth: 0.5f, Absorption: false);
        float Width(UnderwaterSunlightCase c)
        {
            UnderwaterSunlightImage image = scenes.Capture(c);
            float z = image.ShadowCentroid().Offset;
            return image.EdgeWidth10To90(z);
        }
        float shallow = Width(settings), deep = Width(settings with { Depth = 2.0f });
        float texel = UnderwaterSunlightScenes.ShadowTexelWorld(settings);
        float predicted = UnderwaterSunlightScenes.KernelRadiusTexels(2.0f, texel)
            / UnderwaterSunlightScenes.KernelRadiusTexels(0.5f, texel), ratio = deep / shallow;
        output.WriteLine($"10-90 widths shallow={shallow:F6} m, deep={deep:F6} m, ratio={ratio:F6}, predicted={predicted:F6} +/-25%, texel={texel:F6} m");
        Assert.True(deep > shallow && MathF.Abs(ratio - predicted) <= predicted * 0.25f,
            $"Penumbra width ratio {ratio:F6}, expected {predicted:F6} +/-25%. Shallow {shallow:F6} m, deep {deep:F6} m.");
    }

    [GpuFact]
    public void ShallowKernelMatchesTheShore()
    {
        AssertShallowKernel(0.001f, oneCodeLimit: true);
        AssertShallowKernel(0.01f, oneCodeLimit: false);
    }

    void AssertShallowKernel(float depth, bool oneCodeLimit)
    {
        var settings = new UnderwaterSunlightCase(UnderwaterSunlightGeometry.ThinSlab,
            Depth: depth, Absorption: false, SlabBottom: 1f);
        UnderwaterSunlightImage on = scenes.Capture(settings);
        UnderwaterSunlightImage dryKernel = scenes.Capture(settings with { Enabled = false });
        // The transverse X edge is inside both shadow footprints. Refraction moves the shadow along Z,
        // so comparing this common interior isolates the kernel without moving either the camera or caster.
        float centerZ = settings.SlabBottom * UnderwaterSunlightScenes.AirTangent
            + depth * UnderwaterSunlightScenes.WaterTangent;
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
        float texel = UnderwaterSunlightScenes.ShadowTexelWorld(settings);
        float blend = Math.Clamp(UnderwaterSunlightScenes.KernelRadiusTexels(depth, texel) - 1f, 0f, 1f);
        int allowance = oneCodeLimit ? 1 : (int)MathF.Ceiling(255f * blend) + 1;
        output.WriteLine($"depth={depth:F3} m, maximum key-visibility difference={maximum}/255, allowance={allowance}/255, blend={blend:F6}, edge range={darkest}..{brightest}, pixels={count}");
        Assert.True(darkest < 26 && brightest > 229, $"The shallow comparison must contain lit and shadowed bed pixels, got {darkest}..{brightest}.");
        Assert.True(maximum <= allowance, $"Shallow kernel differs from the dry kernel by {maximum}/255 at {depth:F3} m, allowance {allowance}/255.");
    }

    [Fact]
    public void AcceptanceWindowsRejectIncorrectRefractiveIndices()
    {
        foreach (float index in new[] { 1.2f, 1.333f, 1.5f })
        {
            // Independent Snell calculation, with no renderer or shader helper calls.
            double sine = Math.Cos(35d * Math.PI / 180d) / index;
            double cosine = Math.Sqrt(1d - sine * sine);
            float displacement = (float)(sine / cosine);
            double path = UnderwaterSunlightAcceptance.AttenuationDepth / cosine;
            Vector3 transmittance = new((float)Math.Exp(-0.6d * path),
                (float)Math.Exp(-0.3d * path), (float)Math.Exp(-0.15d * path));
            bool correctIndex = index == 1.333f;
            bool rayAccepted = UnderwaterSunlightAcceptance.AcceptsRay(displacement);
            bool attenuationAccepted = UnderwaterSunlightAcceptance.AcceptsAttenuation(transmittance,
                UnderwaterSunlightAcceptance.AttenuationDepth);
            Vector3 error = Vector3.Abs(transmittance - UnderwaterSunlightAcceptance.ExpectedTransmittance(
                UnderwaterSunlightAcceptance.AttenuationDepth));
            output.WriteLine($"n={index:F3}, displacement={displacement:F6}, RGB={transmittance}, RGB errors={error}, ray accepted={rayAccepted}, attenuation accepted={attenuationAccepted}");
            Assert.Equal(correctIndex, rayAccepted);
            Assert.Equal(correctIndex, attenuationAccepted);
            Assert.Equal(correctIndex, error.X <= UnderwaterSunlightAcceptance.RatioAllowance);
            Assert.Equal(correctIndex, error.Y <= UnderwaterSunlightAcceptance.RatioAllowance);
            Assert.Equal(correctIndex, error.Z <= UnderwaterSunlightAcceptance.RatioAllowance);
        }
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

    void AssertShadowSwitch(UnderwaterSunlightImage on, UnderwaterSunlightImage off,
        float refMinZ, float refMaxZ, float dryMinZ, float dryMaxZ)
    {
        var p = on.ShadowSwitch(off, refMinZ, refMaxZ, dryMinZ, dryMaxZ);
        output.WriteLine($"exclusive patches ref Z={refMinZ:F6}..{refMaxZ:F6} ({p.RefCount} pixels), dry Z={dryMinZ:F6}..{dryMaxZ:F6} ({p.DryCount} pixels). Ref on max={p.OnRefMax:F6}, off min={p.OffRefMin:F6}, per-pixel on/off max={p.RefOnOffMax:F6}. Dry on min={p.OnDryMin:F6}, off max={p.OffDryMax:F6}, minimum contrast change={p.MinimumChange:F6}");
        // Assert the control independently before checking the requested new behaviour.
        Assert.True(p.OffRefMin >= 0.95f && p.OffDryMax <= 0.35f,
            $"Off must be lit at the refracted position and shadowed at the dry position. Ref minimum {p.OffRefMin:F6}, dry maximum {p.OffDryMax:F6}.");
        Assert.True(p.OnRefMax <= 0.35f && p.OnDryMin >= 0.95f && p.RefOnOffMax <= 0.35f && p.MinimumChange >= 0.6f,
            $"On must reverse the dry shadow per pixel. Ref maximum {p.OnRefMax:F6}, dry minimum {p.OnDryMin:F6}, on/off maximum {p.RefOnOffMax:F6}, minimum change {p.MinimumChange:F6}.");
    }

    void AssertAttenuation(UnderwaterSunlightCase settings, UnderwaterSunlightImage? on = null)
    {
        on ??= scenes.Capture(settings);
        UnderwaterSunlightImage off = scenes.Capture(settings with { Enabled = false });
        // This clear patch is under the same zero-opacity water in both frames, away from any caster.
        Vector3 probe = new(-2f, settings.BedY, 1f);
        Vector3 ratio = on.ChannelRatio(off, probe);
        float path = settings.Depth * UnderwaterSunlightScenes.PathPerMetre;
        Vector3 expected = UnderwaterSunlightAcceptance.ExpectedTransmittance(settings.Depth);
        Vector3 error = Vector3.Abs(ratio - expected);
        output.WriteLine($"RGB on/off={ratio}, expected={expected}, errors={error}, L={path:F6} m, allowance={UnderwaterSunlightAcceptance.RatioAllowance:F6} per channel");
        // Also distinguish a water-pass contribution from the receiver's lighting in the report.
        UnderwaterSunlightImage noWater = scenes.Capture(settings with { Enabled = false, Water = false });
        output.WriteLine($"same-look off versus no-water probe RGB difference={Vector3.Abs(off.Sample(probe) - noWater.Sample(probe))}");
        Assert.True(UnderwaterSunlightAcceptance.AcceptsAttenuation(ratio, settings.Depth),
            $"Underwater key RGB ratios {ratio}, expected {expected} within {UnderwaterSunlightAcceptance.RatioAllowance:F6} per channel along L={path:F6} m.");
    }
}

/// <summary>One set of GPU acceptance windows, also checked against independent Snell predictions headlessly.</summary>
internal static class UnderwaterSunlightAcceptance
{
    internal const float RayAllowance = 0.04f;
    internal const float AttenuationDepth = 2f;
    internal const float RatioAllowance = 0.01f;

    internal static bool AcceptsRay(float waterDisplacement) =>
        MathF.Abs(waterDisplacement - UnderwaterSunlightScenes.WaterTangent) <= RayAllowance;

    internal static Vector3 ExpectedTransmittance(float depth)
    {
        float path = depth * UnderwaterSunlightScenes.PathPerMetre;
        return new Vector3(MathF.Exp(-0.6f * path), MathF.Exp(-0.3f * path), MathF.Exp(-0.15f * path));
    }

    internal static bool AcceptsAttenuation(Vector3 actual, float depth)
    {
        Vector3 error = Vector3.Abs(actual - ExpectedTransmittance(depth));
        return error.X <= RatioAllowance && error.Y <= RatioAllowance && error.Z <= RatioAllowance;
    }
}
