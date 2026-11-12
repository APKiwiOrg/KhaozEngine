using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using KhaozEngine.Tests.WaterPrismCensus;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "Diagnostic")]
[Trait("Category", "WaterKernelContract")]
public sealed class WaterKernelExtendedCorpusTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("bank-stand")]
    [InlineData("bank-along")]
    [InlineData("bank-across")]
    [InlineData("swim-entry")]
    [InlineData("deep-swim")]
    [InlineData("deep-tread")]
    [InlineData("flat-neighbour")]
    [InlineData("flat-control")]
    [InlineData("dry-control")]
    public void ExpandedPatchSweepMatchesTheFrozenTraceOnThisRuntime(string name)
    {
        using var scene = new WaterPrismScenario(name);
        var reference = new FrozenWaterReference(scene.Colliders.Medium.CaptureTraceForTests());
        int traces = 0;
        scene.Field.Recorded = (query, _, _, _) =>
        {
            _ = FrozenWaterComparison.Check(scene.Colliders.Medium, reference, query);
            traces++;
        };
        var state = scene.Basis;
        for (int tick = 0; tick < 4; tick++) state = scene.Step(state, scene.Command);
        Assert.True(traces > 0);
        int count = WaterKernelCorpus.Sweep(scene.Colliders.Medium, reference,
            185484 + Array.IndexOf(WaterPrismScenario.Names, name), 252);
        Assert.Equal(264, count);
        output.WriteLine($"CORPUS_EXT case={name}, traces={traces}, queries={count}");
    }

    [Fact]
    public void EveryExistingBankLiftGridInputMatchesTheFrozenTrace()
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document(109, 3));
        var snapshot = field.CaptureTraceForTests();
        var reference = new FrozenWaterReference(snapshot);
        float anchor = snapshot.Ground.HeightAt(110.375f, -236.79904f);
        int count = 0;
        foreach (float z in new[] { -236.59904f, -236.69904f, -236.79904f, -236.89905f, -236.97406f })
            foreach (float x in TileWaterBankLiftGridTests.Laterals(z))
                foreach (float lift in new[] { 0.096971154f, 0.2f, 0.4f })
                {
                    float y = -0.45606077f + (snapshot.Ground.HeightAt(x, z) - anchor);
                    var query = new MovementMediumSweepQuery(new(new(x, y, z), 0.3f, 0.75f,
                        GrazingWaterBank.Space, null), new(0, lift, 0));
                    _ = FrozenWaterComparison.Check(field, reference, query, identity: GrazingWaterBank.Identity);
                    count++;
                }
        Assert.Equal(510, count);
        output.WriteLine($"CORPUS_EXT bank-lift-grid={count}");
    }
}
