using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class HangingClothWearerTests
{
    private static readonly int[] GeometrySelectedRows = [0, 3, 7, 8, 14, 21, 28, 35, 36, 43, 50, 57, 64];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frozen_original_cut_meets_all_detailed_display_limits(bool flat) => Measure(flat, geometrySelected: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Geometry_selected_proxy_meets_the_same_original_cut_limits(bool flat) => Measure(flat, geometrySelected: true);

    private static void Measure(bool flat, bool geometrySelected)
    {
        var neutral = At(0, flat);
        var cut = new HangingPanelCut();
        var proxy = new ClothDrapeFixture(geometrySelected ? GeometrySelectedRows : null);
        using var wearer = new HangingPanelDrawRig(cut, neutral, proxy, matchGuideLift: geometrySelected);
        using (var original = new HangingPanelDrawRig(cut, neutral))
            wearer.VerifyOriginalFallback(original, neutral);
        var gate = flat ? new FlatLipReentry(IsFlatLipEligible) : null;
        var proxyGate = flat ? new FlatLipReentry(IsFlatLipEligible) : null;
        var failures = new List<string>();
        string? tracePath = Environment.GetEnvironmentVariable("KE_CLOTH_WEARER_TRACE");
        using StreamWriter? trace = string.IsNullOrEmpty(tracePath) ? null : File.CreateText(tracePath + (flat ? ".flat" : ".raised"));
        double prior = 0;
        for (int frame = 0; frame <= 20 * 60; frame++)
        {
            double frameTime = frame / 60.0;
            bool released = frame > 16 * 60;
            // Keep the 60 Hz presentations and measure the same visible pose just before the flat deadline.
            // 1.6754 s is off the 120 Hz grid and before the frozen 1.67578353 s deadline.
            if (flat && frame is 581 or 821)
                Present(frameTime - (101 / 60.0 - 1.6754), released);
            Present(frameTime, released);
        }
        Assert.True(failures.Count == 0, $"{failures.Count} failures. {string.Join(", ", failures.GetRange(0, Math.Min(failures.Count, 15)))}");

        void Present(double time, bool released)
        {
            double driven = released ? 0 : Math.Max(0, time - 4);
            HangingPanelPose pose = At(driven, flat);
            if (released) pose = pose.WithWithdrawal();
            PresentedPanel panel = wearer.PresentCloth(pose, (float)(time - prior), batch: flat);
            prior = time;
            if (time < 8) return;
            ClothObservation geometry = HangingPanelFixture.Observe(pose, panel);
            var closest = wearer.ClosestLiningWitness(pose.MarkerInModel);
            MaterialObservation material = geometry.Material!.Value;
            float lipClearance = MathF.Min(geometry.Lining.Lip, MathF.Min(geometry.Outer.Lip, geometry.Rim.Lip));
            FlatLipBearingDecision? bearing = !released ? gate?.Observe(driven, geometry.IsLipLoaded, geometry.MarkerGap, lipClearance) : null;
            WornClothStatus status = wearer.CurrentClothStatus;
            ClothObservation proxyWitness = HangingPanelFixture.Observe(pose, wearer.PresentFrozenProxyWitness(pose));
            ClothObservation withoutLift = HangingPanelFixture.Observe(pose, wearer.PresentWithoutOutfitLift());
            float proxyLip = MathF.Min(proxyWitness.Lining.Lip, MathF.Min(proxyWitness.Outer.Lip, proxyWitness.Rim.Lip));
            FlatLipBearingDecision? proxyBearing = !released
                ? proxyGate?.Observe(driven, proxyWitness.IsLipLoaded, proxyWitness.MarkerGap, proxyLip)
                : null;

            trace?.WriteLine(JsonSerializer.Serialize(new
            {
                time, phase = pose.PhaseSeconds, geometry, bearing, status, proxyWitness, proxyBearing, withoutLift,
                proxyRows = geometrySelected ? GeometrySelectedRows : null,
                staticRegistration = wearer.StaticRegistration, maxGuideLiftError = wearer.MaxGuideLiftError,
                closest, markedFittedOffset = Components(wearer.MarkedFittedOffset), markedBareOffset = Components(wearer.MarkedBareOffset),
            }));
            Require(wearer.CurrentClothPose, "current display revision", (float)status.Projection.Status);
            Require(status.Fallback == WornClothFallback.None, "refusal", (float)status.Fallback);
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
                if (!pass) failures.Add($"{time:R}s {metric} {value:R}");
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
