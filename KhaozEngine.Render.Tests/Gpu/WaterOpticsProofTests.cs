using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Imaging;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class WaterOpticsProofTests : IClassFixture<WaterOpticsProofScene>
{
    readonly WaterOpticsProofScene _fixture;

    public WaterOpticsProofTests(WaterOpticsProofScene fixture) => _fixture = fixture;

    // Missing skinned submission, a hidden bank or a bad projection must fail this fixture check.
    // The current submerged colour is recorded, without treating it as optical acceptance.
    [GpuFact]
    public void FixtureShowsSkinnedObjectAndBank()
    {
        var settings = new WaterOpticsCase();
        WaterOpticsImage image = _fixture.Capture(WaterOpticsScenario.SkinnedBody, settings);
        WaterOpticsImage noObject = _fixture.Capture(WaterOpticsScenario.SlopingBank, settings);
        WaterOpticsImage noBank = _fixture.Capture(WaterOpticsScenario.FlatBed, settings);
        WaterOpticsImage opaque = _fixture.Capture(WaterOpticsScenario.SkinnedBody, settings,
            plane => new WaterPlane(plane.CenterX, plane.SurfaceY, plane.CenterZ,
                plane.HalfExtentX, plane.HalfExtentZ, new WaterLook { Opacity = 0f }));
        Save("skinned-body-legacy", image);
        Save("sloping-bank-legacy", noObject);
        Save("flat-bed-legacy", noBank);
        Save("skinned-body-no-water", opaque);

        AssertVisible(image);
        Assert.True(Vector3.Distance(image.Sample("dry"), noObject.Sample("dry")) > 0.08f,
            "The dry sample must see the real skinned tube rather than the bed behind it.");
        Assert.True(Vector3.Distance(image.Sample("submerged"), noObject.Sample("submerged")) > 0.08f,
            "The 0.103 m sample must see submerged geometry rather than unobstructed water.");
        Assert.True(Vector3.Distance(image.Sample("bank"), noBank.Sample("bank")) > 0.08f,
            "The bank sample must distinguish the authored slope from the flat bed.");
        Assert.True(Vector3.Distance(image.Sample("open"), opaque.Sample("open")) > 0.08f,
            "The public plane callback must switch visible water off over the same opaque scene.");
        Assert.True(Vector3.Distance(image.Sample("dry"), opaque.Sample("dry"))
            <= GoldenCompare.InSessionTolerance, "Above-water geometry must occlude water.");
    }

    // Stale water submissions, camera state or render targets must not survive a capture change.
    [GpuFact]
    public void AgedFixtureMatchesFreshScene()
    {
        WaterOpticsImage bed30 = _fixture.Capture(WaterOpticsScenario.FlatBed,
            new WaterOpticsCase(BedDepth: 30f));
        WaterOpticsImage bed100 = _fixture.Capture(WaterOpticsScenario.DeepShelf,
            new WaterOpticsCase(BedDepth: 100f));
        Save("flat-bed-30m-legacy", bed30);
        Save("deep-shelf-100m-legacy", bed100);
        Assert.True(Vector3.Distance(bed30.Sample("open"), bed30.Sample("background")) > 0.08f,
            "The 30 m bed must cover the perspective rays through the water.");
        Assert.True(Vector3.Distance(bed100.Sample("open"), bed100.Sample("background")) > 0.08f,
            "The 100 m shelf must cover the perspective rays through the water.");
        Assert.True(Vector3.Distance(bed100.Sample("far-water"), bed100.Sample("background")) > 0.08f,
            "The deep bed at the far water edge must remain inside the perspective frustum.");
        Save("ledge-legacy", _fixture.Capture(WaterOpticsScenario.Ledge, new WaterOpticsCase()));
        Save("stacked-bodies-legacy", _fixture.Capture(WaterOpticsScenario.StackedBodies,
            new WaterOpticsCase(Offset: new Vector3(16384f, 128f, -16384f), ReverseOrder: true)));
        Save("adjacent-fields-legacy", _fixture.Capture(WaterOpticsScenario.AdjacentFields,
            new WaterOpticsCase(Samples: 4, ReverseOrder: true)));
        Save("sloping-bank-temporal-legacy", _fixture.Capture(WaterOpticsScenario.SlopingBank,
            new WaterOpticsCase(Temporal: true)));

        var settings = new WaterOpticsCase();
        WaterOpticsImage aged = _fixture.Capture(WaterOpticsScenario.SkinnedBody, settings);
        using var freshFixture = new WaterOpticsProofScene();
        WaterOpticsImage fresh = freshFixture.Capture(WaterOpticsScenario.SkinnedBody, settings);
        Save("aged-skinned-body-legacy", aged);
        Save("fresh-skinned-body-legacy", fresh);
        AssertVisible(aged);
        AssertVisible(fresh);

        float[] a = GoldenCompare.Downsample(aged.Rgba, aged.Width, aged.Height);
        float[] b = GoldenCompare.Downsample(fresh.Rgba, fresh.Width, fresh.Height);
        float worst = 0f;
        for (int i = 0; i < a.Length; i++) worst = MathF.Max(worst, MathF.Abs(a[i] - b[i]));
        WriteMetrics("aged-fresh", new
        {
            WorstGridChannelDifference = worst,
            Tolerance = GoldenCompare.InSessionTolerance
        });
        Assert.True(worst <= GoldenCompare.InSessionTolerance,
            $"Aged/fresh grid difference {worst:F6} exceeds {GoldenCompare.InSessionTolerance:F6}.");
    }

    // An upper-only change must not contaminate any of the lower body's 25 sampled pixels.
    [GpuFact]
    public void StackedLowerProbeIsIsolatedFromUpperWater()
    {
        var settings = new WaterOpticsCase();
        WaterOpticsImage both = _fixture.Capture(WaterOpticsScenario.StackedBodies, settings);
        WaterOpticsImage lowerOnly = _fixture.Capture(WaterOpticsScenario.StackedBodies, settings,
            plane => plane.SurfaceY > 0f
                ? new WaterPlane(plane.CenterX, plane.SurfaceY, plane.CenterZ,
                    plane.HalfExtentX, plane.HalfExtentZ, new WaterLook { Opacity = 0f })
                : plane);
        Save("stacked-both", both);
        Save("stacked-upper-off", lowerOnly);

        Vector2 point = both.Samples["lower"];
        int cx = (int)(point.X + 0.5f), cy = (int)(point.Y + 0.5f);
        int changed = 0;
        for (int y = cy - 2; y <= cy + 2; y++)
            for (int x = cx - 2; x <= cx + 2; x++)
                for (int channel = 0; channel < 4; channel++)
                {
                    int i = (y * both.Width + x) * 4 + channel;
                    if (both.Rgba[i] != lowerOnly.Rgba[i]) changed++;
                }
        float upperDifference = Vector3.Distance(both.Sample("upper"), lowerOnly.Sample("upper"));
        WriteMetrics("stacked-isolation", new
        {
            LowerPixel = new[] { point.X, point.Y },
            FirstRow = cy - 2,
            LastRow = cy + 2,
            ChangedLowerChannels = changed,
            UpperSampleDifference = upperDifference
        });
        Assert.True(upperDifference > 0.08f,
            "The upper probe must respond to upper water being switched off, so isolation is not vacuous.");
        Assert.True(changed == 0,
            $"Changing only upper water changed {changed} channels in the lower body's 5 by 5 patch.");
    }

    static void AssertVisible(WaterOpticsImage image)
    {
        Assert.Equal(384, image.Width);
        Assert.Equal(384, image.Height);
        Assert.Equal(384 * 384 * 4, image.Rgba.Length);
        Vector3 background = image.Sample("background");
        foreach (string sample in new[] { "dry", "submerged", "bank" })
        {
            Vector2 point = image.Samples[sample];
            Assert.InRange(point.X, 2f, image.Width - 3f);
            Assert.InRange(point.Y, 2f, image.Height - 3f);
            Assert.True(Vector3.Distance(image.Sample(sample), background) > 0.08f,
                $"The {sample} patch must contain scene geometry, not background.");
        }
    }

    internal static void Save(string name, WaterOpticsImage image)
    {
        string? directory = Environment.GetEnvironmentVariable("KE_WATER_OPTICS_OUTPUT");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        PngWriter.Save(Path.Combine(directory, name + ".png"), image.Rgba, image.Width, image.Height);
        WriteMetrics(name, new
        {
            image.Width,
            image.Height,
            Samples = System.Linq.Enumerable.ToDictionary(image.Samples, p => p.Key, p => new
            {
                Pixel = new[] { p.Value.X, p.Value.Y },
                Rgb = new[] { image.Sample(p.Key).X, image.Sample(p.Key).Y, image.Sample(p.Key).Z }
            })
        });
    }

    internal static void WriteMetrics<T>(string name, T metrics)
    {
        string? directory = Environment.GetEnvironmentVariable("KE_WATER_OPTICS_OUTPUT");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"),
            JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
    }
}
