using System;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class HangingClothWearerHandoffTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Detailed_cut_remains_exterior_through_promotion_reversal_and_fitted_rest(bool flat, bool refuseFirst,
        bool reuseMaterialFactor)
    {
        // #1870: verify the lining, outer skin and rim returned by the wearer during a real handoff.
        const float tick = 1f / 120, blend = 1f / 12;
        var cut = new HangingPanelCut();
        double time = 0;
        var pose = At(time);
        bool inspectedHandoff = false;
        WornClothStatus peak = default;
        double peakPhase = 0;
        int peakProxyNarrow = 0, peakDetailedNarrow = 0, peakCombinedNarrow = 0;
        using var rig = new HangingPanelDrawRig(cut, pose, new ClothDrapeFixture(),
            useMaterialMapping: true, clothBlendSeconds: blend, reuseMaterialFactor: reuseMaterialFactor);

        MotionLevel incoming = flat ? MotionLevel.Simulated : MotionLevel.Held;
        var cheap = Step(0, incoming, false);
        for (int i = 0; i < 6; i++) cheap = Step(tick, incoming, false);
        Assert.False(cheap.HasCloth);
        output.WriteLine($"Before promotion at {time:R} s: visible signed clearance {cheap.Geometry.MinimumSignedClearance:R} m, " +
            $"crossings {cheap.Geometry.AnalyticSupportCrossings}, contained triangles {cheap.Geometry.ContainedTriangles}.");
        if (flat)
            Assert.True(Distance(cheap.Positions, rig.AuthoredVisiblePositions(pose.Wearer)) > ClothSurfaceFile.RoundTripTolerance);

        var first = Step(0, incoming, true);
        Assert.True(first.HasCloth);
        Assert.InRange(Distance(cheap.Positions, first.Positions), 0, ClothSurfaceFile.RoundTripTolerance);
        var live = first;
        for (int i = 0; i < 16; i++) live = Step(tick, MotionLevel.Simulated, true);
        Assert.Equal(WornClothTier.Cloth, live.Status.Tier);
        Assert.True(Distance(cheap.Positions, live.Positions) > ClothSurfaceFile.RoundTripTolerance);

        if (refuseFirst)
        {
            // A support through the sewn edge makes the requested handoff impossible at this pose.
            Vector3 clasp = Vector3.Transform(HangingPanelFixture.Seam, pose.Chest);
            var blockedPose = pose with { Support = Matrix4x4.CreateTranslation(clasp) };
            var blocked = rig.StepHandoff(blockedPose, 0, MotionLevel.Held, request: false);
            Assert.NotEqual(WornClothFallback.None, blocked.Status.Fallback);
            Assert.True(blocked.HasCloth);
            Assert.False(blocked.IsCurrent);
            Assert.Equal(live.Revision, blocked.Revision);
            Assert.Equal(live.Positions, blocked.Positions);
            live = Step(0, MotionLevel.Simulated, true);
        }

        var outgoing = Step(0, MotionLevel.Held, false);
        Assert.InRange(Distance(live.Positions, outgoing.Positions), 0, ClothSurfaceFile.RoundTripTolerance);
        for (int i = 0; i < 4; i++) outgoing = Step(tick, MotionLevel.Held, false);
        Assert.True(outgoing.Status.IsBlending);
        var reversed = Step(0, MotionLevel.Held, true);
        Assert.InRange(Distance(outgoing.Positions, reversed.Positions), 0, ClothSurfaceFile.RoundTripTolerance);

        for (int i = 0; i < 12; i++) live = Step(tick, MotionLevel.Held, true);
        Assert.Equal(WornClothTier.Cloth, live.Status.Tier);
        using var reference = new HangingPanelDrawRig(cut, At(0));
        ClothObservation target = HangingPanelFixture.Observe(pose, reference.Present(pose, cut, 0, MotionLevel.Held));
        Assert.True(target.IsExterior);
        output.WriteLine($"Independent final Held target at {time:R} s: minimum {target.MinimumSignedClearance:R} m, " +
            $"crossings {target.AnalyticSupportCrossings}, contained {target.ContainedTriangles}.");
        var demotionStart = Step(0, MotionLevel.Held, false);
        var preceding = demotionStart;
        float maxTickTravel = 0;
        // A stationary final pose isolates the tier switch from actual body motion.
        for (int i = 0; i < 9; i++)
        {
            var frame = Step(tick, MotionLevel.Held, false, move: false);
            maxTickTravel = MathF.Max(maxTickTravel, Distance(preceding.Positions, frame.Positions));
            preceding = frame;
        }
        var last = Step(tick - .00001f, MotionLevel.Held, false, move: false);
        var released = Step(.00002f, MotionLevel.Held, false, move: false);
        maxTickTravel = MathF.Max(maxTickTravel, Distance(preceding.Positions, last.Positions));
        float travel = Distance(demotionStart.Positions, released.Positions);
        float endpoint = Distance(last.Positions, released.Positions);
        Assert.True(last.HasCloth);
        Assert.False(released.HasCloth);
        Assert.Equal(WornClothTier.Held, released.Status.Tier);
        output.WriteLine($"Peak display at phase {peakPhase:R} s: {peak.DisplayWork.BroadPhaseVisits} broad visits " +
            $"({peak.Projection.ContactChecks} proxy, {peak.Projection.SelfContactChecks} self/shared within proxy, " +
            $"{peak.DetailedProjection.ContactChecks} detailed). Peak narrow candidates over the transfer: " +
            $"{peakCombinedNarrow} combined, {peakProxyNarrow} proxy, {peakDetailedNarrow} detailed.");
        output.WriteLine($"Fitted return: {travel:R} m total, {maxTickTravel:R} m peak per tick, " +
            $"{endpoint:R} m across the final boundary.");
        Assert.InRange(endpoint, 0, ClothSurfaceFile.RoundTripTolerance);
        Assert.InRange(maxTickTravel, 0, travel * (tick / blend) + ClothSurfaceFile.RoundTripTolerance);
        Assert.True(inspectedHandoff);

        HangingPanelPose At(double seconds)
        {
            HangingPanelPose current = HangingPanelFixture.PoseAt(seconds);
            return flat ? current.WithFlatLip() : current;
        }

        HangingPanelDrawRig.DetailedFrame Step(float seconds, MotionLevel level, bool request, bool move = true)
        {
            if (move) time += seconds;
            pose = At(time);
            var sample = rig.StepHandoff(pose, seconds, level, request);
            Assert.True(sample.IsCurrent,
                $"Stale detailed surface at {time:R} s, request {request}, {level}, {sample.Status}, visible clearance {sample.Geometry.MinimumSignedClearance:R} m.");
            Assert.True(sample.Status.Fallback == WornClothFallback.None,
                $"At {time:R} s, request {request}, {level}, status {sample.Status}, visible clearance {sample.Geometry.MinimumSignedClearance:R} m.");
            if (reuseMaterialFactor)
                Assert.Equal(sample.Status.Substeps, rig.CurrentMaterialWork.Factorizations);
            inspectedHandoff |= sample.HasCloth && sample.Status.IsBlending && sample.Status.DetailedProjection.ContactChecks > 0;
            if (sample.Status.DisplayWork.BroadPhaseVisits > peak.DisplayWork.BroadPhaseVisits)
            {
                peak = sample.Status;
                peakPhase = pose.PhaseSeconds;
            }
            peakCombinedNarrow = Math.Max(peakCombinedNarrow, sample.Status.DisplayWork.NarrowPhaseCandidates);
            peakProxyNarrow = Math.Max(peakProxyNarrow, sample.Status.Projection.ContactCandidates);
            peakDetailedNarrow = Math.Max(peakDetailedNarrow, sample.Status.DetailedProjection.ContactCandidates);
            Assert.True(sample.Geometry.IsExterior, $"Minimum visible clearance {sample.Geometry.MinimumSignedClearance:R} m.");
            Assert.InRange(sample.Geometry.Material!.Value.SeamError, 0, HangingPanelFixture.SeamLimit);
            Assert.InRange(sample.Status.DisplayWork.BroadPhaseVisits, 0, ClothWorkLimits.BroadPhaseVisitLimit);
            Assert.InRange(sample.Status.DisplayWork.NarrowPhaseCandidates, 0, ClothWorkLimits.NarrowPhaseCandidateLimit);
            return sample;
        }
    }

    private static float Distance(Vector3[] first, Vector3[] second)
    {
        Assert.NotEmpty(first);
        Assert.Equal(first.Length, second.Length);
        float maximum = 0;
        for (int i = 0; i < first.Length; i++) maximum = MathF.Max(maximum, Vector3.Distance(first[i], second[i]));
        return maximum;
    }
}
