using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc;

[Collection("AllocSensitive")]
public sealed class NativeCapacityDemandTests
{
    [Fact]
    public void MultiWindowBuild_UsesOneCumulativeAdmissionAndDistinctNestedOperationScopes()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.StateWithLimits(new() { Preparation = new(FoliageContext: new()) },
            ResidencyFixture.Window(), ResidencyFixture.Window(-3, 5));
        using var probe = new CapacityDemandProbe();
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var rows = probe.For("MaxSourceUtf8Bytes").Where(r => Read<decimal>(r, "ProposedIncrement") > 0).ToArray();
        Assert.NotEmpty(rows);
        Assert.Single(rows.Select(r => Read<long>(r, "AdmissionId")).Distinct());
        Assert.Single(rows.Select(r => Read<long>(r, "RootOperationId")).Distinct());
        var acquisitions = rows.Where(r => Read<string>(r, "Operation") == "MapSceneAcquisition").ToArray();
        var windows = acquisitions.GroupBy(r => Read<long>(r, "ParentOperationId")).ToArray();
        Assert.Equal(2, windows.Length);
        Assert.All(windows, w => Assert.NotEqual(0, w.Key));
        Assert.NotEqual(windows[0].Key, windows[1].Key);
        // Each invented window acquires one placement and its embedded light, each charged at 512 bytes.
        Assert.Equal(0, Read<decimal>(windows[0].First(), "CurrentDemand"));
        decimal first = windows[0].Sum(r => Read<decimal>(r, "ProposedIncrement"));
        Assert.Equal(1024, first);
        Assert.Equal(first, Read<decimal>(windows[0].Last(), "Requested"));
        Assert.Equal(first, Read<decimal>(windows[1].First(), "CurrentDemand"));
        Assert.Equal(1024, windows[1].Sum(r => Read<decimal>(r, "ProposedIncrement")));
        Assert.Equal(2048, Read<decimal>(windows[1].Last(), "Requested"));
        Assert.Equal(2048, rows.Max(r => Read<decimal>(r, "Requested")));
        Assert.All(rows, r => Assert.False(Read<bool>(r, "Refused")));
        Assert.Equal(2, residency.AcceptedPlans.Count);
    }

    [Fact]
    public void QueueProbe_ReportsSuccessAndRefusalWithoutEnqueuing()
    {
        using var probe = new CapacityDemandProbe();
        var queue = new MapRenderBuildQueue(new(MaxQueuedBytes: 10), new(), TaskScheduler.Default);
        Assert.True(queue.CanEnqueue(1, 7));
        Assert.False(queue.CanEnqueue(1, 11));
        Assert.Equal(0, queue.Count);
        var rows = probe.For("MaxQueuedBytes").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(7, Read<decimal>(rows[0], "Requested"));
        Assert.Equal(11, Read<decimal>(rows[1], "Requested"));
        Assert.False(Read<bool>(rows[0], "Refused"));
        Assert.True(Read<bool>(rows[1], "Refused"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodeDemand_ReportsBeforeAndAfterExpansionAndRetainsTheOriginalRefusal(bool refused)
    {
        using var probe = new CapacityDemandProbe();
        var budget = new GltfDecodeBudget(new(MaxVertices: refused ? 1 : 1_048_576));
        var result = GltfDecodeAdmission.Measure(NativeRenderAssetFixtures.Glb("instanced"), budget);
        Assert.Equal(!refused, result.Allowed);
        var rows = probe.For("MaxVertices").ToArray();
        Assert.NotEmpty(rows);
        Assert.Equal(refused, rows.Any(r => Read<bool>(r, "Refused")));
        Assert.Equal(0, budget.OwnedBytes);
        if (!refused) Assert.Contains(rows, r => Read<decimal>(r, "Requested") == result.ExpandedVertexReferences);
    }
}
