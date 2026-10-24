using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Tests.Gpu.WaterOpticsProofTests;

namespace KhaozEngine.Tests.Gpu;

public sealed class WaterOpticsGoldenTests(WaterOpticsProofScene fixture) : IClassFixture<WaterOpticsProofScene>
{
    /// <summary>The approved RGB allowance between a wet probe and its bed-only reference.</summary>
    const float Allowance = 0.04f;
    /// <summary>The approved ledge top, below the water surface.</summary>
    const float LedgeDepth = 0.3f;

    WaterOpticsHandle _deep, _shallow, _bank, _bank30, _bank100, _left, _right, _tiltedBank, _tiltedBankDeeper, _slice, _sliceFlipped, _sliceRowError, _upperCave;

    void Prepare()
    {
        _deep = fixture.Field("deep", () => new WaterOpticsField(2, 2, [2f, 2f, 2f, 2f]));
        _shallow = fixture.Field("shallow", () => new WaterOpticsField(2, 2, [0.05f, 0.05f, 0.05f, 0.05f]));
        _bank = fixture.Field("bank", () => new WaterOpticsField(33, 2, WaterOpticsProofScene.BankDepths(2f, 33, -4f, 4f)));
        _bank30 = fixture.Field("bank-30", () => new WaterOpticsField(33, 2, WaterOpticsProofScene.BankDepths(30f, 33, -4f, 4f)));
        _bank100 = fixture.Field("bank-100", () => new WaterOpticsField(33, 2, WaterOpticsProofScene.BankDepths(100f, 33, -4f, 4f)));
        (_left, _right) = Halves(fixture);
        _tiltedBank = fixture.Field("tilted-bank", () => TiltedBank(0f));
        _tiltedBankDeeper = fixture.Field("tilted-bank-deeper", () => TiltedBank(0.1f));
        _slice = fixture.Field("tilted-slice-0.5", () => TiltedSlice(0.5f));
        _sliceFlipped = fixture.Field("tilted-slice-minus-0.5", () => TiltedSlice(-0.5f));
        _sliceRowError = fixture.Field("tilted-slice-0", () => TiltedSlice(0f));
        // The upper body's own bed: the enclosed space's roof at y = 1.2 over [-3.9, -0.9], else the floor at -2.
        _upperCave = fixture.Field("upper-cave", () =>
        {
            var depths = new float[33 * 2];
            for (int x = 0; x < 33; x++)
            {
                float worldX = -4f + x * 0.25f;
                depths[x] = depths[33 + x] = worldX >= -3.9f && worldX <= -0.9f ? 0.2f : 3.4f;
            }
            return new WaterOpticsField(33, 2, depths);
        });
    }

    /// <summary>The two 17-column halves of the continuous 33-column bank, sharing the x = 0 column.</summary>
    static (WaterOpticsHandle Left, WaterOpticsHandle Right) Halves(WaterOpticsProofScene scene) => (
        scene.Field("bank-left", () => new WaterOpticsField(17, 2, WaterOpticsProofScene.BankDepths(2f, 17, -4f, 0f))),
        scene.Field("bank-right", () => new WaterOpticsField(17, 2, WaterOpticsProofScene.BankDepths(2f, 17, 0f, 4f))));

    static WaterPlane Bind(WaterPlane plane, WaterOpticsHandle handle)
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

    // Ignoring the bound field would expose the 0.3 m ledge as pale shallows. A precision loss at 30 m or
    // 100 m would band the open probes against the scene-depth reference of the same bed without the ledge.
    [GpuFact]
    public void BedDepthAt2_30_100MetresMatchesItsReference()
    {
        Prepare();
        string[] probes = ["submerged", "ledge-edge", "open", "far-water", "seam-left"];
        foreach ((float depth, WaterOpticsHandle field) in new[] { (2f, _bank), (30f, _bank30), (100f, _bank100) })
        {
            var settings = new WaterOpticsCase(BedDepth: depth, ObjectDepth: LedgeDepth, ControlledLighting: true);
            WaterOpticsImage wet = fixture.Capture(WaterOpticsScenario.Ledge, settings, p => Bind(p, field));
            WaterOpticsImage bed = fixture.Capture(WaterOpticsScenario.SlopingBank, settings);
            WaterOpticsImage legacy = fixture.Capture(WaterOpticsScenario.Ledge, settings);
            Save($"depth-{depth:F0}-ledge-bound", wet);
            Save($"depth-{depth:F0}-bed-reference", bed);
            Save($"depth-{depth:F0}-ledge-legacy", legacy);
            Dictionary<string, float> distances = Measure(wet, bed, probes);
            float witness = Vector3.Distance(legacy.Sample("submerged"), bed.Sample("submerged"));
            WriteMetrics($"depth-{depth:F0}", new { Distances = distances, LegacyLedgeWitness = witness, Maximum = Allowance });
            AssertWithin(distances, $"{depth} m bed");
            Assert.True(witness > Allowance, $"The legacy ledge must differ from the {depth} m bed ({witness:F6}).");
        }
    }

    // Grading or feathering by the ledge's scene depth would band its top, edges and front face.
    [GpuFact]
    public void SubmergedLedgeCreatesNoFalseShoreBand()
    {
        Prepare();
        string[] transect = ["submerged", "ledge-edge", "beside-ledge", "ledge-side", "open"];
        var settings = new WaterOpticsCase(ObjectDepth: LedgeDepth, ControlledLighting: true);
        WaterOpticsImage wet = fixture.Capture(WaterOpticsScenario.Ledge, settings, p => Bind(p, _bank));
        WaterOpticsImage bed = fixture.Capture(WaterOpticsScenario.SlopingBank, settings);
        WaterOpticsImage legacy = fixture.Capture(WaterOpticsScenario.Ledge, settings);
        WaterOpticsImage dryLedge = fixture.Capture(WaterOpticsScenario.Ledge, settings, WaterOff);
        WaterOpticsImage dryBed = fixture.Capture(WaterOpticsScenario.SlopingBank, settings, WaterOff);
        Save("ledge-band-bound", wet);
        Save("ledge-band-bed-reference", bed);
        Save("ledge-band-legacy", legacy);
        Save("ledge-band-water-off", dryLedge);
        Dictionary<string, float> distances = Measure(wet, bed, transect);
        Dictionary<string, float> legacyBand = Measure(legacy, bed, transect);
        Dictionary<string, float> opaque = Measure(dryLedge, dryBed, transect);
        WriteMetrics("ledge-band", new { Distances = distances, LegacyBand = legacyBand, Opaque = opaque, Maximum = Allowance });
        Assert.True(opaque.Values.Max() <= 0.02f, $"The ledge's opaque colour must match the bed ({opaque.Values.Max():F6}).");
        AssertWithin(distances, "Ledge transect");
        Assert.True(legacyBand["submerged"] > Allowance,
            $"The legacy ledge top must show its shallow band ({legacyBand["submerged"]:F6}).");
    }

    // Resolving a field by XZ, by the last submission or by the scene's deepest bed would swap these beds.
    [GpuFact]
    public void StackedCaveBodiesKeepIndependentFields()
    {
        Prepare();
        string[] probes = ["lower", "upper", "upper-cave"];
        foreach (bool reverse in new[] { false, true })
        {
            var settings = new WaterOpticsCase(ReverseOrder: reverse, ControlledLighting: true);
            WaterOpticsImage wet = fixture.Capture(WaterOpticsScenario.StackedBodies, settings,
                p => Bind(p, p.SurfaceY > 1f ? _upperCave : _deep));
            WaterOpticsImage bed = fixture.Capture(WaterOpticsScenario.StackedBodies, settings);
            WaterOpticsImage swapped = fixture.Capture(WaterOpticsScenario.StackedBodies, settings,
                p => Bind(p, p.SurfaceY > 1f ? _deep : _upperCave));
            Save($"cave-bound-{reverse}", wet);
            Save($"cave-bed-reference-{reverse}", bed);
            Save($"cave-swapped-{reverse}", swapped);
            Dictionary<string, float> distances = Measure(wet, bed, probes);
            Dictionary<string, float> swap = Measure(swapped, bed, probes);
            WriteMetrics($"cave-fields-{reverse}", new { Distances = distances, Swapped = swap, Maximum = Allowance });
            AssertWithin(distances, $"Stacked cave (reverse {reverse})");
            Assert.True(swap.Values.Max() > Allowance, $"Swapped fields must be visible ({swap.Values.Max():F6}).");
        }
    }

    // A per-patch mapping error or an internal cell edge treated as shore would break the shared x = 0 column.
    [GpuFact]
    public void AdjacentFieldsMatchOneContinuousField()
    {
        Prepare();
        string[] probes = ["seam-left", "seam-right", "open", "ramp-deep", "ramp-mid", "shore", "beyond-bank"];
        string[] flat = ["seam-left", "seam-right", "open"];
        var settings = new WaterOpticsCase(ControlledLighting: true);
        WaterOpticsImage patches = fixture.Capture(WaterOpticsScenario.AdjacentFields, settings,
            p => Bind(p, p.CenterX < 0f ? _left : _right));
        WaterOpticsImage continuous = fixture.Capture(WaterOpticsScenario.SlopingBank, settings, p => Bind(p, _bank));
        WaterOpticsImage bed = fixture.Capture(WaterOpticsScenario.SlopingBank, settings);
        WaterOpticsImage wrongPatch = fixture.Capture(WaterOpticsScenario.AdjacentFields, settings, p => Bind(p, _left));
        Save("adjacent-bound", patches);
        Save("adjacent-continuous", continuous);
        Save("adjacent-bed-reference", bed);
        Save("adjacent-wrong-patch", wrongPatch);
        Dictionary<string, float> distances = Measure(patches, continuous, probes);
        Dictionary<string, float> reference = Measure(patches, bed, flat);
        Dictionary<string, float> wrong = Measure(wrongPatch, continuous, probes);
        WriteMetrics("adjacent-fields", new { Distances = distances, BedReference = reference, WrongPatch = wrong, Maximum = Allowance });
        AssertWithin(distances, "Adjacent patches against one field");
        AssertWithin(reference, "Adjacent patches against the bed");
        Assert.True(wrong.Values.Max() > Allowance, $"The wrong right-hand patch must be visible ({wrong.Values.Max():F6}).");
    }

    // Sampling wpAbsXz, reducing the relative binding twice, or rebasing only one axis slides this tilted bank
    // against its origin control. A flipped V or a wrong Z source is consistent across frames, so the rebased
    // tilted field is also matched at shore-surface (z = 0.5) with an independent slice that is uniform along Z.
    // Ripple detail is world-anchored, so it is disabled to compare the rebased frame with an origin control.
    [GpuFact]
    public void RenderOriginShiftKeepsOpticalSampling()
    {
        Prepare();
        string[] probes = ["ramp-deep", "ramp-mid", "shore", "shore-surface", "open", "beyond-bank"];
        string[] slice = ["shore-surface"];
        var control = new WaterOpticsCase(ControlledLighting: true);
        var shifted = control with { Offset = new Vector3(4096f, 0f, -8192f) };
        WaterOpticsImage origin = fixture.Capture(WaterOpticsScenario.SlopingBank, control, p => Wide(p, _tiltedBank));
        WaterOpticsImage rebased = fixture.Capture(WaterOpticsScenario.SlopingBank, shifted, p => Wide(p, _tiltedBank));
        WaterOpticsImage wrongX = fixture.Capture(WaterOpticsScenario.SlopingBank, shifted,
            p => Wide(p, _tiltedBank, shiftX: 0.5f));
        WaterOpticsImage wrongZ = fixture.Capture(WaterOpticsScenario.SlopingBank, shifted,
            p => Wide(p, _tiltedBank, shiftZ: 0.5f));
        WaterOpticsImage changedDepth = fixture.Capture(WaterOpticsScenario.SlopingBank, shifted, p => Wide(p, _tiltedBankDeeper));
        WaterOpticsImage sliceReference = fixture.Capture(WaterOpticsScenario.SlopingBank, shifted, p => Wide(p, _slice));
        WaterOpticsImage flippedRow = fixture.Capture(WaterOpticsScenario.SlopingBank, shifted, p => Wide(p, _sliceFlipped));
        WaterOpticsImage wrongRow = fixture.Capture(WaterOpticsScenario.SlopingBank, shifted, p => Wide(p, _sliceRowError));
        Save("rebase-origin-control", origin);
        Save("rebase-shifted", rebased);
        Save("rebase-wrong-mapping-x", wrongX);
        Save("rebase-wrong-mapping-z", wrongZ);
        Save("rebase-changed-depth", changedDepth);
        Save("rebase-slice-reference", sliceReference);
        Save("rebase-slice-flipped-row", flippedRow);
        Dictionary<string, float> distances = Measure(rebased, origin, probes);
        Dictionary<string, float> mappingX = Measure(wrongX, origin, probes);
        Dictionary<string, float> mappingZ = Measure(wrongZ, origin, probes);
        Dictionary<string, float> depth = Measure(changedDepth, origin, probes);
        Dictionary<string, float> sliceMatch = Measure(rebased, sliceReference, slice);
        Dictionary<string, float> flipped = Measure(flippedRow, sliceReference, slice);
        Dictionary<string, float> rowError = Measure(wrongRow, sliceReference, slice);
        WriteMetrics("rebase-sampling", new { Distances = distances, WrongMappingHalfMetreX = mappingX,
            WrongMappingHalfMetreZ = mappingZ, DepthPlusTenCentimetres = depth, SliceReference = sliceMatch,
            FlippedRowZMinusHalf = flipped, RowErrorZZero = rowError, Maximum = Allowance });
        AssertWithin(distances, "Rebased bank");
        AssertWithin(sliceMatch, "Rebased bank against its z = 0.5 slice");
        Assert.True(flipped["shore-surface"] > Allowance,
            $"The flipped z = -0.5 row must be visible ({flipped["shore-surface"]:F6}).");
        Assert.True(rowError["shore-surface"] > Allowance,
            $"The z = 0 row must be visible ({rowError["shore-surface"]:F6}).");
        Assert.True(mappingX.Values.Max() > Allowance, $"A 0.5 m X mapping error must be visible ({mappingX.Values.Max():F6}).");
        Assert.True(mappingZ.Values.Max() > Allowance, $"A 0.5 m Z mapping error must be visible ({mappingZ.Values.Max():F6}).");
        Assert.True(depth.Values.Max() > Allowance, $"A 0.1 m depth change must be visible ({depth.Values.Max():F6}).");
    }

    // Reading the previous frame's depth or an unresolved sample would expose the moved skin as a false shore.
    [GpuFact]
    public void TemporalJitterAndMsaaUseCurrentDepth()
    {
        Prepare();
        string[] probes = ["submerged", "open", "seam-left"];
        foreach ((string mode, WaterOpticsCase basis) in new[]
        {
            ("temporal", new WaterOpticsCase(Temporal: true, ControlledLighting: true)),
            ("msaa4", new WaterOpticsCase(Samples: 4, ControlledLighting: true))
        })
            foreach (float objectDepth in new[] { 0.15f, 0.103f })
            {
                WaterOpticsCase settings = basis with { ObjectDepth = objectDepth };
                WaterOpticsImage wet = fixture.Capture(WaterOpticsScenario.SkinnedBody, settings, p => Bind(p, _bank));
                WaterOpticsImage bed = fixture.Capture(WaterOpticsScenario.SlopingBank, settings);
                WaterOpticsImage opaque = fixture.Capture(WaterOpticsScenario.SkinnedBody, settings, WaterOff);
                Save($"aa-{mode}-{objectDepth:F3}-bound", wet);
                Save($"aa-{mode}-{objectDepth:F3}-bed-reference", bed);
                Dictionary<string, float> distances = Measure(wet, bed, probes);
                float occlusion = Vector3.Distance(wet.Sample("dry"), opaque.Sample("dry"));
                WriteMetrics($"aa-{mode}-{objectDepth:F3}", new { Distances = distances, DryOcclusion = occlusion, Maximum = Allowance });
                AssertWithin(distances, $"{mode} at {objectDepth} m");
                Assert.True(occlusion <= Allowance, $"{mode} dry skin occlusion {occlusion:F6} exceeds {Allowance}.");
            }
    }

    // A clamped, downscaled or transposed maximum texture would move the probes near its final row and column.
    [GpuFact]
    public void MaximumFieldR32SamplesOnThisBackend()
    {
        Prepare();
        const int size = WaterOpticsField.MaxDimension;
        WaterOpticsStats before = default, loaded = default, after = default;
        WaterOpticsHandle maximum = default;
        fixture.Prepare(scene =>
        {
            before = scene.WaterOpticsStats;
            maximum = scene.LoadWaterOpticsField(new WaterOpticsField(size, size,
                WaterOpticsProofScene.BankDepths(2f, size, -4f, 4f, size)));
            loaded = scene.WaterOpticsStats;
        });
        try
        {
            string[] flat = ["submerged", "open", "far-water", "seam-left", "beyond-bank"];
            string[] slope = ["ramp-deep", "ramp-mid", "shore"];
            var settings = new WaterOpticsCase(ControlledLighting: true);
            WaterOpticsImage wet = fixture.Capture(WaterOpticsScenario.SkinnedBody, settings, p => Bind(p, maximum));
            WaterOpticsImage lattice = fixture.Capture(WaterOpticsScenario.SkinnedBody, settings, p => Bind(p, _bank));
            WaterOpticsImage bed = fixture.Capture(WaterOpticsScenario.SlopingBank, settings);
            Save("maximum-field-bound", wet);
            Save("maximum-field-lattice-control", lattice);
            Dictionary<string, float> reference = Measure(wet, bed, flat);
            Dictionary<string, float> control = Measure(wet, lattice, slope);
            WriteMetrics("maximum-field", new
            {
                Backend = GoldenCompare.GoldenBackendToken(KhaozEngine.Gpu.GpuBackendSelector.Select()),
                Size = size, BedReference = reference, LatticeControl = control, Maximum = Allowance,
                TextureBytes = loaded.LiveTextureBytes - before.LiveTextureBytes,
                Uploads = loaded.UploadCount - before.UploadCount
            });
            Assert.Equal((long)size * size * sizeof(float), loaded.LiveTextureBytes - before.LiveTextureBytes);
            Assert.Equal(before.UploadCount + 1, loaded.UploadCount);
            Assert.Equal(before.LiveFields + 1, loaded.LiveFields);
            AssertWithin(reference, "Maximum field against the bed");
            AssertWithin(control, "Maximum field against the 33-column lattice");
        }
        finally
        {
            fixture.Prepare(scene =>
            {
                scene.UnloadWaterOpticsField(maximum);
                after = scene.WaterOpticsStats;
            });
        }
        Assert.Equal(before.LiveFields, after.LiveFields);
    }

    // Stale resized targets, field slots or bindings from earlier configurations must not reach a later frame.
    [GpuFact]
    public void AgedBoundFixtureMatchesFreshScene()
    {
        Prepare();
        fixture.Capture(WaterOpticsScenario.StackedBodies, new WaterOpticsCase(Samples: 4), p => Bind(p, _deep));
        fixture.Capture(WaterOpticsScenario.SlopingBank,
            new WaterOpticsCase(Temporal: true, TargetWidth: 640, TargetHeight: 480), p => Bind(p, _bank30));
        fixture.Capture(WaterOpticsScenario.AdjacentFields, new WaterOpticsCase(Samples: 4, TargetWidth: 256,
            TargetHeight: 192, Offset: new Vector3(4096f, 0f, -8192f)), p => Bind(p, p.CenterX < 4096f ? _left : _right));
        fixture.Capture(WaterOpticsScenario.Ledge, new WaterOpticsCase(BedDepth: 100f, ObjectDepth: LedgeDepth,
            TargetWidth: 448, TargetHeight: 384), p => Bind(p, _bank100));
        WaterOpticsImage[] aged = Snapshots(fixture);

        using var fresh = new WaterOpticsProofScene();
        WaterOpticsImage[] current = Snapshots(fresh);
        for (int i = 0; i < aged.Length; i++)
        {
            Save($"aged-bound-{i}", aged[i]);
            Save($"fresh-bound-{i}", current[i]);
            Assert.Equal(aged[i].Width, current[i].Width);
            Assert.Equal(aged[i].Rgba, current[i].Rgba);
        }
    }

    /// <summary>Three bound snapshots over different fields and target sizes, loading fields on demand.</summary>
    static WaterOpticsImage[] Snapshots(WaterOpticsProofScene scene)
    {
        WaterOpticsHandle bank = scene.Field("bank", () =>
            new WaterOpticsField(33, 2, WaterOpticsProofScene.BankDepths(2f, 33, -4f, 4f)));
        WaterOpticsHandle wide = scene.Field("wide-bank", () =>
            new WaterOpticsField(41, 2, WaterOpticsProofScene.BankDepths(2f, 41, -5f, 5f)));
        (WaterOpticsHandle left, WaterOpticsHandle right) = Halves(scene);
        return
        [
            scene.Capture(WaterOpticsScenario.SkinnedBody, new WaterOpticsCase(), p => Bind(p, bank)),
            scene.Capture(WaterOpticsScenario.SlopingBank, new WaterOpticsCase(TargetWidth: 320, TargetHeight: 240),
                p => Wide(p, wide)),
            scene.Capture(WaterOpticsScenario.AdjacentFields, new WaterOpticsCase(TargetWidth: 512, TargetHeight: 288),
                p => Bind(p, p.CenterX < 0f ? left : right))
        ];
    }

    /// <summary>Bind the 10 m by 8 m field around this plane, without world-anchored ripple detail.
    /// <paramref name="shiftX"/> and <paramref name="shiftZ"/> deliberately displace the mapping while keeping
    /// complete coverage.</summary>
    static WaterPlane Wide(WaterPlane p, WaterOpticsHandle field, float shiftX = 0f, float shiftZ = 0f)
        => new WaterPlane(p.CenterX, p.SurfaceY, p.CenterZ, p.HalfExtentX, p.HalfExtentZ,
                new WaterLook { NormalStrength = 0f })
            .WithOptics(new WaterOpticsBinding(field,
                new Vector3(p.CenterX - 5f + shiftX, p.SurfaceY, p.CenterZ - 4f + shiftZ), new Vector2(10f, 8f)));

    /// <summary>The 10 m by 8 m bank tilted 0.1 m shallower per metre of +Z, at 0.25 m spacing on both axes, plus
    /// <paramref name="extra"/>. It is compared only with itself across a rebase, so it need not match the drawn bed.</summary>
    static WaterOpticsField TiltedBank(float extra)
    {
        const int columns = 41, rows = 33;
        float[] depths = WaterOpticsProofScene.BankDepths(2f, columns, -5f, 5f, rows);
        for (int z = 0; z < rows; z++)
        {
            float worldZ = -4f + z * 0.25f;
            for (int x = 0; x < columns; x++) depths[z * columns + x] += extra - 0.1f * worldZ;
        }
        return new WaterOpticsField(columns, rows, depths);
    }

    /// <summary>The tilted bank's depths at <paramref name="worldZ"/>, held uniform along Z over the same rectangle.
    /// Its value at z = 0.5 is the independent expectation for a correct V mapping.</summary>
    static WaterOpticsField TiltedSlice(float worldZ)
    {
        float[] depths = WaterOpticsProofScene.BankDepths(2f, 41, -5f, 5f);
        for (int i = 0; i < depths.Length; i++) depths[i] -= 0.1f * worldZ;
        return new WaterOpticsField(41, 2, depths);
    }

    static Dictionary<string, float> Measure(WaterOpticsImage wet, WaterOpticsImage reference, string[] probes)
        => probes.ToDictionary(probe => probe, probe => Vector3.Distance(wet.Sample(probe), reference.Sample(probe)));

    static void AssertWithin(Dictionary<string, float> distances, string label)
    {
        foreach (var (probe, distance) in distances)
            Assert.True(distance <= Allowance, $"{label}: {probe} RGB distance {distance:F6} exceeds {Allowance}.");
    }

    static WaterPlane WaterOff(WaterPlane p) => new(p.CenterX, p.SurfaceY, p.CenterZ,
        p.HalfExtentX, p.HalfExtentZ, new WaterLook { Opacity = 0f });
}
