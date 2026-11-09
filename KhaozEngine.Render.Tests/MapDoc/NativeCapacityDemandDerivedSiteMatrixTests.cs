using System;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Accepted and refused rows for the native decode, build and retention views' originating fields.</summary>
[Collection("AllocSensitive")]
public sealed class NativeCapacityDemandDerivedSiteMatrixTests
{
    static (decimal Current, decimal Increment, decimal Allowance, bool Refused) Of(object row) =>
        (Read<decimal>(row, "CurrentDemand"), Read<decimal>(row, "ProposedIncrement"), Read<decimal>(row, "Allowance"), Read<bool>(row, "Refused"));

    static MapRenderDecodeAllowance Decode(long declared)
    {
        var scene = new MapSceneAdmission(new(MaxPreparedBytes: 1_000_000));
        Assert.True(scene.TryReservePreparation(1000, 0));
        var budgets = new MapRenderBudgets(MaxResidentCpuBytes: 2_000_000);
        var residency = new MapRenderAdmission(budgets);
        Assert.NotNull(residency.TryReserveCpu(3000));
        return new(scene, residency, budgets, new(MaxPeakBytes: declared));
    }

    [Fact]
    public void DecodeView_ReservationAcceptsAndRefusesPerOrigin()
    {
        using var probe = new CapacityDemandProbe();
        var budget = Decode(100).Budget;
        Assert.True(budget.TryReserve(10, 0));
        Assert.False(budget.TryReserve(95, 0));
        var rows = probe.Events.Where(r => Read<string>(r, "Site") == "GltfDecodeBudget.MaxPeakBytes.1").ToArray();
        Assert.Equal(new[] { "MaxPreparedBytes", "MaxResidentCpuBytes", "MaxPeakBytes", "MaxPreparedBytes", "MaxResidentCpuBytes", "MaxPeakBytes" },
            rows.Select(r => Read<string>(r, "LimitName")));
        Assert.Equal((1000m, 10m, 1_000_000m, false), Of(rows[0]));
        Assert.Equal((3000m, 10m, 2_000_000m, false), Of(rows[1]));
        Assert.Equal((0m, 10m, 100m, false), Of(rows[2]));
        Assert.Equal((1010m, 95m, 1_000_000m, false), Of(rows[3]));
        Assert.Equal((3010m, 95m, 2_000_000m, false), Of(rows[4]));
        Assert.Equal((10m, 95m, 100m, true), Of(rows[5]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodeView_MetadataAndImageGatesPublishEveryOrigin(bool refused)
    {
        using var probe = new CapacityDemandProbe();
        var budget = Decode(refused ? 100 : 900_000).Budget;
        Assert.Equal(!refused, GltfDecodeAdmission.Measure(NativeRenderAssetFixtures.Glb("instanced"), budget).Allowed);
        var metadata = probe.Events.Where(r => Read<string>(r, "Site") == "GltfDecodeAdmission.MaxPeakBytes.1").ToArray();
        Assert.Equal(new decimal[] { 1000, 3000, 0 }, metadata.Select(r => Read<decimal>(r, "CurrentDemand")));
        Assert.Equal(new[] { false, false, refused }, metadata.Select(r => Read<bool>(r, "Refused")));
        Assert.Single(metadata.Select(r => Read<decimal>(r, "ProposedIncrement")).Distinct());
        using var imageProbe = new CapacityDemandProbe();
        var image = Decode(refused ? 100 : 900_000).Budget;
        Action decode = () => ImageRgba.DecodeBounded(NativeRenderAssetFixtures.Png(2, 3), image.Limits.MaxPeakBytes, 100, image.Limits);
        if (refused) Assert.Throws<ImageDecodeRefusal>(decode); else decode();
        var peak = imageProbe.Events.Where(r => Read<string>(r, "Site") == "ImageRgba.MaxPeakBytes.1").ToArray();
        Assert.Equal(new[] { "MaxPreparedBytes", "MaxResidentCpuBytes", "MaxPeakBytes" }, peak.Select(r => Read<string>(r, "LimitName")));
        Assert.Equal(new decimal[] { 1000, 3000, 0 }, peak.Select(r => Read<decimal>(r, "CurrentDemand")));
        Assert.Equal(new decimal[] { 1_000_000, 2_000_000, refused ? 100 : 900_000 }, peak.Select(r => Read<decimal>(r, "Allowance")));
        Assert.Equal(new[] { false, false, refused }, peak.Select(r => Read<bool>(r, "Refused")));
    }

    [Fact]
    public void BuildReservation_RefusesAtTheQueueOriginWhileTheSceneOriginAccepts()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.StateWithLimits(new(MaxPreparedBytes: 134_217_728) { Preparation = new(FoliageContext: new()) }, ResidencyFixture.Window());
        using var probe = new CapacityDemandProbe();
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), new(MaxQueuedBytes: 8192, MaxPlanBytes: 67_108_864), scheduler);
        for (int i = 0; i < 8; i++) { residency.Update(); scheduler.RunAll(); }
        var transition = residency.Update().Transition;
        Assert.Equal(MapSceneStatus.CapacityExceeded, transition.Status);
        // The public refusal names the queue's ceiling and the build's requested root total.
        var capacity = transition.Capacity!;
        Assert.Equal(("MaxQueuedBytes", 8192m), (capacity.LimitName, capacity.Allowance));
        var root = probe.Events.Where(r => Read<string>(r, "Site").StartsWith("MapPreparationLedger.", StringComparison.Ordinal) &&
            !Read<string>(r, "Site").StartsWith("MapPreparationLedger.Window.", StringComparison.Ordinal)).ToArray();
        var refused = Assert.Single(root, r => Read<string>(r, "LimitName") == "MaxQueuedBytes" && Read<bool>(r, "Refused"));
        Assert.Equal((capacity.Requested, 8192m), (Read<decimal>(refused, "Requested"), Read<decimal>(refused, "Allowance")));
        var scene = root[Array.IndexOf(root, refused) - 1];
        Assert.Equal("MaxPreparedBytes", Read<string>(scene, "LimitName"));
        Assert.Equal((capacity.Requested, 134_217_728m, false), (Read<decimal>(scene, "Requested"), Read<decimal>(scene, "Allowance"), Read<bool>(scene, "Refused")));
        Assert.Contains(root, r => Read<string>(r, "LimitName") == "MaxQueuedBytes" && !Read<bool>(r, "Refused") && Read<decimal>(r, "Allowance") == 8192);
    }

    [Theory]
    [InlineData("faces")]
    [InlineData("retained")]
    [InlineData("first")]
    public void NativeFoliageChunk_RefusesAtTheBindingOrigin(string bound)
    {
        using var fixture = ResidencyFixture.Foliage(4);
        var policy = fixture.FoliageState().Policy;
        // The construction controls are the first retained allocation of every chunk.
        long declared = bound == "first" ? MapFoliageInventory.ConstructionControls - 1 : bound == "retained" ? MapFoliageInventory.ConstructionControls + 1 : 50_000_000;
        MapRenderSourceState State(int faces) => fixture.StateWithLimits(new(MaxPreparedBytes: 134217728, MaxPreparedFaces: faces)
        { Preparation = new(Foliage: new(MaxQueryRetainedBytes: declared)) },
            ResidencyFixture.Window(0, 64) with { Channels = MapSceneChannels.Foliage }) with
        { Policy = policy };
        var budgets = new MapRenderBudgets(MaxQueuedBytes: 134217728, MaxPlanBytes: 134217728, MaxResidentCpuBytes: 536870912);
        // A one-face scene ceiling binds the first chunk's copy. A foliage-only window prepares no face before it.
        int faces = bound == "faces" ? 1 : 262144;
        var state = State(faces);
        using var probe = new CapacityDemandProbe();
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), budgets, scheduler);
        for (int i = 0; i < 8; i++) { residency.Update(); scheduler.RunAll(); }
        Assert.Empty(residency.AcceptedPlans);
        string site = bound switch
        {
            "faces" => "MapFoliageSupportBuilder.MaxFaces.2",
            "first" => "MapFoliageGenerationAdmission.MaxQueryRetainedBytes.1",
            _ => "MapFoliageRenderCache.MaxQueryRetainedBytes.1",
        };
        string limit = bound == "faces" ? "MaxPreparedFaces" : "MaxQueryRetainedBytes";
        // A one-face chunk ceiling first binds the support grid's cell ceiling, four cells per face plus 64.
        if (bound == "faces") site = "MapFoliageSupportBuilder.MaxFaces.1";
        var rows = probe.Events.Where(r => Read<string>(r, "Site") == site).ToArray();
        var refused = rows.First(r => Read<string>(r, "LimitName") == limit && Read<bool>(r, "Refused"));
        Assert.Equal(bound == "faces" ? faces : declared, Read<decimal>(refused, "Allowance"));
        // The grid's faces are field units: no scene face is consumed before it, and its cells need more than one face.
        if (bound == "faces") Assert.Equal(0m, Read<decimal>(refused, "CurrentDemand"));
        if (bound == "faces") Assert.True(Read<decimal>(refused, "ProposedIncrement") > faces);
        if (bound == "first") Assert.Equal((0m, (decimal)MapFoliageInventory.ConstructionControls), (Read<decimal>(refused, "CurrentDemand"), Read<decimal>(refused, "ProposedIncrement")));
        if (bound == "retained")
        {
            // The aggregate and per-copy generation gates accept the construction controls before the cache's storage refuses.
            foreach (string gate in new[] { "MapFoliageGenerationAdmission.MaxQueryRetainedBytes.1", "MapFoliageGenerationAdmission.MaxQueryRetainedBytes.2" })
                Assert.Contains(probe.Events, r => Read<string>(r, "Site") == gate && Read<string>(r, "LimitName") == "MaxQueryRetainedBytes" &&
                    Of(r) == (0m, (decimal)MapFoliageInventory.ConstructionControls, (decimal)declared, false));
        }
        if (bound != "faces") return;
        // The declared face ceiling accepts the same grid against its own default allowance.
        var declaredFaces = rows[Array.IndexOf(rows, refused) - 1];
        Assert.Equal("MaxFaces", Read<string>(declaredFaces, "LimitName"));
        Assert.Equal((0m, (decimal)new MapFoliageLimits().MaxFaces, false),
            (Read<decimal>(declaredFaces, "CurrentDemand"), Read<decimal>(declaredFaces, "Allowance"), Read<bool>(declaredFaces, "Refused")));
    }

    [Fact]
    public void DecodeView_PeakRemainderRefusesAtTheDeclaredOrigin()
    {
        byte[] glb = NativeRenderAssetFixtures.Glb("instanced");
        // The admitted measurement's public peak, taken without an observer.
        var admitted = GltfDecodeAdmission.Measure(glb, Decode(900_000).Budget);
        Assert.True(admitted.Allowed);
        decimal peak = admitted.PeakBytes;
        using var probe = new CapacityDemandProbe();
        // One byte below the measured peak still admits the metadata scan, then refuses the expanded peak.
        Assert.False(GltfDecodeAdmission.Measure(glb, Decode((long)peak - 1).Budget).Allowed);
        var rows = probe.Events.Where(r => Read<string>(r, "Site") == "GltfDecodeAdmission.MaxPeakBytes.2").ToArray();
        Assert.Equal(new[] { "MaxPreparedBytes", "MaxResidentCpuBytes", "MaxPeakBytes" }, rows.Select(r => Read<string>(r, "LimitName")));
        Assert.Equal((1000m, peak, 1_000_000m, false), Of(rows[0]));
        Assert.Equal((3000m, peak, 2_000_000m, false), Of(rows[1]));
        Assert.Equal((0m, peak, peak - 1, true), Of(rows[2]));
    }
}
