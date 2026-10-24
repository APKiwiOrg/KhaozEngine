using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Tests.Gpu.WaterOpticsProofTests;

namespace KhaozEngine.Tests.Gpu;

public sealed class WaterOpticsGoldenTests(WaterOpticsProofScene fixture) : IClassFixture<WaterOpticsProofScene>
{
    WaterOpticsHandle _deep, _shallow, _bank;

    void Prepare()
    {
        if (_deep.IsValid) return;
        fixture.Prepare(scene =>
        {
            _deep = scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [2f, 2f, 2f, 2f]));
            _shallow = scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [0.05f, 0.05f, 0.05f, 0.05f]));
            var depths = new float[33 * 2];
            for (int z = 0; z < 2; z++)
                for (int x = 0; x < 33; x++)
                {
                    float worldX = -4f + x * 0.25f;
                    depths[z * 33 + x] = worldX < 0.5f || worldX > 3.5f ? 2f : 1f - worldX * 0.5f;
                }
            _bank = scene.LoadWaterOpticsField(new WaterOpticsField(33, 2, depths));
        });
    }

    WaterPlane Bind(WaterPlane plane, WaterOpticsHandle handle)
        => plane.WithOptics(new WaterOpticsBinding(handle,
            new Vector3(plane.CenterX - plane.HalfExtentX, plane.SurfaceY, plane.CenterZ - plane.HalfExtentZ),
            new Vector2(plane.HalfExtentX * 2f, plane.HalfExtentZ * 2f)));

    // Reverting bed-depth routing must expose the skin as a false shore, despite equal opaque RGB.
    [GpuFact]
    public void BoundBedDoesNotBecomeAShoreWhenASkinMoves()
    {
        Prepare();
        foreach (float depth in new[] { 0.103f, 0.15f })
        {
            var settings = new WaterOpticsCase(ObjectDepth: depth, ControlledLighting: true);
            WaterOpticsImage body = fixture.Capture(WaterOpticsScenario.SkinnedBody, settings, p => Bind(p, _bank));
            WaterOpticsImage bed = fixture.Capture(WaterOpticsScenario.SlopingBank, settings, p => Bind(p, _bank));
            WaterOpticsImage legacy = fixture.Capture(WaterOpticsScenario.SkinnedBody, settings);
            Save($"controlled-skin-{depth:F3}-bound", body);
            Save($"controlled-bed-{depth:F3}-bound", bed);
            Save($"controlled-skin-{depth:F3}-legacy", legacy);
            float difference = Vector3.Distance(body.Sample("submerged"), bed.Sample("submerged"));
            WriteMetrics($"bed-skin-{depth:F3}", new { Difference = difference, Maximum = 0.04f });
            Assert.True(difference <= 0.04f, $"Bed/skin RGB distance {difference:F6} exceeds 0.04 at {depth} m.");
        }
    }

    // Disabling the hardware depth test would paint water over the dry skin, independently of bed depth.
    [GpuFact]
    public void AboveWaterSkinStillOccludesWater()
    {
        Prepare();
        var settings = new WaterOpticsCase(ControlledLighting: true);
        WaterOpticsImage bound = fixture.Capture(WaterOpticsScenario.SkinnedBody, settings, p => Bind(p, _bank));
        WaterOpticsImage legacy = fixture.Capture(WaterOpticsScenario.SkinnedBody, settings);
        WaterOpticsImage opaque = fixture.Capture(WaterOpticsScenario.SkinnedBody, settings, WaterOff);
        Save("above-water-bound", bound);
        Save("above-water-opaque", opaque);
        float difference = Vector3.Distance(bound.Sample("dry"), legacy.Sample("dry"));
        float opaqueDifference = Vector3.Distance(bound.Sample("dry"), opaque.Sample("dry"));
        WriteMetrics("above-water", new { Difference = difference, OpaqueDifference = opaqueDifference, Maximum = 0.02f });
        Assert.True(difference < 0.02f && opaqueDifference < 0.02f);
        var inspection = new WaterOpticsCase();
        Save("inspection-skinned-bound", fixture.Capture(WaterOpticsScenario.SkinnedBody, inspection, p => Bind(p, _bank)));
        Save("inspection-skinned-legacy", fixture.Capture(WaterOpticsScenario.SkinnedBody, inspection));
        Save("inspection-bank-bound", fixture.Capture(WaterOpticsScenario.SlopingBank, inspection, p => Bind(p, _bank)));
    }

    // A shared look or last-submission cache must not let an upper field alter the isolated lower probe.
    [GpuFact]
    public void TwoLevelsAndReversedSubmissionKeepTheirOwnFields()
    {
        Prepare();
        var look = new WaterLook();
        foreach (bool reverse in new[] { false, true })
        {
            var settings = new WaterOpticsCase(ReverseOrder: reverse, ControlledLighting: true);
            WaterPlane Select(WaterPlane p, bool lowerShallow, bool upperShallow)
            {
                var sameLook = new WaterPlane(p.CenterX, p.SurfaceY, p.CenterZ, p.HalfExtentX, p.HalfExtentZ, look);
                return Bind(sameLook, (p.SurfaceY > 1f ? upperShallow : lowerShallow) ? _shallow : _deep);
            }
            WaterOpticsImage deep = fixture.Capture(WaterOpticsScenario.StackedBodies, settings, p => Select(p, false, false));
            WaterOpticsImage upper = fixture.Capture(WaterOpticsScenario.StackedBodies, settings, p => Select(p, false, true));
            WaterOpticsImage lower = fixture.Capture(WaterOpticsScenario.StackedBodies, settings, p => Select(p, true, false));
            Save($"stacked-deep-{reverse}", deep);
            Save($"stacked-upper-shallow-{reverse}", upper);
            Save($"stacked-lower-shallow-{reverse}", lower);
            float lowerIsolation = Vector3.Distance(deep.Sample("lower"), upper.Sample("lower"));
            float lowerResponse = Vector3.Distance(deep.Sample("lower"), lower.Sample("lower"));
            float upperResponse = Vector3.Distance(deep.Sample("upper"), upper.Sample("upper"));
            WriteMetrics($"stacked-fields-{reverse}", new { lowerIsolation, lowerResponse, upperResponse });
            Assert.True(lowerIsolation <= GoldenCompare.InSessionTolerance, $"Lower isolation {lowerIsolation}.");
            Assert.True(lowerResponse > 0.04f, $"Lower field had no optical effect ({lowerResponse}).");
            Assert.True(upperResponse > 0.04f, $"Upper field had no optical effect ({upperResponse}).");
        }
    }

    [GpuFact]
    public void LegacyNoBindingMatchesItsControl()
    {
        Prepare();
        var settings = new WaterOpticsCase();
        WaterOpticsImage legacy = fixture.Capture(WaterOpticsScenario.SkinnedBody, settings);
        // Age the per-field cache between two legacy captures.
        fixture.Capture(WaterOpticsScenario.SkinnedBody, settings, p => Bind(p, _bank));
        WaterOpticsImage control = fixture.Capture(WaterOpticsScenario.SkinnedBody, settings, p => p.WithOptics(default));
        Save("legacy-control", control);
        Assert.Equal(legacy.Rgba, control.Rgba);
    }

    // Reducing the already-relative binding a second time, or sampling wpAbsXz, detaches this bank from its field.
    [GpuFact]
    public void BoundMappingUsesRenderRelativeCoordinates()
    {
        Prepare();
        var settings = new WaterOpticsCase(Offset: new Vector3(16384f, 128f, -16384f), ControlledLighting: true);
        WaterOpticsImage body = fixture.Capture(WaterOpticsScenario.SkinnedBody, settings, p => Bind(p, _bank));
        WaterOpticsImage bed = fixture.Capture(WaterOpticsScenario.SlopingBank, settings, p => Bind(p, _bank));
        Save("rebased-controlled-skin", body);
        Save("rebased-controlled-bed", bed);
        float distance = Vector3.Distance(body.Sample("submerged"), bed.Sample("submerged"));
        WriteMetrics("rebased-bed-skin", new { Difference = distance, Maximum = 0.04f });
        Assert.True(distance <= 0.04f, $"Rebased bed/skin RGB distance {distance:F6} exceeds 0.04.");
    }

    // The authored ramp ends at x = 3.5. Beyond it the field must describe the bed rather than extrapolated land.
    [GpuFact]
    public void BankFieldDoesNotInventLandBeyondTheAuthoredRamp()
    {
        Prepare();
        var settings = new WaterOpticsCase(ControlledLighting: true);
        WaterOpticsImage bank = fixture.Capture(WaterOpticsScenario.SlopingBank, settings, p => Bind(p, _bank));
        WaterOpticsImage flat = fixture.Capture(WaterOpticsScenario.FlatBed, settings, p => Bind(p, _deep));
        Save("bank-edge-bound", bank);
        Save("bank-edge-bed-control", flat);
        float distance = Vector3.Distance(bank.Sample("beyond-bank"), flat.Sample("beyond-bank"));
        WriteMetrics("bank-edge", new { Difference = distance, Maximum = 0.04f });
        Assert.True(distance <= 0.04f, $"Beyond-bank RGB distance {distance:F6} exceeds 0.04.");
    }

    [GpuFact]
    public void AgedBoundFixtureMatchesFreshScene()
    {
        Prepare();
        var settings = new WaterOpticsCase();
        fixture.Capture(WaterOpticsScenario.StackedBodies, new WaterOpticsCase(Samples: 4), p => Bind(p, _deep));
        WaterOpticsImage aged = fixture.Capture(WaterOpticsScenario.SkinnedBody, settings, p => Bind(p, _bank));
        using var fresh = new WaterOpticsProofScene();
        WaterOpticsHandle handle = default;
        // The same independent authored slope, loaded outside capture recording.
        fresh.Prepare(scene =>
        {
            var depths = new float[66];
            for (int z = 0; z < 2; z++)
                for (int x = 0; x < 33; x++)
                {
                    float worldX = -4f + x * 0.25f;
                    depths[z * 33 + x] = worldX < 0.5f || worldX > 3.5f ? 2f : 1f - worldX * 0.5f;
                }
            handle = scene.LoadWaterOpticsField(new WaterOpticsField(33, 2, depths));
        });
        WaterOpticsImage current = fresh.Capture(WaterOpticsScenario.SkinnedBody, settings, p => Bind(p, handle));
        Save("aged-bound", aged);
        Save("fresh-bound", current);
        Assert.Equal(aged.Rgba, current.Rgba);
    }

    static WaterPlane WaterOff(WaterPlane p) => new(p.CenterX, p.SurfaceY, p.CenterZ,
        p.HalfExtentX, p.HalfExtentZ, new WaterLook { Opacity = 0f });
}
