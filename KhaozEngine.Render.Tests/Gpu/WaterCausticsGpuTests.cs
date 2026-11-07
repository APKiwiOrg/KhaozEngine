using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Absolute acceptance for underwater caustics on synthetic scenes. Bands are measured against
/// <see cref="CausticOracle"/>, the design's forward map evaluated independently in double precision, never against
/// another render. Comparisons with M2 alone use the same scene and water with only the opt-in off.
/// </summary>
public sealed class WaterCausticsGpuTests(WaterCausticsScenes scenes, ITestOutputHelper output)
    : IClassFixture<WaterCausticsScenes>
{
    /// <summary>One map texel, the design's tolerance for where a band lands.</summary>
    const double Texel = WaterSurfaceWindow.MapTexelMetres;

    [GpuFact]
    public void Flat_surface_gives_a_term_of_exactly_one()
    {
        var flat = new WaterCausticsCase { NormalStrength = 0f };
        WaterCausticsImage on = scenes.Capture(flat);
        WaterCausticsImage off = scenes.Capture(flat with { Caustics = false });
        Assert.Equal(1, on.Diagnostics.DrawnPlanes);
        AssertIdentical(off, on, "flat surface");
    }

    [GpuFact]
    public void Allocated_atlas_leaves_opted_out_frames_unchanged()
    {
        var ripple = new WaterCausticsCase();
        scenes.Capture(ripple);
        Assert.NotNull(scenes.Scene.WaterCausticMapForTests);
        WaterCausticsImage shared = scenes.Capture(ripple with { Caustics = false });
        using var fresh = new WaterCausticsScenes();
        WaterCausticsImage control = fresh.Capture(ripple with { Caustics = false });
        Assert.Null(fresh.Scene.WaterCausticMapForTests);
        AssertIdentical(control, shared, "opted out after an atlas");
    }

    /// <summary>Each receiver sits exactly on a slice, so strength times depth is one metre in both rows: strength 1 at
    /// the 1 m slice and strength 0.5 at the 2 m slice. A wrong deflection sign moves a band half a wavelength, a
    /// flipped map mirrors the diagonal bands, and a wrong entry moves them by the receiver's refracted run.</summary>
    [GpuTheory]
    [InlineData(50f, 0f, 1f, 1f)]
    [InlineData(35f, 135f, 0.5f, 2f)]
    public void Single_ripple_focuses_at_its_analytic_place(float elevation, float azimuth, float strength, float depth) =>
        AssertBandsAtOracle(new WaterCausticsCase
        {
            SunElevationDegrees = elevation,
            SunAzimuthDegrees = azimuth,
            Strength = strength,
            ReceiverDepth = depth,
        });

    [GpuFact]
    public void Slice_mean_is_one_over_whole_wavelengths()
    {
        // Heading 0 puts the bands along Z, so 128 texel columns hold exactly four 2 m wavelengths. The region keeps
        // 2 m inside the rectangle, beyond any deflection at these slices.
        var settings = new WaterCausticsCase { NormalStrength = 0.02f, Heading = 0f };
        scenes.Capture(settings);
        CausticMapReadback map = scenes.ReadMap();
        CausticOracle oracle = settings.Oracle();
        int column = map.Column(-4f), row = map.Row(-3f);
        const int columns = 128, rows = 96;
        float[] fractions = [0.125f, 0.25f, 0.5f, 1f];
        int tested = 0;
        for (int slice = 0; slice < 4; slice++)
        {
            double scaledDepth = settings.Strength * WaterCausticsScenes.SliceDepth * fractions[slice];
            double sum = 0d, maximum = 0d;
            for (int z = row; z < row + rows; z++)
                for (int x = column; x < column + columns; x++)
                {
                    float value = map.Slice(0, x, z, slice);
                    sum += value;
                    maximum = Math.Max(maximum, value);
                }
            double mean = sum / (columns * rows);
            bool shortOfFocus = scaledDepth < oracle.FocalDepth;
            output.WriteLine($"slice {slice} at {scaledDepth:F3} m: mean={mean:F5}, maximum={maximum:F4}, oracle peak={oracle.PeakTerm(scaledDepth):F4}, focal depth={oracle.FocalDepth:F3} m, gated={shortOfFocus}");
            if (!shortOfFocus) continue;
            tested++;
            Assert.True(maximum < 16d, $"Slice {slice} reaches the clamp ({maximum:F4}), so its mean is not conserved.");
            Assert.True(Math.Abs(mean - 1d) <= 0.01d, $"Slice {slice} mean {mean:F5} is not 1 within 1%.");
        }
        Assert.True(tested >= 2, $"Only {tested} slices lie short of focus.");
    }

    /// <summary>Both halves share the scene look and land identical shared-edge vertices, so the two maps differ only in
    /// the order half floats add, a few units in the last place, inside the 1/64 the brief allows.</summary>
    [GpuFact]
    public void Adjacent_rectangles_match_one_rectangle()
    {
        var whole = new WaterCausticsCase();
        scenes.Capture(whole);
        CausticMapReadback one = scenes.ReadMap();
        WaterCausticsImage split = scenes.Capture(whole with { Layout = WaterCausticsLayout.Split });
        CausticMapReadback two = scenes.ReadMap();
        Assert.Equal((2, 1), (split.Diagnostics.DrawnPlanes, split.Diagnostics.Layers));
        // The second body can grow the atlas, so the comparison walks layer 0 of each readback.
        Assert.Equal(one.Window, two.Window);
        double worst = 0d;
        for (int slice = 0; slice < 4; slice++)
        {
            double sliceWorst = 0d;
            int column = 0, row = 0, over = 0;
            for (int z = 0; z < one.Height; z++)
                for (int x = 0; x < one.Height; x++)
                {
                    double difference = Math.Abs(one.Slice(0, x, z, slice) - two.Slice(0, x, z, slice));
                    if (difference > 1d / 64d) over++;
                    if (difference > sliceWorst) (sliceWorst, column, row) = (difference, x, z);
                }
            float wx = one.Window.MinX + (column + 0.5f) * WaterSurfaceWindow.MapTexelMetres;
            float wz = one.Window.MinZ + (row + 0.5f) * WaterSurfaceWindow.MapTexelMetres;
            output.WriteLine($"slice {slice}: max map difference={sliceWorst:F6} at ({wx:F4}, {wz:F4}) m, one={one.Slice(0, column, row, slice):F4}, two={two.Slice(0, column, row, slice):F4}, texels over 1/64={over}");
            worst = Math.Max(worst, sliceWorst);
        }
        Assert.True(worst <= 1d / 64d, $"Cutting the water in two changed the map by {worst:F6}.");
    }

    /// <summary>The same adjacent-row measure as M2's waterline fact, on a bank whose bands reach the waterline.</summary>
    [GpuFact]
    public void Waterline_has_no_seam()
    {
        var bank = new WaterCausticsCase { Layout = WaterCausticsLayout.Bank };
        (float AtWaterline, float Beside) Step(WaterCausticsImage image) => new UnderwaterSunlightImage(image.Rgba,
            image.Visibility, image.ViewProjection, new UnderwaterSunlightCase()).AdjacentRowStep();
        WaterCausticsImage bands = scenes.Capture(bank), alone = scenes.Capture(bank with { Caustics = false });
        var on = Step(bands);
        var off = Step(alone);
        // The bank is submerged for z below 0 and reaches 1 m deep at the view's edge, so bands must cover it.
        int wet = 0, banded = 0;
        foreach (var (x, y, i) in WaterCausticsImage.Interior())
        {
            if (bands.Local(x, y).Z > -0.5d) continue;
            wet++;
            if (!Same(bands, alone, i)) banded++;
        }
        output.WriteLine($"waterline step={on.AtWaterline:F6}, adjacent maximum={on.Beside:F6}, M2 alone step={off.AtWaterline:F6}, adjacent={off.Beside:F6}, allowance={2f / 255f:F6}, banded wet pixels={banded} of {wet}");
        Assert.True(banded > wet / 2, $"Only {banded} of {wet} wet bank pixels carry bands.");
        Assert.True(on.AtWaterline <= on.Beside + 2f / 255f,
            $"Waterline step {on.AtWaterline:F6} exceeds adjacent-row maximum {on.Beside:F6} plus 2/255.");
    }

    /// <summary>The map outside a rectangle holds 1 beyond the farthest any ray lands, plus the bilinear read's texel.</summary>
    [GpuFact]
    public void Entry_beyond_the_deflection_reads_one()
    {
        var settings = new WaterCausticsCase { Layout = WaterCausticsLayout.Narrow, ReceiverDepth = 3f };
        WaterCausticsImage on = scenes.Capture(settings);
        WaterCausticsImage off = scenes.Capture(settings with { Caustics = false });
        CausticOracle oracle = settings.Oracle();
        // The deepest slice deflects farthest, and deflection grows linearly with depth.
        double reach = oracle.MaxDeflection(settings.Strength * WaterCausticsScenes.SliceDepth) + Texel;
        var (perX, perZ) = oracle.EntryPerMetre;
        int beyond = 0, beyondDifferent = 0, inside = 0, insideDifferent = 0;
        foreach (var (x, y, i) in WaterCausticsImage.Interior())
        {
            var (lx, lz) = on.Local(x, y);
            if (lx <= -2d + 1d / 64d || lx >= 6d) continue;
            double ex = lx + perX * settings.ReceiverDepth, ez = lz + perZ * settings.ReceiverDepth;
            bool different = !Same(on, off, i);
            if (ex < -2d - reach)
            {
                beyond++;
                if (different) beyondDifferent++;
            }
            else if (ex > -2d + reach && ex < 6d - reach && Math.Abs(ez) < 6d - reach)
            {
                inside++;
                if (different) insideDifferent++;
            }
        }
        output.WriteLine($"reach={reach:F4} m, entries beyond: {beyond} pixels, {beyondDifferent} differ from M2 alone; entries inside: {inside} pixels, {insideDifferent} differ");
        Assert.True(beyond > 1000 && inside > 1000, $"Too few receivers: {beyond} beyond, {inside} inside.");
        Assert.True(insideDifferent > inside / 2, "The control receivers must carry bands.");
        Assert.Equal(0, beyondDifferent);
    }

    [GpuFact]
    public void Stacked_bodies_keep_their_own_bands()
    {
        var settings = new WaterCausticsCase { Layout = WaterCausticsLayout.Stacked };
        WaterCausticsImage on = scenes.Capture(settings);
        WaterCausticsImage off = scenes.Capture(settings with { Caustics = false });
        Assert.Equal(new WaterCausticDiagnostics(2, 2, 0, 0, 2), on.Diagnostics);
        BandFit upper = on.Bands(off, settings.Oracle(), 1d, 1d, (x, _) => x < -0.25d);
        BandFit lower = on.Bands(off, settings.Oracle(lower: true), 1d, 1d, (x, _) => x > 0.25d);
        output.WriteLine($"upper body: {upper}");
        output.WriteLine($"lower body: {lower}");
        AssertFit(upper, "upper body");
        AssertFit(lower, "lower body");
    }

    /// <summary>Sizing counts the two far bodies, so the atlas holds four layers while the window uses one. Receivers
    /// address the atlas by its capacity, so the river reads its own column and its bands land where a one-layer atlas
    /// puts them. Both maps are rendered from the same window, and the column scale is a power of two, so the reads
    /// agree to the sampler's sub-texel precision and the fitted bands within one profile bin.</summary>
    [GpuFact]
    public void Shared_layer_river_reads_its_own_layer_in_a_larger_atlas()
    {
        var river = new WaterCausticsCase { Layout = WaterCausticsLayout.SharedRiver };
        WaterCausticsImage on = scenes.Capture(river);
        Assert.Equal(new WaterCausticDiagnostics(3, 1, 0, 0, 1), on.Diagnostics);
        Assert.Equal(4, scenes.ReadMap().Layers);
        WaterCausticsImage off = scenes.Capture(river with { Caustics = false });
        BandFit shared = on.Bands(off, river.Oracle(), 1d, 1d);

        using var fresh = new WaterCausticsScenes();
        var alone = river with { Layout = WaterCausticsLayout.Single };
        WaterCausticsImage single = fresh.Capture(alone);
        Assert.Equal(1, fresh.ReadMap().Layers);
        BandFit reference = single.Bands(fresh.Capture(alone with { Caustics = false }), alone.Oracle(), 1d, 1d);
        output.WriteLine($"four-layer atlas: {shared}");
        output.WriteLine($"one-layer atlas: {reference}");
        AssertFit(shared, "river in a four-layer atlas");
        Assert.Equal(reference.Bands, shared.Bands);
        for (int band = 0; band < shared.Bands; band++)
            Assert.True(Math.Abs(shared.Errors[band] - reference.Errors[band]) <= WaterCausticsImage.BinMetres,
                $"Band {band} sits at {shared.Errors[band]:F4} m in the larger atlas and {reference.Errors[band]:F4} m in one layer.");
    }

    [GpuFact]
    public void Pattern_moves_with_time_and_the_same_time_repeats()
    {
        var first = new WaterCausticsCase();
        var later = first with { Time = 1.5f };
        WaterCausticsImage a = scenes.Capture(first);
        WaterCausticsImage b = scenes.Capture(later);
        WaterCausticsImage again = scenes.Capture(first);
        WaterCausticsImage off = scenes.Capture(first with { Caustics = false });
        AssertIdentical(a, again, "the same time");
        BandFit atFirst = a.Bands(off, first.Oracle(), 1d, 1d), atLater = b.Bands(off, later.Oracle(), 1d, 1d);
        double moved = first.Oracle().Distance(atLater.Predicted, atFirst.Predicted);
        output.WriteLine($"time 0: {atFirst}");
        output.WriteLine($"time 1.5 s: {atLater}, predicted movement={moved:F4} m");
        Assert.True(Math.Abs(moved) > 4d * Texel, "The time step must move the bands measurably.");
        AssertFit(atFirst, "time 0");
        AssertFit(atLater, "time 1.5 s");
    }

    /// <summary>Review focus 2: the lattice and the ripple phase are absolute, so moving the render origin under a far
    /// scene moves nothing on the world. Both renders are fitted against the same absolute prediction.</summary>
    [GpuFact]
    public void Bands_stay_on_the_world_when_the_render_origin_rebases()
    {
        var far = new WaterCausticsCase { Origin = new Vector3(1000.3f, 0f, -2000.7f) };
        var rebased = far with { RenderOffset = new Vector3(17.1f, 0f, -9.3f) };
        BandFit Fit(WaterCausticsCase c) => scenes.Capture(c).Bands(scenes.Capture(c with { Caustics = false }),
            c.Oracle(), 1d, 1d);
        BandFit atOrigin = Fit(far), moved = Fit(rebased);
        output.WriteLine($"render origin at the scene: {atOrigin}");
        output.WriteLine($"render origin moved: {moved}");
        AssertFit(atOrigin, "render origin at the scene");
        AssertFit(moved, "render origin moved");
    }

    [GpuFact]
    public void Shadows_off_receiver_still_receives_the_term()
    {
        WaterCausticsImage on = AssertBandsAtOracle(new WaterCausticsCase { ShadowStrength = 0f });
        Assert.Equal(255, on.Visibility.Min());
    }

    /// <summary>A shadow-only caster darkens part of a bright band. Where both renders report no key visibility, the
    /// term has nothing to multiply and the pixels match M2 alone. Beside the shadow the same band is bright, at least
    /// half its predicted excess over 1.</summary>
    [GpuFact]
    public void Key_shadow_stays_dark()
    {
        var settings = new WaterCausticsCase { Caster = true };
        WaterCausticsImage on = scenes.Capture(settings);
        WaterCausticsImage off = scenes.Capture(settings with { Caustics = false });
        CausticOracle oracle = settings.Oracle();
        Func<double, double> term = oracle.TermLookup(1d);
        const double bright = 1.4d;
        int umbra = 0, umbraDifferent = 0, lit = 0;
        double litRatio = 0d;
        foreach (var (x, y, i) in WaterCausticsImage.Interior())
        {
            var (wx, wz) = on.World(x, y);
            double u = oracle.Heading.X * wx + oracle.Heading.Y * wz;
            if (term(oracle.EntryOf(u, settings.ReceiverDepth)) < bright) continue;
            if (on.Visibility[i] == 0 && off.Visibility[i] == 0)
            {
                umbra++;
                if (!Same(on, off, i)) umbraDifferent++;
            }
            else if (on.Visibility[i] == 255 && off.Visibility[i] == 255)
            {
                lit++;
                litRatio += WaterCausticsImage.Luma(on.Linear, i) / WaterCausticsImage.Luma(off.Linear, i);
            }
        }
        litRatio /= Math.Max(lit, 1);
        double required = 1d + (bright - 1d) / 2d;
        output.WriteLine($"bright-band pixels in the umbra={umbra}, differing from M2 alone={umbraDifferent}; lit bright-band pixels={lit}, mean on/off={litRatio:F4}, required={required:F4}");
        Assert.True(umbra > 200 && lit > 200, $"The caster must shadow part of a bright band and leave part lit: {umbra} umbra, {lit} lit.");
        Assert.True(litRatio >= required, $"The lit band reads {litRatio:F4} of M2 alone.");
        Assert.Equal(0, umbraDifferent);
    }

    /// <summary>The composite reads the opaque scene colour copy, so the bands it carries reach the screen scaled per
    /// pixel by the surface's own transmittance <c>W = (1 - F) exp(-sigma d / mu)</c>, the same in both renders. The copy
    /// is fitted like any receiver. The composite is fitted for place, and its excess must keep at least
    /// <c>0.95 exp(-1.02 sigma_max d)</c> of the copy's: Fresnel stays under 0.05 for a ripple tilted under 15 degrees,
    /// and its refracted view path stays within 2% of vertical.</summary>
    [GpuFact]
    public void Bands_reach_the_column_composite()
    {
        var settings = new WaterCausticsCase { Column = true };
        WaterCausticsImage on = scenes.Capture(settings);
        WaterCausticsImage off = scenes.Capture(settings with { Caustics = false });
        Assert.NotNull(on.Copy);
        CausticOracle oracle = settings.Oracle();
        BandFit copy = on.Bands(off, oracle, 1d, 1d, copy: true);
        BandFit composite = on.Bands(off, oracle, 1d, 1d);
        Vector3 sigma = WaterColumnOptics.Derive(new Color(0.10f, 0.13f, 0.10f, 1f), WaterCausticsScenes.Clarity).Extinction;
        double keep = 0.95d * Math.Exp(-1.02d * Math.Max(sigma.X, Math.Max(sigma.Y, sigma.Z)) * settings.ReceiverDepth);
        output.WriteLine($"scene colour copy: {copy}");
        output.WriteLine($"composite: {composite}, composite/copy excess={composite.PeakExcess / copy.PeakExcess:F4}, required={keep:F4}");
        AssertFit(copy, "scene colour copy");
        Assert.True(composite.Bands >= 2 && composite.MaxError <= Texel,
            $"The composite's bands are not where the copy's are: {composite}.");
        Assert.True(composite.PeakExcess >= keep * copy.PeakExcess, $"The composite keeps too little of the bands: {composite}.");
    }

    /// <summary>Review focus 3: at a grazing sun some ripple facets face away from it and refract upward, which the
    /// held <c>-t.y</c> keeps finite. The map starts at 1, loses exactly 1 and only gains, so it is never negative.</summary>
    [GpuTheory]
    [InlineData(1f)]
    [InlineData(15f)]
    [InlineData(45f)]
    [InlineData(89f)]
    public void Sun_elevations_stay_finite(float elevation)
    {
        WaterCausticsImage image = scenes.Capture(new WaterCausticsCase { SunElevationDegrees = elevation });
        CausticMapReadback map = scenes.ReadMap();
        int colourNonFinite = image.Linear.Count(v => !float.IsFinite(v));
        int mapNonFinite = map.Values.Count(v => !float.IsFinite(v));
        float minimum = map.Values.Where(float.IsFinite).Min(), maximum = map.Values.Where(float.IsFinite).Max();
        output.WriteLine($"elevation {elevation} degrees: non-finite colour={colourNonFinite}, non-finite map={mapNonFinite}, map range={minimum:F4}..{maximum:F4}");
        Assert.Equal(1, image.Diagnostics.DrawnPlanes);
        Assert.Equal(0, colourNonFinite);
        Assert.Equal(0, mapNonFinite);
        Assert.True(minimum >= 0f, $"The map went negative ({minimum}).");
    }

    /// <summary>
    /// Bands jump half a wavelength, then the frames settle. Stepped and steady sequences end on the same jitter phases
    /// with the same current frames, so what separates them is history left from before the step, which every frame
    /// clips into the current neighbourhood and keeps by at most <c>A / (A + w)</c>, with <c>A</c> the accumulation cap and
    /// <c>w</c> the nearest sample's Lanczos-2 weight at that frame's jitter. Measured in the resolve's weighted luma
    /// <c>L / (1 + L)</c>, where those operations contract: the trail may exceed the steady sequence's own difference
    /// from the temporal-off image by that decay times the starting gap, plus half-float rounding. MSAA has no history,
    /// so the same bound holds trivially there.
    /// </summary>
    [GpuTheory]
    [InlineData("temporal")]
    [InlineData("msaa")]
    public void Temporal_resolve_leaves_no_trail(string resolve)
    {
        const int warm = 16, settle = 64;
        AntiAliasing aa = resolve == "msaa" ? AntiAliasing.Msaa(4) : AntiAliasing.Temporal;
        // Wave speed 1 and k = pi: pi seconds move the ripple phase by pi, half a wavelength.
        var before = new WaterCausticsCase { Aa = aa };
        var after = before with { Time = MathF.PI };
        WaterCausticsImage offBefore = scenes.Capture(before with { Aa = AntiAliasing.Off });
        WaterCausticsImage offAfter = scenes.Capture(after with { Aa = AntiAliasing.Off });
        // Both sequences last warm + settle frames, a multiple of the eight native phases, so they end on the same phase.
        WaterCausticsImage steady = scenes.Capture(after, warm + settle);
        scenes.Capture(before, warm);
        WaterCausticsImage stepped = scenes.Capture(after, settle, cut: false);

        double start = MaxWeightedDifference(offBefore, offAfter), floor = MaxWeightedDifference(steady, offAfter);
        double trail = MaxWeightedDifference(stepped, offAfter);
        double decay = 1d;
        if (aa.Mode == AntiAliasingMode.Temporal)
            for (int frame = 0; frame < settle; frame++)
            {
                Vector2 jitter = TemporalJitter.Offset(frame, TemporalJitter.NativePhaseCount);
                double weight = Lanczos2(jitter.X) * Lanczos2(jitter.Y);
                decay *= TemporalResolveTuning.MaxAccumulation / (TemporalResolveTuning.MaxAccumulation + weight);
            }
        else decay = 0d;
        double bound = floor + decay * (start + 2d * floor) + 1d / 512d;
        output.WriteLine($"{resolve}: step gap={start:F5}, steady difference from off={floor:F5}, stepped difference={trail:F5}, history decay over {settle} frames={decay:F5}, bound={bound:F5}");
        Assert.True(trail <= bound, $"The {resolve} resolve trails the old bands: {trail:F5} against a bound of {bound:F5}.");
    }

    [GpuFact]
    public void SharedSceneMatchesFreshAfterChangingConfigurations()
    {
        scenes.Capture(new WaterCausticsCase { Layout = WaterCausticsLayout.Stacked });
        scenes.Capture(new WaterCausticsCase { Column = true, Caster = true });
        scenes.Capture(new WaterCausticsCase { Aa = AntiAliasing.Temporal }, 8);
        scenes.Capture(new WaterCausticsCase { Aa = AntiAliasing.Msaa(4), Origin = new Vector3(1000.3f, 0f, -2000.7f) });
        // Atlases never shrink, so both scenes reach four layers before the compared frame.
        scenes.Capture(new WaterCausticsCase { Layout = WaterCausticsLayout.SharedRiver });
        var settings = new WaterCausticsCase();
        WaterCausticsImage reused = scenes.Capture(settings);
        using var fresh = new WaterCausticsScenes();
        fresh.Capture(new WaterCausticsCase { Layout = WaterCausticsLayout.SharedRiver });
        WaterCausticsImage control = fresh.Capture(settings);
        AssertIdentical(control, reused, "shared versus fresh");
    }

    WaterCausticsImage AssertBandsAtOracle(WaterCausticsCase settings)
    {
        WaterCausticsImage on = scenes.Capture(settings);
        WaterCausticsImage off = scenes.Capture(settings with { Caustics = false });
        BandFit fit = on.Bands(off, settings.Oracle(), settings.Strength * settings.ReceiverDepth, settings.ReceiverDepth);
        output.WriteLine($"elevation {settings.SunElevationDegrees}, azimuth {settings.SunAzimuthDegrees}, strength {settings.Strength}, depth {settings.ReceiverDepth}: {fit}");
        AssertFit(fit, "single ripple");
        return on;
    }

    /// <summary>Place within one map texel, and at least half the oracle's peak excess, which the lattice and the
    /// bilinear read can only lower by smoothing the peak. A missing term leaves a flat profile, whose search lands a
    /// quarter wavelength off.</summary>
    static void AssertFit(BandFit fit, string label)
    {
        Assert.True(fit.Bands >= 2, $"{label}: only {fit.Bands} bands fitted.");
        Assert.True(fit.PeakExcess >= 0.5d * fit.PredictedExcess, $"{label}: bands too faint, {fit}.");
        Assert.True(fit.MaxError <= Texel, $"{label}: bands off their analytic place, {fit}.");
    }

    void AssertIdentical(WaterCausticsImage expected, WaterCausticsImage actual, string label)
    {
        int bytes = expected.Rgba.Zip(actual.Rgba).Count(p => p.First != p.Second);
        int linear = expected.Linear.Zip(actual.Linear).Count(p => BitConverter.SingleToInt32Bits(p.First) != BitConverter.SingleToInt32Bits(p.Second));
        int visibility = expected.Visibility.Zip(actual.Visibility).Count(p => p.First != p.Second);
        output.WriteLine($"{label}: differing presented bytes={bytes}, linear values={linear}, visibility bytes={visibility}");
        Assert.Equal(0, bytes);
        Assert.Equal(0, linear);
        Assert.Equal(0, visibility);
    }

    static bool Same(WaterCausticsImage a, WaterCausticsImage b, int pixel)
    {
        for (int c = 0; c < 4; c++)
            if (a.Rgba[pixel * 4 + c] != b.Rgba[pixel * 4 + c]
                || BitConverter.SingleToInt32Bits(a.Linear[pixel * 4 + c]) != BitConverter.SingleToInt32Bits(b.Linear[pixel * 4 + c]))
                return false;
        return true;
    }

    static double MaxWeightedDifference(WaterCausticsImage a, WaterCausticsImage b)
    {
        double worst = 0d;
        foreach (var (_, _, i) in WaterCausticsImage.Interior())
        {
            double la = WaterCausticsImage.Luma(a.Linear, i), lb = WaterCausticsImage.Luma(b.Linear, i);
            worst = Math.Max(worst, Math.Abs(la / (1d + la) - lb / (1d + lb)));
        }
        return worst;
    }

    static double Lanczos2(double x)
    {
        if (x == 0d) return 1d;
        double px = Math.PI * x;
        return Math.Abs(x) >= 2d ? 0d : 2d * Math.Sin(px) * Math.Sin(px / 2d) / (px * px);
    }
}
