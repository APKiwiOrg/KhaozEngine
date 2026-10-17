using System.Linq;
using System.Runtime;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public sealed class SupportCostSamplerTests
{
    [Theory]
    [InlineData("Find")]
    [InlineData("GroundTickBorrowed")]
    [InlineData("DryTick")]
    [InlineData("ExplicitTick")]
    [InlineData("LegacyDownPass")]
    [InlineData("LegacyTreadDownPass")]
    [InlineData("LegacyTick")]
    [InlineData("Lease")]
    public void EveryOperationPrimesHitOnlyPathsBeforeWarmObserversAndResetsItsSamples(string operation)
    {
        var row = SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0])
            .Single(r => r.Name == (operation == "LegacyTreadDownPass" ? "stairs box" : "flat box"))
            with
        { Explicit = operation.StartsWith("Explicit", System.StringComparison.Ordinal) };
        int invocations = 0, beforeWarm = 0;
        bool allInvocationsIsolated = true;
        SupportQueryDiagnostics.Enable(true);
        ContactAllocationDiagnostics.SetObserver((phase, entering) =>
        {
            if (phase == ContactAllocationPhase.Route && entering)
            {
                invocations++;
                allInvocationsIsolated &= GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
            }
        });
        try
        {
            var samples = new SupportCostSampler(false)
            {
                BeforeWarm = () => beforeWarm = invocations,
                IsolateAllocations = true
            }.Measure(row, operation);
            Assert.Equal(2, beforeWarm);
            Assert.Equal(3, invocations);
            Assert.True(allInvocationsIsolated, "Cold, priming and warm operations must share the checked no-GC region.");
            var whole = samples[0];
            Assert.Equal(whole.ColdEndState, whole.EndState);
            // First use builds the floor's world form and every later call binds it. Every other counter repeats.
            Assert.Equal(whole.ColdCounts.Find > 0 ? 1 : 0, whole.ColdCounts.WorldFormBuilds);
            Assert.Equal(0, whole.Counts.WorldFormBuilds);
            Assert.Equal(whole.ColdCounts with { WorldFormBuilds = 0 }, whole.Counts);
            Assert.Equal(whole.ColdQueries, whole.Queries);
            Assert.Equal(whole.Counts.Find, whole.SupportReads.Length);
            Assert.Equal(whole.ColdCounts.Find, whole.ColdSupportReads.Length);
            Assert.Equal(whole.Counts.GroundSubsteps + 1, samples.Count);
            if (operation == "Find")
            {
                Assert.Equal(1, whole.Counts.Find);
                Assert.Single(whole.SupportReads);
                Assert.Single(whole.ColdSupportReads);
                Assert.True(whole.GeometryMatchesOracle);
                Assert.Equal(0, whole.WarmBytes);
            }
        }
        finally
        {
            ContactAllocationDiagnostics.SetObserver(null);
            SupportQueryDiagnostics.SetGroundSubstepObserver(null, null);
            SupportQueryDiagnostics.SetFindObserver(null);
            SupportQueryDiagnostics.Enable(false);
            SupportQueryDiagnostics.Reset();
        }
    }
}
