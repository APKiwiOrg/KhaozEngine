using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class HangingClothWearerTests
{
    private static readonly int[] GeometrySelectedRows = [0, 3, 7, 8, 14, 21, 28, 35, 36, 43, 50, 57, 64];
    private const double DrivingStart = 4;
    private const double MeasurementStart = 8;

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void Frozen_original_cut_meets_all_detailed_display_limits(bool flat, bool batch, bool reuseMaterialFactor)
    {
        var failures = Measure(flat, batch, reuseMaterialFactor ? Mapping.Reuse : Mapping.Material);
        Assert.True(failures.Count == 0,
            $"{failures.Count} failures. {string.Join(", ", failures.GetRange(0, Math.Min(failures.Count, 15)))}");
    }

    [Theory]
    [Trait("Category", "Diagnostic")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Fitted_registration_controls_replay_the_original_cut(bool flat, bool alternate) =>
        Measure(flat, batch: flat, alternate ? Mapping.Alternate : Mapping.Fitted);

    private enum Mapping { Material, Fitted, Alternate, Reuse }

    private static List<(double Time, string Metric, float Value)> Measure(bool flat, bool batch, Mapping mapping)
    {
        var neutral = At(0, flat);
        var cut = new HangingPanelCut();
        bool geometrySelected = mapping == Mapping.Alternate;
        bool reuse = mapping == Mapping.Reuse;
        var proxy = new ClothDrapeFixture(geometrySelected ? GeometrySelectedRows : null);
        using var wearer = new HangingPanelDrawRig(cut, neutral, proxy, matchGuideLift: geometrySelected,
            useMaterialMapping: mapping is Mapping.Material or Mapping.Reuse, reuseMaterialFactor: reuse);
        using (var original = new HangingPanelDrawRig(cut, neutral))
            wearer.VerifyOriginalFallback(original, neutral);
        var gate = flat ? new FlatLipReentry(IsFlatLipEligible) : null;
        var proxyGate = flat ? new FlatLipReentry(IsFlatLipEligible) : null;
        var failures = new List<(double Time, string Metric, float Value)>();
        string? tracePath = Environment.GetEnvironmentVariable("KE_CLOTH_WEARER_TRACE");
        string outfit = flat ? "flat" : "raised";
        string entry = batch ? "batch" : "direct";
        using StreamWriter? trace = string.IsNullOrEmpty(tracePath) ? null :
            File.CreateText($"{tracePath}.{mapping.ToString().ToLowerInvariant()}.{outfit}.{entry}.jsonl");
        double prior = 0;
        double? deadline = null;
        int deadlineProbes = 0;
        bool recoveryPending = false;
        for (int frame = 0; frame <= 20 * 60; frame++)
        {
            double frameTime = frame / 60.0;
            bool released = frame > 16 * 60;
            if (flat && !released && frameTime >= MeasurementStart)
            {
                double previousFrame = (frame - 1) / 60.0 - DrivingStart;
                double currentFrame = frameTime - DrivingStart;
                if (!IsFlatLipEligible(previousFrame) && IsFlatLipEligible(currentFrame))
                {
                    double geometricEntry = FlatLipReentry.FindEntry(IsFlatLipEligible, previousFrame, currentFrame);
                    // Put the derived deadline on the inside of the inclusive interval after converting clocks.
                    deadline = Math.BitDecrement(DrivingStart + geometricEntry + FlatLipReentry.RecoveryWindowSeconds);
                }
                if (deadline is double time && time <= frameTime)
                {
                    Present(time, released, deadlineSample: true);
                    deadlineProbes++;
                    deadline = null;
                }
            }
            Present(frameTime, released, deadlineSample: false);
        }
        if (recoveryPending)
            failures.Add((prior, "open flat re-entry window", 0));
        if (flat)
            Assert.True(deadlineProbes > 0);
        return failures;

        void Present(double time, bool released, bool deadlineSample)
        {
            double driven = released ? 0 : Math.Max(0, time - DrivingStart);
            HangingPanelPose pose = At(driven, flat);
            if (released) pose = pose.WithWithdrawal();
            PresentedPanel panel = wearer.PresentCloth(pose, (float)(time - prior), batch);
            prior = time;
            WornClothStatus status = wearer.CurrentClothStatus;
            Require(wearer.CurrentClothPose, "current display revision", (float)status.Projection.Status);
            Require(status.Fallback == WornClothFallback.None, "refusal", (float)status.Fallback);
            Require(!status.Solve.Recovered, "fallback recovery", (float)(status.Solve.FallbackStatus ?? ClothStepStatus.Accepted));
            if (reuse)
            {
                ClothMaterialSolveWork work = wearer.CurrentMaterialWork;
                Require(work.Factorizations == status.Substeps, "factorizations", work.Factorizations);
                Require(work.ResidualChecks >= status.Substeps * (ClothWorkLimits.IterationLimit - 1),
                    "residual certificates", work.ResidualChecks);
            }
            Require(status.DisplayWork.BroadPhaseVisits <= ClothWorkLimits.BroadPhaseVisitLimit,
                "display broad-phase work", status.DisplayWork.BroadPhaseVisits);
            Require(status.DisplayWork.NarrowPhaseCandidates <= ClothWorkLimits.NarrowPhaseCandidateLimit,
                "display candidates", status.DisplayWork.NarrowPhaseCandidates);
            bool measured = time >= MeasurementStart;
            if (!measured && (!reuse || trace is null)) return;
            string? visiblePositionsSha256 = reuse && trace is not null
                ? Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(panel.Vertices[..cut.Rest.Length])))
                : null;
            ClothObservation geometry = HangingPanelFixture.Observe(pose, panel);
            var closest = wearer.ClosestLiningWitness(pose.MarkerInModel);
            MaterialObservation material = geometry.Material!.Value;
            float lipClearance = MathF.Min(geometry.Lining.Lip, MathF.Min(geometry.Outer.Lip, geometry.Rim.Lip));
            FlatLipBearingDecision? bearing = measured && !released
                ? gate?.Observe(driven, geometry.IsLipLoaded, geometry.MarkerGap, lipClearance) : null;
            if (bearing is { } observed)
                recoveryPending = observed.Verdict == FlatLipBearingVerdict.Closing;
            ClothObservation proxyWitness = HangingPanelFixture.Observe(pose, wearer.PresentFrozenProxyWitness(pose));
            ClothObservation withoutLift = HangingPanelFixture.Observe(pose, wearer.PresentWithoutOutfitLift());
            float proxyLip = MathF.Min(proxyWitness.Lining.Lip, MathF.Min(proxyWitness.Outer.Lip, proxyWitness.Rim.Lip));
            FlatLipBearingDecision? proxyBearing = measured && !released
                ? proxyGate?.Observe(driven, proxyWitness.IsLipLoaded, proxyWitness.MarkerGap, proxyLip)
                : null;

            if (trace is not null)
            {
                var sample = new
                {
                    time, phase = pose.PhaseSeconds, deadlineSample, geometry, bearing, status, proxyWitness, proxyBearing, withoutLift,
                    proxyRows = geometrySelected ? GeometrySelectedRows : null,
                    staticRegistration = wearer.StaticRegistration, maxGuideLiftError = wearer.MaxGuideLiftError,
                    closest, markedFittedOffset = Components(wearer.MarkedFittedOffset), markedBareOffset = Components(wearer.MarkedBareOffset),
                };
                if (reuse)
                {
                    JsonObject details = JsonSerializer.SerializeToNode(sample)!.AsObject();
                    details["materialWork"] = JsonSerializer.SerializeToNode(wearer.CurrentMaterialWork);
                    details["visiblePositionsSha256"] = visiblePositionsSha256;
                    details["garmentMeshSha256"] = wearer.GarmentMeshDigest;
                    details["garmentDefinitionSha256"] = wearer.GarmentDefinitionDigest;
                    trace.WriteLine(details.ToJsonString());
                }
                else
                    trace.WriteLine(JsonSerializer.Serialize(sample));
            }
            if (!measured) return;
            Require(geometry.IsExterior, "exterior", geometry.MinimumSignedClearance);
            Require(proxyWitness.IsExterior, "intrinsic exterior", proxyWitness.MinimumSignedClearance);
            Require(material.SeamError <= HangingPanelFixture.SeamLimit, "seam", material.SeamError);
            Require(proxyWitness.Material!.Value.SeamError <= HangingPanelFixture.SeamLimit,
                "intrinsic seam", proxyWitness.Material.Value.SeamError);
            if (released)
            {
                if (time == 20)
                {
                    Require(geometry.HasReleasedMarker, "withdrawal gap", geometry.MarkerGap);
                    Require(material.BarePlumbError <= HangingPanelFixture.PlumbLimit, "bare plumb", material.BarePlumbError);
                    Require(proxyWitness.HasReleasedMarker, "intrinsic withdrawal gap", proxyWitness.MarkerGap);
                }
                return;
            }
            Require(flat || geometry.IsLipLoaded, "loaded lip", geometry.MarkerGap);
            if (!geometrySelected)
                Require(proxyBearing?.Acceptable ?? proxyWitness.HasBearing, "intrinsic bearing", proxyWitness.MarkerGap);
            Require(bearing?.Acceptable ?? geometry.HasBearing, "bearing", geometry.MarkerGap);
            if (deadlineSample)
                Require(geometry.HasBearing, "flat deadline bearing", geometry.MarkerGap);
            Require(material.TensileStrain <= HangingPanelFixture.StrainLimit, "strain", material.TensileStrain);
            MaterialObservation intrinsicMaterial = proxyWitness.Material.Value;
            Require(intrinsicMaterial.TensileStrain <= HangingPanelFixture.StrainLimit,
                "intrinsic strain", intrinsicMaterial.TensileStrain);
            Require(material.HemAngleDegrees <= HangingPanelFixture.HemAngleLimit, "tail angle", material.HemAngleDegrees);
            Require(material.BridgeClearance >= HangingPanelFixture.BridgeDaylight, "bridge", material.BridgeClearance);
            if (!flat || pose.PhaseSeconds == 0)
                Require(material.TangentError <= HangingPanelFixture.TangentLimit, "tangent", material.TangentError ?? float.NaN);
            if (flat && time == 16)
                Require(material.LipPlumbError <= HangingPanelFixture.PlumbLimit, "flat plumb", material.LipPlumbError);

            void Require(bool pass, string metric, float value)
            {
                if (!pass) failures.Add((time, metric, value));
            }
        }
    }

    private static float[] Components(Vector3 offset) => [offset.X, offset.Y, offset.Z];

    private static HangingPanelPose At(double seconds, bool flat)
    {
        HangingPanelPose pose = HangingPanelFixture.PoseAt(seconds);
        return flat ? pose.WithFlatLip() : pose;
    }

    private static bool IsFlatLipEligible(double seconds)
    {
        var pose = At(seconds, flat: true);
        Vector3 pin = Vector3.Transform(HangingPanelFixture.Seam, pose.Chest * pose.Wearer);
        Vector3 support = Vector3.Transform(pose.Support.Translation, pose.Wearer);
        return PanelDrape.TryTangent(pin, support, HangingPanelFixture.LipRadius + HangingPanelFixture.HalfThickness, out _);
    }
}
