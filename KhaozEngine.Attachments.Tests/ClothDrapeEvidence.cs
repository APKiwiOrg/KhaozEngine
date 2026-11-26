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
    private const int FirstMeasured = 2 * Cycle;
    internal const int LastMeasured = 4 * Cycle;
    internal const int LastReleased = 5 * Cycle;
    internal const float StepSeconds = 1f / 120;
    internal static readonly Vector3 Gravity = new(0, -9.81f, 0);
    private readonly Lazy<List<DrapeSample>> _samples = new(Measure);

    internal IReadOnlyList<DrapeSample> Samples => _samples.Value;

    private static List<DrapeSample> Measure()
    {
        var fixture = new ClothDrapeFixture();
        var samples = new List<DrapeSample>();
        Span<Vector3> pins = stackalloc Vector3[ClothDrapeFixture.Columns];
        Span<ClothSupport> first = stackalloc ClothSupport[2];
        Span<ClothSupport> last = stackalloc ClothSupport[2];
        foreach (bool flat in new[] { false, true })
        {
            var limits = new ClothWorkLimits(4, 24, 256, 4096, MaxMaterialFactorProducts: 1_675_728);
            var simulation = new ClothSimulation(fixture.Rig, limits);
            simulation.Reset(fixture.Fitted);
            HangingPanelPose opening = At(0, flat);
            int recoveries = 0, checks = 0, candidates = 0;
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
                    factorProducts = Math.Max(factorProducts, step.MaterialFactorProducts);
                }
                opening = pose;

                if (tick % 2 == 0 && tick >= FirstMeasured * 2)
                {
                    PresentedPanel panel = fixture.Present(simulation.Positions, pose, recoveries);
                    ClothObservation geometry = HangingPanelFixture.Observe(pose, panel);
                    samples.Add(new DrapeSample(flat, withdrawn, tick / 2, pose.PhaseSeconds,
                        geometry, status, checks, candidates, factorProducts));
                }
            }
        }

        string? tracePath = Environment.GetEnvironmentVariable("KE_CLOTH_DRAPE_TRACE");
        using StreamWriter? trace = string.IsNullOrEmpty(tracePath) ? null : File.CreateText(tracePath);
        foreach (DrapeSample sample in samples)
            trace?.WriteLine(JsonSerializer.Serialize(sample));

        return samples;
    }

    private static HangingPanelPose At(double time, bool flat)
    {
        HangingPanelPose pose = HangingPanelFixture.PoseAt(time);
        return flat ? pose.WithFlatLip() : pose;
    }

    internal sealed record DrapeSample(bool Flat, bool Withdrawn, int Frame, double PhaseSeconds,
        ClothObservation Geometry, ClothStepStatus Status, int PeakContactChecks, int PeakContactCandidates,
        long PeakMaterialFactorProducts);
}
