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

    [Fact]
    public void SubstepGeometryIgnoresAFailingParentTick()
    {
        SupportCostRow row = SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Single(r => r.Name == "flat box");
        SupportQueryDiagnostics.Enable(true);
        try
        {
            var samples = new SupportCostSampler(false)
            {
                IsolateAllocations = true,
                CheckedOperation = output => output with { GeometryPassed = false, GeometryMatchesOracle = false },
            }.Measure(row, "DryTick");
            Assert.False(samples[0].GeometryPassed);
            Assert.False(samples[0].ColdGeometryPassed);
            Assert.True(samples.Count > 1);
            foreach (SupportCostSample substep in samples.Skip(1))
            {
                Assert.NotEmpty(substep.SupportReads);
                Assert.True(substep.GeometryMatchesOracle, substep.Operation);
                Assert.True(substep.GeometryPassed, substep.Operation);
                Assert.True(substep.ColdGeometryPassed, substep.Operation);
                Assert.False(SupportCostVerdict.Check(substep, substep, false, 1, 0, hosted: true).Geometry, substep.Operation);
            }
        }
        finally
        {
            SupportQueryDiagnostics.SetGroundSubstepObserver(null, null);
            SupportQueryDiagnostics.SetFindObserver(null);
            SupportQueryDiagnostics.Enable(false);
            SupportQueryDiagnostics.Reset();
        }
    }

    [Fact]
    public void SubstepWithAMismatchedReadOrNoSupportFails()
    {
        SupportCostRow row = SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Single(r => r.Name == "flat box");
        SupportQueryDiagnostics.Enable(true);
        try
        {
            SupportCostSample substep = new SupportCostSampler(false) { IsolateAllocations = true }.Measure(row, "DryTick")[1];
            SupportFindObservation[] warm = substep.SupportReads, cold = substep.ColdSupportReads;
            Assert.NotEmpty(warm);
            Assert.NotEmpty(cold);
            SupportFindObservation[] wrongWarm = [warm[0] with { Height = warm[0].Height + 0.01f }, .. warm[1..]];
            SupportFindObservation[] wrongCold = [cold[0] with { Height = cold[0].Height + 0.01f }, .. cold[1..]];
            using var execution = new SupportCostExecution(row, "DryTick");
            Assert.Equal(new SupportCostSampler.SubstepVerdict(true, true, true), SupportCostSampler.JudgeSubstep(execution, warm, true, cold, true));
            Assert.Equal(new SupportCostSampler.SubstepVerdict(false, false, true), SupportCostSampler.JudgeSubstep(execution, wrongWarm, true, cold, true));
            Assert.Equal(new SupportCostSampler.SubstepVerdict(true, true, false), SupportCostSampler.JudgeSubstep(execution, warm, true, wrongCold, true));
            Assert.Equal(new SupportCostSampler.SubstepVerdict(true, false, true), SupportCostSampler.JudgeSubstep(execution, warm, false, cold, true));
            Assert.Equal(new SupportCostSampler.SubstepVerdict(true, true, false), SupportCostSampler.JudgeSubstep(execution, warm, true, cold, false));
        }
        finally
        {
            SupportQueryDiagnostics.SetGroundSubstepObserver(null, null);
            SupportQueryDiagnostics.SetFindObserver(null);
            SupportQueryDiagnostics.Enable(false);
            SupportQueryDiagnostics.Reset();
        }
    }

    [Fact]
    public void SubstepGeometryJudgesItsOwnReads()
    {
        SupportCostRow row = SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Single(r => r.Name == "hidden crate");
        SupportQueryDiagnostics.Enable(true);
        try
        {
            var samples = new SupportCostSampler(false) { IsolateAllocations = true }.Measure(row, "DryTick");
            SupportCostSample tick = samples[0];
            using (var execution = new SupportCostExecution(row, "DryTick"))
                foreach (SupportFindObservation read in tick.SupportReads.Concat(tick.ColdSupportReads))
                {
                    execution.CheckFind(read, out bool matched);
                    Assert.True(matched, $"Tick read at {read.Axis} feet {read.FeetY}");
                }
            // Every read matches, so the tick fails only on its end placement.
            Assert.False(tick.GeometryPassed);
            Assert.False(tick.GeometryMatchesOracle);
            Assert.True(samples.Count > 1);
            foreach (SupportCostSample substep in samples.Skip(1))
            {
                Assert.Equal("SupportedSubstep", substep.Outcome);
                Assert.Equal("SupportedSubstep", substep.ColdOutcome);
                Assert.True(substep.GeometryMatchesOracle, substep.Operation);
                Assert.True(substep.GeometryPassed, substep.Operation);
                Assert.True(substep.ColdGeometryPassed, substep.Operation);
                Assert.False(SupportCostVerdict.Check(substep, substep, false, 1, 0, hosted: true).Geometry, substep.Operation);
            }
        }
        finally
        {
            SupportQueryDiagnostics.SetGroundSubstepObserver(null, null);
            SupportQueryDiagnostics.SetFindObserver(null);
            SupportQueryDiagnostics.Enable(false);
            SupportQueryDiagnostics.Reset();
        }
    }
}
