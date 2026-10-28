using System;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc;

[Collection("AllocSensitive")]
public sealed class NativeCapacityDemandFoliageTests
{
    [Fact]
    public void NativeChunks_KeepTheOriginalSampleProfileThroughTheCacheCopy()
    {
        using var fixture = ResidencyFixture.Foliage(4);
        var layer = fixture.Document.Foliage!.Layers[0];
        var density = new byte[1024]; Array.Fill(density, (byte)255, 0, 128);
        fixture.Document.Foliage = new(1, [MapFoliageLayer.FromStorage(layer, [], [layer.Chunks[0], new(new(1, 0), density)])]);
        var context = MapFoliageGenerationContext.Capture(fixture.Document, fixture.Closure).Context!;
        var first = MapFoliageDistribution.GenerateInstances(context, "cover", default);
        var second = MapFoliageDistribution.GenerateInstances(context, "cover", new(1, 0));
        Assert.True(first.Count > 0 && second.Count > 0);
        long allowance = first.Count + second.Count - 1;
        var policy = fixture.FoliageState().Policy;
        var state = fixture.StateWithLimits(new(MaxPreparedBytes: 134217728)
        { Preparation = new(Foliage: new(MaxQuerySamples: allowance)) },
            ResidencyFixture.Window(0, 64) with { Channels = MapSceneChannels.Foliage }) with
        { Policy = policy };
        using var probe = new CapacityDemandProbe();
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(),
            new(MaxQueuedBytes: 134217728, MaxPlanBytes: 134217728, MaxResidentCpuBytes: 536870912), scheduler);
        scheduler.RunAll(); residency.Update(); residency.Update();
        Assert.Empty(residency.AcceptedPlans);
        var rows = probe.For("MaxQuerySamples").Where(row => Read<string>(row, "Site") == "MapFoliageInstanceBuilder.MaxQuerySamples.1").ToArray();
        var chunks = rows.GroupBy(row => Read<long>(row, "AdmissionId")).ToArray();
        Assert.Equal(2, chunks.Length);
        Assert.Equal(0, Read<decimal>(chunks[0].First(), "CurrentDemand"));
        Assert.Equal(first.Count, Read<decimal>(chunks[1].First(), "CurrentDemand"));
        Assert.All(rows, row => Assert.Equal(allowance, Read<decimal>(row, "Allowance")));
        Assert.Equal(allowance + 1, Read<decimal>(chunks[1].Last(), "Requested"));
        Assert.True(Read<bool>(chunks[1].Last(), "Refused"));
    }
}
