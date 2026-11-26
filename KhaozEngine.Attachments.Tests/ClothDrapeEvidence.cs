using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Attachments;

namespace KhaozEngine.Tests.Attachments;

/// <summary>Shares the fixed trace between the breathing test and the withdrawal acceptance gate.</summary>
public sealed class ClothDrapeEvidence
{
    private const int Cycle = 4 * 60;
    internal const int FirstMeasured = 2 * Cycle;
    internal const int LastMeasured = 4 * Cycle;
    internal const int LastReleased = 5 * Cycle;
    internal const float StepSeconds = 1f / 120;
    internal static readonly Vector3 Gravity = new(0, -9.81f, 0);
    private readonly Lazy<List<DrapeSample>> _samples = new(() => Measure(layered: false));
    private readonly Lazy<List<DrapeSample>> _layered = new(() => Measure(layered: true));

    internal IReadOnlyList<DrapeSample> Samples => _samples.Value;
    internal IReadOnlyList<DrapeSample> LayeredSamples => _layered.Value;

    private static List<DrapeSample> Measure(bool layered)
    {
        var fixture = new ClothDrapeFixture();
        var samples = new List<DrapeSample>();
        Span<Vector3> pins = stackalloc Vector3[ClothDrapeFixture.Columns];
        Span<ClothSupport> first = stackalloc ClothSupport[2];
        Span<ClothSupport> last = stackalloc ClothSupport[2];
        foreach (bool flat in new[] { false, true })
        {
            FlatLipReentry? reentry = flat ? new FlatLipReentry(IsFlatLipEligible) : null;
            ClothWorkLimits limits = layered
                ? ClothWorkLimits.LayeredContact
                : new ClothWorkLimits(4, 24, 256, 4096, MaxMaterialFactorProducts: 1_675_728);
            var simulation = new ClothSimulation(fixture.Rig, limits);
            simulation.Reset(fixture.Fitted);
            HangingPanelPose opening = At(0, flat);
            int recoveries = 0, checks = 0, candidates = 0, velocityContacts = 0;
            int refinements = 0, velocityChecks = 0, selfChecks = 0;
            long factorProducts = 0;
            ClothStepStatus status = ClothStepStatus.Accepted;
            for (int tick = 0; tick <= LastReleased * 2; tick++)
            {
                bool withdrawn = tick > LastMeasured * 2;
                double time = withdrawn ? 0 : Math.Max(0, tick - Cycle * 2) / 120.0;
                HangingPanelPose pose = At(time, flat);
                if (withdrawn) pose = pose.WithWithdrawal();
                if (tick > 0)
                {
                    fixture.PosePins(pose, pins);
                    ClothDrapeFixture.PoseSupports(opening, first);
                    ClothDrapeFixture.PoseSupports(pose, last);
                    ClothStepResult step = simulation.Advance(new ClothSubstep(pins, first, last, StepSeconds, Gravity));
                    status = step.Status;
                    recoveries += step.Status == ClothStepStatus.Accepted ? 0 : 1;
                    checks = Math.Max(checks, step.ContactChecks);
                    candidates = Math.Max(candidates, step.ContactCandidates);
                    velocityContacts = Math.Max(velocityContacts, step.NormalVelocityContacts);
                    refinements = Math.Max(refinements, step.SweepRefinements);
                    velocityChecks = Math.Max(velocityChecks, step.NormalVelocityChecks);
                    selfChecks = Math.Max(selfChecks, step.SelfContactChecks);
                    factorProducts = Math.Max(factorProducts, step.MaterialFactorProducts);
                }
                opening = pose;

                if (tick >= FirstMeasured * 2 && (!withdrawn || tick % 2 == 0))
                {
                    PresentedPanel panel = fixture.Present(simulation.Positions, pose, recoveries);
                    ClothObservation geometry = HangingPanelFixture.Observe(pose, panel);
                    float lipClearance = MathF.Min(geometry.Lining.Lip, MathF.Min(geometry.Outer.Lip, geometry.Rim.Lip));
                    FlatLipBearingDecision? bearing = withdrawn ? null :
                        reentry?.Observe(time, geometry.IsLipLoaded, geometry.MarkerGap, lipClearance);
                    samples.Add(new DrapeSample(flat, withdrawn, tick, tick / 2, time, pose.PhaseSeconds,
                        geometry, bearing, status, checks, candidates, velocityContacts, factorProducts,
                        refinements, velocityChecks, selfChecks));
                }
            }
        }

        string? tracePath = Environment.GetEnvironmentVariable("KE_CLOTH_DRAPE_TRACE");
        using StreamWriter? trace = string.IsNullOrEmpty(tracePath) ? null :
            File.CreateText(layered ? tracePath + ".layered" : tracePath);
        foreach (DrapeSample sample in samples)
            trace?.WriteLine(JsonSerializer.Serialize(sample));

        return samples;
    }

    private static HangingPanelPose At(double time, bool flat)
    {
        HangingPanelPose pose = HangingPanelFixture.PoseAt(time);
        return flat ? pose.WithFlatLip() : pose;
    }

    private static bool IsFlatLipEligible(double seconds)
    {
        HangingPanelPose pose = At(seconds, flat: true);
        Vector3 pin = Vector3.Transform(HangingPanelFixture.Seam, pose.Chest * pose.Wearer);
        Vector3 support = Vector3.Transform(pose.Support.Translation, pose.Wearer);
        float radius = HangingPanelFixture.LipRadius + HangingPanelFixture.HalfThickness;
        return PanelDrape.TryTangent(pin, support, radius, out _);
    }

    internal sealed record DrapeSample(bool Flat, bool Withdrawn, int Tick, int Frame, double DrivenSeconds,
        double PhaseSeconds, ClothObservation Geometry, FlatLipBearingDecision? Bearing, ClothStepStatus Status,
        int PeakContactChecks, int PeakContactCandidates, int PeakNormalVelocityContacts, long PeakMaterialFactorProducts,
        int PeakSweepRefinements, int PeakNormalVelocityChecks, int PeakSelfContactChecks)
    {
        public bool PresentationSample => Tick % 2 == 0;
        public float RawBearingExcess => MathF.Max(0, Geometry.MarkerGap - HangingPanelFixture.BearingLimit);
    }
}
