using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// THE SOFT FILTER, measured as a WIDTH rather than as a look. Every case here walks a row of single-texel probes
/// across a shadow edge and counts how far the picture takes to get from lit to shadowed, so "softer" is a number
/// in metres and "contact hardening" is two of those numbers compared.
/// <para>
/// EVERY PROBE IS A VISIBILITY RATIO, not a brightness. The same scene is captured twice, once with the light
/// carrying a map and once with <see cref="LightShadow.None"/>, and each probe is the shadowed level over the
/// unshadowed one. That divides out the distance falloff and the grazing N.L, which over a scan several metres
/// long move the lit level by a third all on their own and would swamp the thing being measured. What is left is
/// 1 where the light reaches and 0 where it does not, on every backend and at any exposure.
/// </para>
/// <para>
/// THE FRAMING IS PER CASE. The standing framing puts about forty-six metres across 192 pixels, where a fifteen
/// centimetre penumbra is two thirds of one texel and no count means anything. Each case frames the few metres
/// around its own edge first, which it may do because a class fixture is built once PER TEST CLASS.
/// </para>
/// </summary>
public sealed class PointShadowFilterGpuTests(PointShadowScene fixture, ITestOutputHelper output)
    : IClassFixture<PointShadowScene>
{
    /// <summary>Generous on purpose: the far case's receiver stands ten metres out, and a light that dies before
    /// it would leave nothing to divide by.</summary>
    const float Radius = 30f;
    const float Intensity = 1f;

    // TWO LIGHTS OVER ONE WALL, and the only difference between them is how far behind the wall the edge lands.
    // The fixture's wall is x in [2, 2.4], y in [0, 3], z in [-3, 3], so the silhouette is its top-far corner
    // (2.4, 3) and the boundary on the floor is where the ray over that corner reaches y = 0.
    //
    // NEAR: standing at y = 6 the ray dives steeply and the edge lands 1.4 m behind the wall. The blocker is
    // 3.31 m from the light and the receiver 6.62 m, so the penumbra term (dR - dB) / dB is 1.0.
    static readonly Vector3 NearLight = new(1f, 6f, 0f);
    const float NearEdgeX = 3.8f;

    // FAR: half a metre over the wall top the same ray grazes out to 8.4 m behind it. The blocker is now 1.49 m
    // from the light and the receiver 10.41 m, so the same term is 6.0. Same wall, same light size, six times the
    // penumbra: that ratio IS contact hardening.
    static readonly Vector3 FarLight = new(1f, 3.5f, 0f);
    const float FarEdgeX = 10.8f;

    static PointShadowSettings Budget(PointShadowFilter filter, float maxPenumbraTexels = 6f,
        float lightSizeMetres = 0.15f) => new()
        {
            Filter = filter,
            LightSizeMetres = lightSizeMetres,
            MaxPenumbraTexels = maxPenumbraTexels,
        };

    PointShadowScene.Shot Wall(Vector3 light, LightShadow shadow, PointShadowSettings budget) =>
        fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawWall();
            s.AddLight(light, Color.White, Radius, Intensity, shadow);
        }, budget);

    /// <summary>One TEXEL of the red channel at a world point, with the point pinned on the picture rather than
    /// clamped onto its edge: a probe that walked off the capture would read the border and be counted as a
    /// plateau.</summary>
    int Texel(PointShadowScene.Shot shot, Vector3 world)
    {
        Vector2 px = fixture.Pixel(world);
        int x = (int)px.X, y = (int)px.Y;
        Assert.True(x >= 0 && x < PointShadowScene.Width && y >= 0 && y < PointShadowScene.Height,
            $"the probe at {world} lands at {px}, off a {PointShadowScene.Width} by {PointShadowScene.Height} "
            + "capture. Frame the case around its own edge first.");
        return shot.Rgba[(y * PointShadowScene.Width + x) * 4];
    }

    /// <summary>The visibility profile across an edge: one entry per probe, each the shadowed level over the
    /// unshadowed one, walking <paramref name="steps"/> probes from <paramref name="from"/> to
    /// <paramref name="to"/>.</summary>
    float[] Profile(PointShadowScene.Shot shadowed, PointShadowScene.Shot plain, Vector3 from, Vector3 to,
        int steps)
    {
        var v = new float[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            Vector3 at = Vector3.Lerp(from, to, i / (float)steps);
            int lit = Texel(plain, at);
            Assert.True(lit > 24,
                $"the unshadowed capture reads {lit} at {at}, too dark to divide by. Move the probe row inside "
                + "the light's reach or raise the intensity.");
            v[i] = Texel(shadowed, at) / (float)lit;
        }
        return v;
    }

    /// <summary>How wide the transition is, in METRES: the probes whose visibility lies strictly between the two
    /// plateaus, times the step. The plateaus are 0 and 1 by construction (see the class remarks), so the band is
    /// everything in 0.15 .. 0.85 and the two ends of the scan are asserted to be outside it, which is what stops
    /// a scan that missed the edge from reporting a narrow one.</summary>
    static float BandMetres(float[] profile, float lengthMetres)
    {
        Assert.True(profile[0] < 0.15f,
            $"the scan must START deep in shadow, and it starts at {profile[0]:0.000}");
        Assert.True(profile[^1] > 0.85f,
            $"the scan must END deep in the lit region, and it ends at {profile[^1]:0.000}");
        int inside = 0;
        foreach (float v in profile)
            if (v is > 0.15f and < 0.85f) inside++;
        return inside * lengthMetres / (profile.Length - 1);
    }

    /// <summary>
    /// THE HEADLINE: the shipped Soft filter spreads the edge several times as far as the four-tap Hard one, at
    /// the same place in the same scene. Measured eight metres behind the wall, where a lantern's light through a
    /// doorway is asked to look like light rather than like a stencil.
    /// </summary>
    [GpuFact]
    public void ASoftEdgeIsSeveralTimesWiderThanTheHardOneWellBehindTheWall()
    {
        fixture.FrameOn(new Vector3(FarEdgeX, 0f, 0f), 1.6f);
        const float half = 1.2f;
        Vector3 from = new(FarEdgeX - half, 0f, 0f), to = new(FarEdgeX + half, 0f, 0f);

        PointShadowScene.Shot plain = Wall(FarLight, LightShadow.None, Budget(PointShadowFilter.Soft));
        PointShadowScene.Shot hard = Wall(FarLight, LightShadow.Static(301), Budget(PointShadowFilter.Hard));
        PointShadowScene.Shot soft = Wall(FarLight, LightShadow.Static(302), Budget(PointShadowFilter.Soft));

        float hardBand = BandMetres(Profile(hard, plain, from, to, 120), 2f * half);
        float softBand = BandMetres(Profile(soft, plain, from, to, 120), 2f * half);
        output.WriteLine($"far edge: hard {hardBand:0.000} m, soft {softBand:0.000} m");

        Assert.True(softBand >= 2f * hardBand,
            $"the soft edge is {softBand:0.000} m against the hard one's {hardBand:0.000} m, which is not the "
            + "clear factor this exists to hold");
        Assert.True(softBand >= 0.08f,
            $"the soft edge is {softBand:0.000} m, which is a hard edge with a rounding error on it");
    }

    /// <summary>
    /// CONTACT HARDENING, which is the realism half of the request: the same wall, the same light size and the
    /// same filter, with the edge measured where it lands 1.4 m behind the wall and again where it lands 8.4 m
    /// behind it. The penumbra follows the distance from the OCCLUDER, so the near edge stays crisp while the far
    /// one spreads.
    /// <para>
    /// The texel cap is lifted to sixteen for this case ON PURPOSE. At the shipped six the far edge saturates it,
    /// and a comparison of two numbers both sitting on the same ceiling would say nothing about the physics. The
    /// case above measures the shipped default instead.
    /// </para>
    /// </summary>
    [GpuFact]
    public void TheSoftEdgeIsTighterCloseBehindTheWallThanWellBeyondIt()
    {
        PointShadowSettings budget = Budget(PointShadowFilter.Soft, maxPenumbraTexels: 16f);

        fixture.FrameOn(new Vector3(NearEdgeX, 0f, 0f), 0.8f);
        PointShadowScene.Shot nearPlain = Wall(NearLight, LightShadow.None, budget);
        PointShadowScene.Shot nearSoft = Wall(NearLight, LightShadow.Static(303), budget);
        float nearBand = BandMetres(
            Profile(nearSoft, nearPlain, new Vector3(NearEdgeX - 0.6f, 0f, 0f),
                new Vector3(NearEdgeX + 0.6f, 0f, 0f), 120), 1.2f);

        // The far scan is SIX metres long on purpose. The kernel is a disc about the RAY, and this ray meets the
        // floor at under twenty degrees, so a 0.9 m disc lies nearly three metres along the floor either side of
        // the edge. A shorter scan starts inside the penumbra, where whether one probe reads under the plateau
        // threshold is down to that texel's own dither rotation rather than to the filter.
        fixture.FrameOn(new Vector3(FarEdgeX, 0f, 0f), 3.6f);
        PointShadowScene.Shot farPlain = Wall(FarLight, LightShadow.None, budget);
        PointShadowScene.Shot farSoft = Wall(FarLight, LightShadow.Static(304), budget);
        float farBand = BandMetres(
            Profile(farSoft, farPlain, new Vector3(FarEdgeX - 3f, 0f, 0f),
                new Vector3(FarEdgeX + 3f, 0f, 0f), 120), 6f);

        output.WriteLine($"contact hardening: near {nearBand:0.000} m, far {farBand:0.000} m");
        Assert.True(farBand >= 2.5f * nearBand,
            $"the edge 1.4 m behind the wall measures {nearBand:0.000} m and the one 8.4 m behind it "
            + $"{farBand:0.000} m. The penumbra is not following the distance from the occluder, which is the "
            + "whole difference between a soft shadow and a blurred one");
    }

    /// <summary>
    /// SOFTENING AN EDGE IS NOT DIMMING A ROOM. Deep inside the lit region and deep inside the shadow the two
    /// filters must agree, so the only thing the new one moves is the handful of texels the edge runs through.
    /// </summary>
    [GpuFact]
    public void TheLitAndShadowedPlateausReadTheSameUnderEitherFilter()
    {
        fixture.FrameOn(new Vector3(FarEdgeX, 0f, 0f), 3.5f);
        PointShadowScene.Shot hard = Wall(FarLight, LightShadow.Static(305), Budget(PointShadowFilter.Hard));
        PointShadowScene.Shot soft = Wall(FarLight, LightShadow.Static(306), Budget(PointShadowFilter.Soft));

        foreach (Vector3 probe in new[] { new Vector3(FarEdgeX - 2.8f, 0f, 0f), new Vector3(FarEdgeX + 2.8f, 0f, 0f) })
        {
            int h = Texel(hard, probe), s = Texel(soft, probe);
            Assert.True(Math.Abs(h - s) <= 2,
                $"the plateau at {probe} reads {h} hard against {s} soft, a gap of {Math.Abs(h - s)} levels. A "
                + "filter that moves a plateau is changing the exposure of the room, not the edge of a shadow");
        }
    }

    /// <summary>A series with its least-squares straight line taken out, so a local bump is readable against a
    /// slow trend.</summary>
    static float[] Detrend(float[] series)
    {
        int n = series.Length;
        float meanX = (n - 1) / 2f, meanY = 0f;
        foreach (float y in series) meanY += y;
        meanY /= n;
        float sxx = 0f, sxy = 0f;
        for (int i = 0; i < n; i++)
        {
            sxx += (i - meanX) * (i - meanX);
            sxy += (i - meanX) * (series[i] - meanY);
        }
        float slope = sxy / sxx;
        var residual = new float[n];
        for (int i = 0; i < n; i++) residual[i] = series[i] - (meanY + slope * (i - meanX));
        return residual;
    }

    /// <summary>
    /// NO SEAM WHERE THE EDGE CROSSES A CUBE FACE. A wider kernel built the old way, with every tap clamped inside
    /// its own face cell, stops reading at the 45 degree plane and the average jumps the moment the edge crosses
    /// it. Here the light stands at y = 5 over the fixture's wall and the shadow of the wall's z edge runs out
    /// along z = 1.5x, crossing from the -Y face onto the +Z face at x = 3.333, and the case walks ALONG that edge
    /// through the crossing.
    /// <para>
    /// WHAT IS MEASURED AT EACH STATION IS AN INTEGRAL, not a level. The filter's disc is rotated per fragment, so
    /// one texel of a nine-tap average carries up to a ninth of dither noise and a single-texel reading cannot
    /// resolve anything smaller than that. Each station instead scans ACROSS the edge and sums the shadowed part of
    /// the profile, which is how far the shadow reaches into a fixed window, in metres. That number is smooth
    /// along a clean edge, it moves if either the position or the width of the penumbra moves, and averaging forty
    /// probes takes the dither out of it.
    /// </para>
    /// </summary>
    [GpuFact]
    public void TheSoftEdgeCrossesACubeFaceBoundaryWithoutAStep()
    {
        var light = new Vector3(0f, 5f, 0f);
        PointShadowSettings budget = Budget(PointShadowFilter.Soft, maxPenumbraTexels: 16f, lightSizeMetres: 0.5f);
        fixture.FrameOn(new Vector3(3.6f, 0f, 5.4f), 1.8f);

        PointShadowScene.Shot plain = Wall(light, LightShadow.None, budget);
        PointShadowScene.Shot soft = Wall(light, LightShadow.Static(307), budget);

        // Across the edge: the boundary line runs along (1, 1.5) normalised, so this points out of the lit side
        // and into the shadowed one.
        var across = Vector3.Normalize(new Vector3(1.5f, 0f, -1f));
        const float window = 0.9f;
        const int stations = 13, probes = 60;

        var reach = new float[stations];
        for (int s = 0; s < stations; s++)
        {
            float x = 2.9f + s * 0.1f;                       // the crossing at x = 3.333 falls in station 4 to 5
            var on = new Vector3(x, 0f, 1.5f * x);
            float[] profile = Profile(soft, plain, on - across * window, on + across * window, probes);
            Assert.True(profile[0] > 0.9f && profile[^1] < 0.1f,
                $"the cross-edge window at {on} runs {profile[0]:0.00} to {profile[^1]:0.00}, so it does not "
                + "reach clear of the penumbra at both ends and the integral below is clipped");
            float sum = 0f;
            foreach (float v in profile) sum += 1f - v;
            reach[s] = sum * 2f * window / probes;
        }

        // The reach drifts slowly along the edge, because the receiver is getting further from the light and the
        // penumbra is widening with it, so the drift is taken out with a least-squares line and what is left is
        // the RESIDUAL. A clamped kernel does not show up as one step: it is a bump three stations wide, centred
        // on the crossing, which is why the residuals are averaged over that neighbourhood rather than differenced
        // pairwise. Station 4 is x = 3.30 and station 5 is x = 3.40, so stations 3 to 5 straddle the crossing at
        // 3.333 with the kernel's own reach either side of it.
        float[] residual = Detrend(reach);
        float bump = (residual[3] + residual[4] + residual[5]) / 3f;
        float worstElsewhere = 0f;
        for (int s = 0; s < stations; s++)
            if (s is < 3 or > 5) worstElsewhere = Math.Max(worstElsewhere, Math.Abs(residual[s]));
        output.WriteLine($"face seam: crossing bump {bump:0.0000} m, largest residual elsewhere "
            + $"{worstElsewhere:0.0000} m; reach "
            + string.Join(", ", Array.ConvertAll(reach, r => r.ToString("0.000"))));

        Assert.True(Math.Abs(bump) <= 0.008f,
            $"the shadow's reach into the window sits {bump:0.0000} m off its own trend across the three stations "
            + $"either side of the cube face boundary at x = 3.333, against {worstElsewhere:0.0000} m anywhere "
            + "else along the same edge. That is a tap clamped inside its own face cell instead of crossing to "
            + "the neighbouring face");
    }

    /// <summary>
    /// THE ACNE PROOF UNDER BOTH FILTERS. The soft path takes many more taps, each at its own direction, so it
    /// reads the atlas where the four-tap one never did and gets a fresh chance to compare a flat floor against
    /// its own stored distance. The tolerance is the three levels the hard path is held to, for the reason given
    /// on <c>PointShadowGpuTests.AnUnoccludedFloorUnderAShadowedLightIsNotAcned</c>: a failure is tuned out
    /// through the bias defaults, never by widening this.
    /// </summary>
    [GpuTheory]
    [InlineData(PointShadowFilter.Hard)]
    [InlineData(PointShadowFilter.Soft)]
    public void AnUnoccludedFloorIsNotAcnedUnderEitherFilter(PointShadowFilter filter)
    {
        var light = new Vector3(0f, 2f, 0f);
        fixture.FrameOn(new Vector3(5f, 0f, 0f), 5.5f);
        PointShadowScene.Shot shadowed = fixture.One(s =>
        {
            s.DrawFloor();
            s.AddLight(light, Color.White, 12f, PointShadowScene.Intensity, LightShadow.Static(308));
        }, Budget(filter));
        PointShadowScene.Shot plain = fixture.One(s =>
        {
            s.DrawFloor();
            s.AddLight(light, Color.White, 12f, PointShadowScene.Intensity, LightShadow.None);
        }, Budget(filter));

        Assert.Equal(1, shadowed.ShadowedLights);
        var worst = (Probe: Vector3.Zero, Delta: 0);
        for (float x = 0.4f; x <= 9.6f; x += 0.4f)
        {
            var probe = new Vector3(x, 0f, 0f);
            int delta = Math.Abs(shadowed.Red(probe) - plain.Red(probe));
            if (delta > worst.Delta) worst = (probe, delta);
        }

        Assert.True(worst.Delta <= 3,
            $"under {filter} the floor at {worst.Probe} reads {shadowed.Red(worst.Probe)} with a shadow map "
            + $"against {plain.Red(worst.Probe)} without one, a gap of {worst.Delta} levels. That is the floor "
            + "shadowing itself. Raise the PointShadowSettings bias defaults, never this tolerance.");
    }

    /// <summary>
    /// THE CONTROL UNDER ALL OF IT: the two filters really are two pictures, and each is stable. Without this a
    /// suite of "soft is wider than hard" could be reading one picture twice.
    /// </summary>
    [GpuFact]
    public void TheTwoFiltersAreTwoPicturesAndEachOneRepeats()
    {
        fixture.FrameOn(new Vector3(FarEdgeX, 0f, 0f), 2.2f);
        byte[] hard = Wall(FarLight, LightShadow.Static(309), Budget(PointShadowFilter.Hard)).Rgba;
        byte[] hardAgain = Wall(FarLight, LightShadow.Static(310), Budget(PointShadowFilter.Hard)).Rgba;
        byte[] soft = Wall(FarLight, LightShadow.Static(311), Budget(PointShadowFilter.Soft)).Rgba;

        Assert.Equal(hard, hardAgain);
        Assert.NotEqual(hard, soft);
    }
}
