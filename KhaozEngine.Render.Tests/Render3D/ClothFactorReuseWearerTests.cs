using System;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class ClothFactorReuseWearerTests(ITestOutputHelper output)
{
    private const int WarmFrames = 12;
    private const int ObservedFrames = 12;
    private const int MaxProductsPerFactor = 418_932;

    [Theory]
    [InlineData(144)]
    [InlineData(60)]
    public void Prepared_original_cut_reuses_one_factor_per_tick_without_warm_allocation(int rate)
    {
        var cut = new HangingPanelCut();
        using var wearer = new HangingPanelDrawRig(cut, HangingPanelFixture.PoseAt(0), new ClothDrapeFixture(),
            useMaterialMapping: true, reuseMaterialFactor: true);
        var surface = wearer.PreparationCostSurface;
        var material = Assert.Single(surface.Materials);
        Assert.True(surface.HasMaterialMapping);
        Assert.Equal(117, surface.Rig.RestPositions.Length);
        Assert.Equal(192, surface.Rig.Triangles.Length);
        Assert.Equal(1_816, material.VisibleIndices.Length / 3);
        Assert.Equal(1_018, material.Vertices.Length);

        float seconds = 1f / rate;
        wearer.PrepareForCost(Matrix4x4.Identity, 0, true, out _, out _);
        for (int frame = 1; frame <= WarmFrames; frame++)
        {
            var pose = At(frame);
            wearer.PoseForPreparationCost(pose);
            wearer.PrepareForCost(pose.Wearer, seconds, true, out _, out _);
        }

        long bytes = 0;
        int fixedSteps = 0;
        ClothMaterialSolveWork materialWork = default;
        int solveBroadPhaseVisits = 0, solveNarrowPhaseCandidates = 0;
        int displayBroadPhaseVisits = 0, detailedBroadPhaseVisits = 0;
        Vector3 firstPoint = default;
        bool moved = false;
        ulong? history = null;
        for (int frame = WarmFrames + 1; frame <= WarmFrames + ObservedFrames; frame++)
        {
            var pose = At(frame);
            wearer.PoseForPreparationCost(pose);
            HangingPanelDrawRig.PreparationDraws draws;
            WornClothPresentation shown;
            ReadOnlyMemory<ModelVertex> detail;
            using (AllocationRegion.Start("warm factor-reuse cloth preparation"))
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                draws = wearer.PrepareForCost(pose.Wearer, seconds, true, out shown, out detail);
                bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Assert.Equal((1, 1), (draws.LipCount, draws.GarmentCount));
            Assert.True(draws.Lip.IsSkinned && draws.Garment.IsSkinned);
            Assert.Equal(pose.Wearer, draws.Garment.Skinned.Model);
            Assert.True(draws.HasCloth && shown.IsCurrentPose);
            Assert.Equal(pose.Wearer, shown.Model);
            Assert.Same(surface, shown.Surface);
            Assert.Equal(surface.Rig.RestPositions.Length, shown.Nodes.Length);
            Assert.Equal(material.Vertices.Length, detail.Length);
            history ??= shown.HistoryRevision;
            Assert.Equal(history.Value, shown.HistoryRevision);

            var status = wearer.CurrentClothStatus;
            Assert.Equal(WornClothTier.Cloth, status.Tier);
            Assert.False(status.IsBlending);
            Assert.Equal(WornClothFallback.None, status.Fallback);
            Assert.Equal(ClothStepStatus.Accepted, status.Projection.Status);
            Assert.Equal(ClothStepStatus.Accepted, status.DetailedProjection.Status);
            Assert.False(status.Solve.Recovered);
            Assert.Equal((2, 0, 0), (status.RigidSupports, status.LowerSegments, status.Hulls));
            Assert.Equal(Ticks(frame) - Ticks(frame - 1), status.Substeps);
            Assert.InRange(status.MaterialFactorProducts, status.Substeps == 0 ? 0 : 1,
                status.Substeps * (long)MaxProductsPerFactor);
            ClothMaterialSolveWork work = wearer.CurrentMaterialWork;
            Assert.Equal(status.MaterialFactorProducts, work.FactorProducts);
            Assert.Equal(status.Substeps, work.Factorizations);
            Assert.True(work.ResidualChecks >= status.Substeps * (ClothWorkLimits.IterationLimit - 1));
            Assert.True(double.IsFinite(work.MaxAcceptedResidualRatio));
            Assert.InRange(work.MaxAcceptedResidualRatio, 0, 1);
            if (status.Substeps == 0) Assert.Equal(default, work);
            fixedSteps += status.Substeps;
            materialWork = materialWork.Combine(work);
            solveBroadPhaseVisits += status.SolveWork.BroadPhaseVisits;
            solveNarrowPhaseCandidates += status.SolveWork.NarrowPhaseCandidates;
            displayBroadPhaseVisits += status.Projection.ContactChecks;
            detailedBroadPhaseVisits += status.DetailedProjection.ContactChecks;

            var patch = detail.Span[cut.Probes.MarkedPatch];
            Assert.True(float.IsFinite(patch.Position.LengthSquared()));
            Assert.InRange(patch.Normal.LengthSquared(), .99f, 1.01f);
            if (frame == WarmFrames + 1) firstPoint = patch.Position;
            moved |= firstPoint != patch.Position;
        }

        Assert.Equal(0, bytes);
        Assert.True(moved);
        Assert.True(solveBroadPhaseVisits > 0 && solveNarrowPhaseCandidates > 0);
        Assert.True(displayBroadPhaseVisits > 0 && detailedBroadPhaseVisits > 0);
        Assert.True(materialWork.CorrectionIterations > 0 && materialWork.OperatorProducts > 0 &&
            materialWork.SubstitutionProducts > 0 && materialWork.VectorProducts > 0);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            rate, observedFrames = ObservedFrames, fixedSteps, callerBytes = bytes, materialWork,
            solveBroadPhaseVisits, solveNarrowPhaseCandidates, displayBroadPhaseVisits, detailedBroadPhaseVisits,
            garmentMeshSha256 = wearer.GarmentMeshDigest, garmentDefinitionSha256 = wearer.GarmentDefinitionDigest,
        }));

        HangingPanelPose At(int frame) => HangingPanelFixture.PoseAt(frame / (double)rate);
        int Ticks(int frame) => (int)(frame * (double)seconds / ClothWorkLimits.LayeredStepSeconds);
    }
}
