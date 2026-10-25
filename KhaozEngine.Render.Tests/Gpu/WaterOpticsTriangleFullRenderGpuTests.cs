using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("GpuTimingSensitive")]
public sealed class WaterOpticsTriangleFullRenderGpuTests(WaterOpticsTriangleFullRenderScene fixture,
    ITestOutputHelper output) : IClassFixture<WaterOpticsTriangleFullRenderScene>
{
    // Fixed before the acceptance run. No per-backend or measured-image tolerance selection.
    const float EquivalentRgbTolerance = .04f;
    const float DryRgbTolerance = .02f;
    const float ExpectedFeatherCoverage = .352f;
    const float FeatherCoverageTolerance = .10f;
    const float ParityRgbTolerance = .01f;

    [GpuFact]
    public void FullSceneMode2MatchesAffineControlAndLiteralWetDryCoverage()
    {
        IReadOnlyDictionary<WaterOpticsFullRenderMode, WaterOpticsFullCapture> captures = fixture.Captures;
        WaterOpticsImage opaque = captures[WaterOpticsFullRenderMode.Opaque].Image;
        WaterOpticsImage control = captures[WaterOpticsFullRenderMode.Bilinear].Image;
        WaterOpticsImage legacy = captures[WaterOpticsFullRenderMode.Legacy].Image;
        var comparisons = new List<object>();
        foreach (WaterOpticsFullRenderMode mode in new[] { WaterOpticsFullRenderMode.Triangles2, WaterOpticsFullRenderMode.Triangles32 })
        {
            WaterOpticsFullCapture capture = captures[mode];
            WaterOpticsImage image = capture.Image;
            float maximum = MaximumRgbDifference(image, control);
            var probes = new Dictionary<string, object>();
            var coverages = new Dictionary<string, float>();
            foreach (string name in new[] { "wet", "object", "feather", "dry", "coverage", "outside" })
            {
                Vector3 actual = image.Sample(name), background = opaque.Sample(name);
                Vector3 direction = WaterOpticsTriangleFullRenderScene.WaterRgb - background;
                Assert.True(direction.Length() > .35f, $"{name} lacks water/opaque colour separation.");
                float coverage = Vector3.Dot(actual - background, direction) / direction.LengthSquared();
                probes.Add(name, new { ActualRgb = new[] { actual.X, actual.Y, actual.Z }, Coverage = coverage });
                coverages.Add(name, coverage);
            }
            comparisons.Add(new { Mode = mode.ToString(), MaximumRgbDistance = maximum, Probes = probes });
            Write("matched-controls", new
            {
                EquivalentRgbTolerance,
                DryRgbTolerance,
                ExpectedFeatherCoverage,
                FeatherCoverageTolerance,
                Comparisons = comparisons
            });
            PrintTiming(capture);
            foreach ((string name, float coverage) in coverages)
            {
                if (name is "wet" or "object" or "coverage")
                    Assert.InRange(coverage, .96f, 1.04f);
                if (name == "feather")
                    Assert.InRange(coverage, ExpectedFeatherCoverage - FeatherCoverageTolerance,
                        ExpectedFeatherCoverage + FeatherCoverageTolerance);
                if (name is "dry" or "outside")
                    Assert.True(Vector3.Distance(image.Sample(name), opaque.Sample(name)) <= DryRgbTolerance,
                        $"{mode} changed the {name} opaque control.");
            }
            Assert.True(maximum <= EquivalentRgbTolerance,
                $"{mode} full-scene RGB distance {maximum:R} exceeds the unchanged affine allowance {EquivalentRgbTolerance}.");
        }
        float fieldNonvacuity = Vector3.Distance(control.Sample("object"), legacy.Sample("object"));
        Write("nonvacuity", new { ObjectBilinearVsLegacyRgb = fieldNonvacuity, Minimum = .12f });
        Assert.True(fieldNonvacuity > .12f, "The legacy object depth must expose the false shoreline control.");
        Assert.Equal(2, captures[WaterOpticsFullRenderMode.Triangles2].MaximumBinOccupancy);
        Assert.Equal(32, captures[WaterOpticsFullRenderMode.Triangles32].MaximumBinOccupancy);
        PrintTiming(captures[WaterOpticsFullRenderMode.Bilinear]);
        output.WriteLine("Per-water GPU timing is unavailable. Completed-frame timestamps cover all production rendering and End finalization.");
    }

    [GpuFact]
    public void AgedMode2FullSceneMatchesFreshScene()
    {
        IReadOnlyDictionary<WaterOpticsFullRenderMode, WaterOpticsFullCapture> captures = fixture.Captures;
        WaterOpticsFullCapture aged = captures[WaterOpticsFullRenderMode.Triangles32];
        using var freshFixture = new WaterOpticsTriangleFullRenderScene();
        WaterOpticsFullCapture fresh = freshFixture.Capture(WaterOpticsFullRenderMode.Triangles32, "FreshTriangles32");
        fixture.WriteContactSheet(captures.Values, fresh);
        float maximum = MaximumRgbDifference(aged.Image, fresh.Image);
        Write("aged-fresh", new
        {
            MaximumRgbDistance = maximum,
            Tolerance = ParityRgbTolerance,
            FreshColdFrame = fresh.ColdFrame,
            FreshWarmFrame = fresh.WarmFrame
        });
        Assert.True(maximum <= ParityRgbTolerance,
            $"Aged/fresh full-scene RGB distance {maximum:R} exceeds {ParityRgbTolerance}.");
        PrintTiming(fresh, freshFixture);
    }

    [GpuFact]
    public void AlternatingKindsOnFocusedAndClipmapMatchIndependentControlInBothQueueOrders()
    {
        // Initialize the shared fixture's canonical captures before aging it through mixed capabilities.
        _ = fixture.Captures;
        var comparisons = new List<object>();
        foreach (WaterGridMode grid in new[] { WaterGridMode.CameraFocused, WaterGridMode.Clipmap })
        {
            WaterOpticsImage mixed = fixture.CaptureMixed(grid, reverse: false, bilinearControl: false);
            WaterOpticsImage control = fixture.CaptureMixed(grid, reverse: false, bilinearControl: true);
            WaterOpticsImage reversed = fixture.CaptureMixed(grid, reverse: true, bilinearControl: false);
            float delta = MaximumRgbDifference(mixed, control);
            float reversal = MaximumRgbDifference(mixed, reversed);
            comparisons.Add(new { Grid = grid.ToString(), MaximumRgbDistance = delta, ReversalDistance = reversal });
            Write("mixed-capabilities", comparisons);
            Assert.True(delta <= EquivalentRgbTolerance, $"{grid} mixed capability RGB distance {delta:R}.");
            Assert.Equal(mixed.Rgba, reversed.Rgba);
            // Nonvacuity. The triangle strip crosses genuinely wet bed, beside the submerged object.
            Assert.True(Vector3.Distance(mixed.Sample("object"), fixture.Captures[WaterOpticsFullRenderMode.Opaque].Image.Sample("object")) > .12f);
        }
    }

    static float MaximumRgbDifference(WaterOpticsImage a, WaterOpticsImage b)
    {
        Assert.Equal(a.Rgba.Length, b.Rgba.Length);
        float maximum = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
        {
            int r = a.Rgba[i] - b.Rgba[i], g = a.Rgba[i + 1] - b.Rgba[i + 1], blue = a.Rgba[i + 2] - b.Rgba[i + 2];
            maximum = MathF.Max(maximum, MathF.Sqrt(r * r + g * g + blue * blue) / 255);
        }
        return maximum;
    }

    void PrintTiming(WaterOpticsFullCapture capture, WaterOpticsTriangleFullRenderScene? source = null)
    {
        source ??= fixture;
        output.WriteLine($"{source.Backend} {source.DeviceName}, {capture.Mode}, K={capture.MaximumBinOccupancy}, "
            + $"scene construction incl shader compilation {source.SceneConstructionIncludingShaderCompilationMs:F3} ms, "
            + $"cold submit/completion {capture.ColdFrame.SubmitAndCompletionWallMs:F3} ms, "
            + $"warm complete-frame GPU {capture.WarmFrame.CompletedFrameGpuMs?.ToString("F3") ?? "unavailable"} ms "
            + $"({capture.WarmFrame.GpuTimingStatus}), warm CPU encode {capture.WarmFrame.EncodeCpuMs:F3} ms, "
            + $"warm submit/completion wall {capture.WarmFrame.SubmitAndCompletionWallMs:F3} ms.");
    }

    void Write<T>(string name, T metrics) => File.WriteAllText(Path.Combine(fixture.DirectoryPath, name + ".json"),
        JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
}
