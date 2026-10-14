using System;
using System.Numerics;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The measurement behind <see cref="PointShadowFilterGpuTests.TheSoftEdgeCrossesACubeFaceBoundaryWithoutAStep"/>,
/// kept apart from the device so a headless test can replay a retained capture through exactly the same numbers.
/// The scene is the fixture's wall under a light at (0, 5, 0): the shadow of the wall's z edge runs out along
/// z = 1.5x and crosses from the -Y face onto the +Z face at x = 3.333. Thirteen stations sit on that line at
/// x = 2.9 to 4.1, and stations 3 to 5 straddle the crossing.
/// <para>
/// A STATION'S REACH IS AN AREA INTEGRAL. Every pixel whose floor point lies in the station's band, the window
/// across the edge by half a station spacing either way along it, adds its shadowed part, so a reach is the mean
/// of about nine hundred pixels. The single row of sixty point reads this replaced carried the nine-tap disc's
/// per-fragment dither and each probe's truncation phase undiluted. Its residuals away from the crossing reached
/// 15.6 mm on Metal and 8.5 mm on WARP, past the bound below, and a quarter-pixel shift of the same WARP capture
/// moved its crossing value anywhere from -5.6 to -10.5 mm. The integral puts the crossing bump of the shipped
/// filter at the same -3.0 mm on both backends, to within 0.1 mm.
/// </para>
/// <para>
/// THE TREND IS FITTED AWAY FROM THE CROSSING. The reach drifts along the edge as the penumbra widens, so a
/// straight line comes out first. A line fitted through the crossing stations as well is pulled towards a seam
/// and pushes every other residual the other way, which hides part of the seam and dresses the rest of the edge
/// up as noise. The line is fitted to the ten other stations only, and the bump is how far the crossing stations
/// sit off it on average.
/// </para>
/// </summary>
internal static class FaceSeamReach
{
    /// <summary>The largest crossing bump that still counts as no step. A face-clamped kernel in this scene
    /// measures +24.6 mm, the shipped filter about -3 mm on Metal and on WARP.</summary>
    public const float Bound = 0.008f;

    /// <summary>Half the window across the edge, in metres. Both ends must sit clear of the penumbra.</summary>
    public const float Window = 0.9f;

    public const int Stations = 13, FirstCrossing = 3, CrossingCount = 3;

    /// <summary>The width of each end strip whose mean visibility proves the window clears the penumbra.</summary>
    const float EndStrip = 0.05f;

    static readonly Vector2 FirstStation = new(2.9f, 4.35f);
    static readonly Vector2 Step = new(0.1f, 0.15f);

    /// <summary>Floor XZ, out of the lit side and into the shadowed one.</summary>
    static readonly Vector2 Across = Vector2.Normalize(new Vector2(1.5f, -1f));

    public sealed record Measurement(float[] Reach, float[] Residual, float Bump, float WorstElsewhere)
    {
        public string Describe() =>
            $"crossing bump {Bump * 1000f:0.00} mm, largest residual elsewhere {WorstElsewhere * 1000f:0.00} mm, "
            + "residuals " + string.Join(" ", Array.ConvertAll(Residual, r => (r * 1000f).ToString("0.0")))
            + " mm, reach " + string.Join(" ", Array.ConvertAll(Reach, r => r.ToString("0.0000"))) + " m";
    }

    /// <summary>
    /// Measure a capture. <paramref name="pixelOf"/> maps a world point to its position in the picture and must be
    /// orthographic, which the fixture's iso camera is, so the floor under every pixel centre follows from three
    /// points. <paramref name="visibility"/> is the shadowed level over the unshadowed one at a pixel, and is only
    /// asked about pixels inside a band.
    /// </summary>
    public static Measurement Measure(int width, int height, Func<Vector3, Vector2> pixelOf,
        Func<int, int, float> visibility)
    {
        Vector2 origin = pixelOf(Vector3.Zero);
        Vector2 ex = pixelOf(Vector3.UnitX) - origin, ez = pixelOf(Vector3.UnitZ) - origin;
        float det = ex.X * ez.Y - ez.X * ex.Y;          // pixels per square metre of floor, up to sign
        float spacing = Step.Length();
        Vector2 along = Step / spacing;

        var shadowed = new double[Stations];
        var pixels = new int[Stations];
        var litEnd = new double[Stations];
        var litEndCount = new int[Stations];
        var darkEnd = new double[Stations];
        var darkEndCount = new int[Stations];
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                Vector2 d = new Vector2(i + 0.5f, j + 0.5f) - origin;
                var floor = new Vector2((d.X * ez.Y - ez.X * d.Y) / det, (ex.X * d.Y - d.X * ex.Y) / det);
                float t = Vector2.Dot(floor - FirstStation, along) / spacing;
                int s = (int)MathF.Floor(t + 0.5f);
                if (s < 0 || s >= Stations) continue;
                float u = Vector2.Dot(floor - (FirstStation + s * Step), Across);
                if (MathF.Abs(u) > Window) continue;

                float v = visibility(i, j);
                shadowed[s] += 1f - v;
                pixels[s]++;
                if (u <= -Window + EndStrip) { litEnd[s] += v; litEndCount[s]++; }
                if (u >= Window - EndStrip) { darkEnd[s] += v; darkEndCount[s]++; }
            }
        }

        float expected = 2f * Window * spacing * MathF.Abs(det);
        var reach = new float[Stations];
        for (int s = 0; s < Stations; s++)
        {
            Assert.True(pixels[s] >= 0.98f * expected,
                $"station {s} covers {pixels[s]} pixels of the {expected:0} its band holds, so the band runs off the "
                + "capture. Frame the case around the whole edge.");
            float lit = (float)(litEnd[s] / litEndCount[s]), dark = (float)(darkEnd[s] / darkEndCount[s]);
            Assert.True(lit > 0.9f && dark < 0.1f,
                $"the window at station {s} runs {lit:0.00} to {dark:0.00}, so it does not reach clear of the "
                + "penumbra at both ends and the integral is clipped");
            reach[s] = (float)(shadowed[s] / pixels[s]) * 2f * Window;
        }
        return Crossing(reach);
    }

    /// <summary>The crossing statistic over a reach series: a least-squares line through every station outside
    /// the crossing, then the crossing stations' mean residual against it.</summary>
    public static Measurement Crossing(float[] reach)
    {
        double n = 0, sx = 0, sy = 0;
        for (int s = 0; s < reach.Length; s++)
        {
            if (IsCrossing(s)) continue;
            n++; sx += s; sy += reach[s];
        }
        double meanX = sx / n, meanY = sy / n, sxx = 0, sxy = 0;
        for (int s = 0; s < reach.Length; s++)
        {
            if (IsCrossing(s)) continue;
            sxx += (s - meanX) * (s - meanX);
            sxy += (s - meanX) * (reach[s] - meanY);
        }
        double slope = sxy / sxx;

        var residual = new float[reach.Length];
        double bump = 0;
        float worst = 0f;
        for (int s = 0; s < reach.Length; s++)
        {
            residual[s] = (float)(reach[s] - (meanY + slope * (s - meanX)));
            if (IsCrossing(s)) bump += residual[s];
            else worst = MathF.Max(worst, MathF.Abs(residual[s]));
        }
        return new Measurement(reach, residual, (float)(bump / CrossingCount), worst);
    }

    static bool IsCrossing(int station) => station >= FirstCrossing && station < FirstCrossing + CrossingCount;
}
