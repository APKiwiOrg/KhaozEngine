using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
[Trait("Category", "Diagnostic")]
public sealed class ClothPreparationCostDiagnostics(ITestOutputHelper output)
{
    private const int SettleFrames = 1_200;
    private const int WarmFrames = 120;
    private const int MeasuredFrames = 600;
    private const int SmokeFrames = 12;
    private const int FactorsPerSubstep = 1_675_728;

    [ClothPreparationFact]
    public void Report_close_cloth_preparation_cost()
    {
        bool smoke = ClothPreparationAdmission.IsSmoke;
        string? workloadSha = smoke ? null : ClothPreparationAdmission.VerifyHostedCheckout();
        int settle = smoke ? 0 : SettleFrames;
        int warm = smoke ? SmokeFrames : WarmFrames;
        int measured = smoke ? SmokeFrames : MeasuredFrames;
        var cut = new HangingPanelCut();
        var proxy = new ClothDrapeFixture();
        Row[] rows =
        [
            Sample(cut, proxy, 144, true, settle, warm, measured, smoke),
            Sample(cut, proxy, 144, false, settle, warm, measured, smoke),
            Sample(cut, proxy, 60, true, settle, warm, measured, smoke),
            Sample(cut, proxy, 60, false, settle, warm, measured, smoke),
        ];
        output.WriteLine(JsonSerializer.Serialize(new
        {
            diagnostic = nameof(ClothPreparationCostDiagnostics),
            workloadSha,
            mode = smoke ? "untimed-smoke" : "hosted-timed",
            subject = "One raised hanging panel, original proxy, same-GLB material mapping and two rigid capsules",
            supportInventory = new[] { "raised transverse lip", "recessed body" },
            wearerCount = 1,
            preparationThreads = 1,
            ordinaryWornDrawsPerFrame = 2,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            logicalProcessors = Environment.ProcessorCount,
            settleFrames = settle,
            warmFrames = warm,
            observedFrames = measured,
            rows,
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static Row Sample(HangingPanelCut cut, ClothDrapeFixture proxy, int rate, bool cloth,
        int settle, int warm, int measured, bool smoke)
    {
        using var wearer = new HangingPanelDrawRig(cut, HangingPanelFixture.PoseAt(0), proxy, useMaterialMapping: true);
        BoundClothSurface surface = wearer.PreparationCostSurface;
        Assert.True(surface.HasMaterialMapping);
        BoundClothMaterial material = Assert.Single(surface.Materials);
        int nodeCount = surface.Rig.RestPositions.Length;
        int proxyFaces = surface.Rig.Triangles.Length;
        int visibleFaces = material.VisibleIndices.Length / 3;
        int vertexCount = material.Vertices.Length;
        Assert.Equal(117, nodeCount);
        Assert.Equal(192, proxyFaces);
        Assert.Equal(1_816, visibleFaces);
        Assert.Equal(1_018, vertexCount);

        float seconds = 1f / rate;
        int primingFrames = settle + warm;
        int lastFrame = primingFrames + measured;
        wearer.PrepareForCost(Matrix4x4.Identity, 0, cloth, out _, out _);
        for (int frame = 1; frame <= primingFrames; frame++)
        {
            HangingPanelPose pose = Pose(frame);
            wearer.PoseForPreparationCost(pose);
            wearer.PrepareForCost(pose.Wearer, seconds, cloth, out _, out _);
        }

        var observations = new Observations();
        long[]? samples = smoke ? null : new long[measured];
        ulong? historyRevision = null;
        for (int frame = primingFrames + 1; frame <= lastFrame; frame++)
        {
            HangingPanelPose pose = Pose(frame);
            wearer.PoseForPreparationCost(pose);
            HangingPanelDrawRig.PreparationDraws descriptors;
            WornClothPresentation shown;
            ReadOnlyMemory<ModelVertex> detail;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            if (samples is null)
                descriptors = wearer.PrepareForCost(pose.Wearer, seconds, cloth, out shown, out detail);
            else
            {
                long start = Stopwatch.GetTimestamp();
                descriptors = wearer.PrepareForCost(pose.Wearer, seconds, cloth, out shown, out detail);
                samples[frame - primingFrames - 1] = Stopwatch.GetTimestamp() - start;
            }
            observations.CallerBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            WornClothStatus status = wearer.CurrentClothStatus;
            observations.Add(status);
            Assert.Equal(1, descriptors.LipCount);
            Assert.Equal(1, descriptors.GarmentCount);
            Assert.True(descriptors.Lip.IsSkinned && descriptors.Garment.IsSkinned);
            Assert.Equal(pose.Wearer, descriptors.Garment.Skinned.Model);
            if (cloth)
            {
                if (!descriptors.HasCloth || !shown.IsCurrentPose || status.Tier != WornClothTier.Cloth || status.IsBlending ||
                    status.Fallback != WornClothFallback.None || status.Projection.Status != ClothStepStatus.Accepted ||
                    status.DetailedProjection.Status != ClothStepStatus.Accepted)
                    Assert.Fail($"Missing active detailed cloth at {rate} Hz frame {frame}. {status}");
                Assert.Equal(pose.Wearer, shown.Model);
                Assert.Same(surface, shown.Surface);
                historyRevision ??= shown.HistoryRevision;
                Assert.Equal(historyRevision.Value, shown.HistoryRevision);
                Assert.Equal(nodeCount, shown.Nodes.Length);
                Assert.Equal(vertexCount, detail.Length);
                Assert.Equal((2, 0, 0), (status.RigidSupports, status.LowerSegments, status.Hulls));
                Assert.Equal(ExpectedSteps(frame) - ExpectedSteps(frame - 1), status.Substeps);
                Assert.Equal(status.Substeps * (long)FactorsPerSubstep, status.MaterialFactorProducts);
                observations.AddDetail(detail.Span[cut.Probes.MarkedPatch]);
            }
            else
            {
                Assert.Equal(WornClothTier.Simulated, status.Tier);
                Assert.False(descriptors.HasCloth);
                Assert.True(detail.IsEmpty);
                Assert.Equal(0, status.MaterialFactorProducts);
            }
        }

        Assert.Equal(0, observations.Refusals);
        Assert.Equal(0, observations.RecoveryAttempts);
        if (cloth)
        {
            Assert.Equal(measured, observations.DetailedFrames);
            Assert.True(observations.ChangedDetailedFrames > 0);
            Assert.True(observations.SolveBroadPhaseVisits > 0);
            Assert.True(observations.SolveNarrowPhaseCandidates > 0);
        }

        return new(rate, cloth ? "cloth" : "simulated-chain-fallback", wearer.GarmentMeshDigest,
            wearer.GarmentDefinitionDigest, wearer.PreparationCostHostFaces, wearer.PreparationCostLipFaces,
            nodeCount, proxyFaces, visibleFaces, cloth ? vertexCount : 0, observations,
            ClothPreparationStatistics.Summarize(samples, samples is null ? 0 : Stopwatch.Frequency));

        HangingPanelPose Pose(int frame) => HangingPanelFixture.PoseAt(Math.Max(0, frame / (double)rate - (smoke ? 0 : 4)));
        int ExpectedSteps(int frame) => (int)(frame * (double)seconds / ClothWorkLimits.LayeredStepSeconds);
    }

    private sealed record Row(int RateHz, string Tier, string GarmentMeshSha256, string GarmentDefinitionSha256,
        int BodyFitFaces, int LipDrawFaces, int ProxyNodes, int ProxyFaces, int VisibleGarmentFaces,
        int DetailedVerticesPerFrame, Observations Work, ClothPreparationStatistics Timing);

    private sealed class Observations
    {
        private Vector3 previousDetail;

        public long CallerBytes { get; set; }
        public int FixedSubsteps { get; private set; }
        public long MaterialFactorProducts { get; private set; }
        public long SolveBroadPhaseVisits { get; private set; }
        public long SolveNarrowPhaseCandidates { get; private set; }
        public long SolveSweepRefinements { get; private set; }
        public long DisplayContactChecks { get; private set; }
        public long DetailedContactChecks { get; private set; }
        public int RigidSupports { get; private set; }
        public int LowerSegments { get; private set; }
        public int Hulls { get; private set; }
        public int DetailedFrames { get; private set; }
        public int ChangedDetailedFrames { get; private set; }
        public int Refusals { get; private set; }
        public int RecoveryAttempts { get; private set; }

        internal void Add(in WornClothStatus status)
        {
            FixedSubsteps += status.Substeps;
            MaterialFactorProducts += status.MaterialFactorProducts;
            SolveBroadPhaseVisits += status.SolveWork.BroadPhaseVisits;
            SolveNarrowPhaseCandidates += status.SolveWork.NarrowPhaseCandidates;
            SolveSweepRefinements += status.SolveWork.SweepRefinements;
            DisplayContactChecks += status.Projection.ContactChecks;
            DetailedContactChecks += status.DetailedProjection.ContactChecks;
            RigidSupports = Math.Max(RigidSupports, status.RigidSupports);
            LowerSegments = Math.Max(LowerSegments, status.LowerSegments);
            Hulls = Math.Max(Hulls, status.Hulls);
            if (status.Fallback != WornClothFallback.None) Refusals++;
            if (status.Solve.FallbackStatus.HasValue || status.Projection.FallbackStatus.HasValue ||
                status.DetailedProjection.FallbackStatus.HasValue) RecoveryAttempts++;
        }

        internal void AddDetail(in ModelVertex vertex)
        {
            Assert.True(float.IsFinite(vertex.Position.LengthSquared()));
            Assert.InRange(vertex.Normal.LengthSquared(), .99f, 1.01f);
            if (DetailedFrames > 0 && vertex.Position != previousDetail) ChangedDetailedFrames++;
            previousDetail = vertex.Position;
            DetailedFrames++;
        }
    }
}
