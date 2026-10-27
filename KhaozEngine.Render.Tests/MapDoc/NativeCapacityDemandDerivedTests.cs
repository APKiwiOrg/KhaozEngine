using System;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Nested preparation, decode and retention byte views publish their originating profile fields.</summary>
[Collection("AllocSensitive")]
public sealed class NativeCapacityDemandDerivedTests
{
    [Fact]
    public void DecodeView_PublishesPreparedResidentAndDeclaredCeilings()
    {
        var scene = new MapSceneAdmission(new(MaxPreparedBytes: 1_000_000));
        Assert.True(scene.TryReservePreparation(1000, 0));
        var budgets = new MapRenderBudgets(MaxResidentCpuBytes: 2_000_000);
        var residency = new MapRenderAdmission(budgets);
        using var cpu = residency.TryReserveCpu(3000)!;
        using var probe = new CapacityDemandProbe();
        var decode = new MapRenderDecodeAllowance(scene, residency, budgets, new(MaxPeakBytes: 900_000));
        var result = GltfDecodeAdmission.Measure(NativeRenderAssetFixtures.Glb("instanced"), decode.Budget);
        Assert.True(result.Allowed);
        var rows = probe.Events.Where(r => Read<string>(r, "Site") == "GltfDecodeAdmission.MaxPeakBytes.1").ToArray();
        // The decode peak is the least of the scene's remaining 999000, residency's remaining 1997000 and the declared 900000.
        Assert.Equal(new[] { "MaxPreparedBytes", "MaxResidentCpuBytes", "MaxPeakBytes" }, rows.Select(r => Read<string>(r, "LimitName")));
        Assert.Equal(new decimal[] { 1000, 3000, 0 }, rows.Select(r => Read<decimal>(r, "CurrentDemand")));
        Assert.Equal(new decimal[] { 1_000_000, 2_000_000, 900_000 }, rows.Select(r => Read<decimal>(r, "Allowance")));
        Assert.Single(rows.Select(r => Read<decimal>(r, "ProposedIncrement")).Distinct());
        Assert.All(rows, r => Assert.False(Read<bool>(r, "Refused")));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SelectedRemainder_PublishesAdmittedPiecesAtTheDecodeGate(bool parts, bool refused)
    {
        var closure = NativeRenderAssetFixtures.Create();
        var budget = new GltfDecodeBudget(new());
        var proofs = MapRenderExtentValidator.Validate(closure, ["prop-a"], budget).Proofs!;
        using var first = MapRenderAssets.LoadSelected(closure, ["prop-a"], proofs, budget).Assets!;
        long bytes = first.RetainedBytes;
        long ceiling = parts ? (refused ? 10 : 100) : (refused ? bytes + 1 : 4 * bytes);
        var allowance = new MapRenderAssetAllowance(parts ? new(MaxParts: (int)ceiling) : new(MaxDecodedBytes: ceiling));
        allowance.Record(first);
        using var probe = new CapacityDemandProbe();
        var second = MapRenderAssets.LoadSelected(closure, ["prop-a"], proofs, budget, allowance.Remaining);
        Assert.Equal(refused, second.Capacity is not null);
        second.Assets?.Dispose();
        string limit = parts ? "MaxParts" : "MaxDecodedBytes";
        var rows = probe.For(limit).Where(r => Read<string>(r, "Site") == "MapRenderAssets." + limit + ".1").ToArray();
        Assert.NotEmpty(rows);
        // The first piece's six parts, or its retained bytes, are the second piece's consumed baseline at every tier.
        Assert.Equal(parts ? 6 : bytes, Read<decimal>(rows[0], "CurrentDemand"));
        Assert.All(rows, r => Assert.True(Read<decimal>(r, "CurrentDemand") >= (parts ? 6 : bytes)));
        Assert.All(rows, r => Assert.Equal(ceiling, Read<decimal>(r, "Allowance")));
        Assert.All(rows, r => Assert.Equal(Read<decimal>(r, "Requested") > ceiling, Read<bool>(r, "Refused")));
        Assert.Equal(refused, Read<bool>(rows[^1], "Refused"));
    }

    [Fact]
    public void BuildReservation_PublishesTheQueueAndSceneCeilingsAtTheRootLedger()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.StateWithLimits(new(MaxPreparedBytes: 134_217_728) { Preparation = new(FoliageContext: new()) }, ResidencyFixture.Window());
        using var probe = new CapacityDemandProbe();
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(),
            new(MaxQueuedBytes: 33_554_432, MaxPlanBytes: 67_108_864), scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var root = probe.Events.Where(r => Read<string>(r, "Site").StartsWith("MapPreparationLedger.", StringComparison.Ordinal) &&
            !Read<string>(r, "Site").StartsWith("MapPreparationLedger.Window.", StringComparison.Ordinal)).ToArray();
        var build = root.Where(r => root.Any(q => Read<string>(q, "LimitName") == "MaxQueuedBytes" &&
            Read<long>(q, "AdmissionId") == Read<long>(r, "AdmissionId"))).ToArray();
        var queued = build.Where(r => Read<string>(r, "LimitName") == "MaxQueuedBytes").ToArray();
        var scene = build.Where(r => Read<string>(r, "LimitName") == "MaxPreparedBytes").ToArray();
        Assert.NotEmpty(queued);
        // The build reservation is the queue's 32 MiB. The scene's 128 MiB ceiling sees the same live demand.
        Assert.Equal(queued.Length, scene.Length);
        Assert.All(queued, r => Assert.Equal(33_554_432, Read<decimal>(r, "Allowance")));
        Assert.All(scene, r => Assert.Equal(134_217_728, Read<decimal>(r, "Allowance")));
        Assert.Equal(queued.Select(r => Read<decimal>(r, "Requested")), scene.Select(r => Read<decimal>(r, "Requested")));
        Assert.All(build, r => Assert.False(Read<bool>(r, "Refused")));
    }

    [Fact]
    public void NativeFoliageChunk_PublishesPreparedFacesBytesAndAggregateRetention()
    {
        using var fixture = ResidencyFixture.Foliage(4);
        var layer = fixture.Document.Foliage!.Layers[0];
        var density = new byte[1024]; Array.Fill(density, (byte)255, 0, 128);
        fixture.Document.Foliage = new(1, [MapFoliageLayer.FromStorage(layer, [], [layer.Chunks[0], new(new(1, 0), density)])]);
        var policy = fixture.FoliageState().Policy;
        var state = fixture.StateWithLimits(new(MaxPreparedBytes: 134217728) { Preparation = new(Foliage: new(MaxQueryRetainedBytes: 50_000_000)) },
            ResidencyFixture.Window(0, 64) with { Channels = MapSceneChannels.Foliage }) with
        { Policy = policy };
        using var probe = new CapacityDemandProbe();
        var scheduler = new ResidencyScheduler();
        var budgets = new MapRenderBudgets(MaxQueuedBytes: 134217728, MaxPlanBytes: 134217728, MaxResidentCpuBytes: 536870912);
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), budgets, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var faces = probe.Events.Where(r => Read<string>(r, "Site") == "MapFoliageSupportBuilder.MaxFaces.2").ToArray();
        // A chunk's face ceiling is the declared MaxFaces, the scene's remaining faces and its remaining bytes at 2560 per face.
        Assert.Contains(faces, r => Read<string>(r, "LimitName") == "MaxFaces" && Read<decimal>(r, "Allowance") == new MapFoliageLimits().MaxFaces);
        var prepared = faces.Where(r => Read<string>(r, "LimitName") == "MaxPreparedFaces").ToArray();
        Assert.NotEmpty(prepared);
        Assert.All(prepared, r => Assert.Equal(new MapSceneLimits().MaxPreparedFaces, Read<decimal>(r, "Allowance")));
        var plan = faces.Where(r => Read<string>(r, "LimitName") == "MaxPlanBytes").ToArray();
        Assert.Equal(prepared.Length, plan.Length);
        Assert.All(plan, r => Assert.Equal(134217728, Read<decimal>(r, "Allowance")));
        for (int i = 0; i < plan.Length; i++)
            Assert.Equal(2560 * Read<decimal>(prepared[i], "ProposedIncrement"), Read<decimal>(plan[i], "ProposedIncrement"));
        var columns = probe.Events.Where(r => Read<string>(r, "Site") == "MapFoliageColumnBudget.MaxQueryRetainedBytes.1").ToArray();
        // The second chunk's column ceiling includes the first chunk's retained bytes against the declared 50000000.
        var aggregate = columns.Where(r => Read<string>(r, "LimitName") == "MaxQueryRetainedBytes" && Read<decimal>(r, "Allowance") == 50_000_000).ToArray();
        Assert.Contains(aggregate, r => Read<decimal>(r, "CurrentDemand") > 0);
        Assert.Contains(columns, r => Read<string>(r, "LimitName") == "MaxPlanBytes" && Read<decimal>(r, "Allowance") == 134217728);
        Assert.Contains(columns, r => Read<string>(r, "LimitName") == "MaxPreparedBytes" && Read<decimal>(r, "Allowance") == 134217728);
        Assert.All(faces.Concat(columns), r => Assert.False(Read<bool>(r, "Refused")));
    }
}
