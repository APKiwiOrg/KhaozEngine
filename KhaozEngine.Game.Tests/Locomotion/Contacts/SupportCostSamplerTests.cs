using System;
using System.Linq;
using System.Numerics;
using System.Runtime;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
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

    // The hidden crate's paced climb ends 0.22 m below the box edge it seats on, inside the legs band. Its tick and
    // every substep pass on reads that each match the oracle. The forced failing parent is
    // SubstepGeometryIgnoresAFailingParentTick.
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
            Assert.True(tick.GeometryPassed);
            Assert.True(tick.GeometryMatchesOracle);
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

    // A tick whose rise from its feet to the support it ends on exceeds MaxStepClimbSpeed * dt is a paced climb.
    [Theory]
    [InlineData("stairs box", true)]
    [InlineData("stairs hull", true)]
    [InlineData("stairs mesh", true)]
    [InlineData("pushed seat", true)]
    [InlineData("hidden crate", true)]
    [InlineData("flat box", false)]
    public void PacedPlacementFollowsTheClimbBudget(string name, bool paced)
    {
        SupportCostRow row = SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Single(r => r.Name == name);
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            using var execution = new SupportCostExecution(row, "DryTick");
            SupportCostOutput output = execution.CheckGeometry(execution.Invoke(), readsMatch: true);
            Assert.Equal(paced, output.Paced);
            Assert.True(output.GeometryPassed, $"{name} ended at {output.EndFeet}");
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    // A paced end must reach the lowest of the support, the budgeted height and the roof limit. The crate's 0.22 m
    // paced lag does. The #1437 descent, feet 0.0589 under the 0.3366 edge, does not.
    [Fact]
    public void PacedPlacementHoldsTheBudgetedHeight()
    {
        using var execution = Paced("hidden crate", out SupportCostOutput output);
        Assert.Null(execution.RoofLimit(new(output.EndFeet.X, output.EndFeet.Z), 0.3366));
        SupportCostOutput paced = execution.CheckGeometry(output, readsMatch: true);
        Assert.True(paced.Paced);
        Assert.True(paced.GeometryPassed, $"Paced end {output.EndFeet}");
        SupportCostOutput descended = execution.CheckGeometry(
            output with { EndFeet = output.EndFeet with { Y = 0.058931626f } }, readsMatch: true);
        Assert.True(descended.Paced);
        Assert.False(descended.GeometryPassed);
    }

    // The pushed seat's ceiling underside is at 1.82, the body is 1.8 tall and a push keeps the 0.001 contact skin
    // below the roof, so its roof limit is 0.019 and binds below the 0.117 budget and the 0.25 support.
    [Fact]
    public void PacedPlacementStopsAtTheRoofLimit()
    {
        using var execution = Paced("pushed seat", out SupportCostOutput output);
        var axis = new Vector2(output.EndFeet.X, output.EndFeet.Z);
        Assert.InRange(Math.Abs(execution.RoofLimit(axis, 0.25)!.Value - 0.019), 0, 1e-6);
        SupportCostOutput paced = execution.CheckGeometry(output, readsMatch: true);
        Assert.True(paced.Paced);
        Assert.True(paced.GeometryPassed, $"Paced end {output.EndFeet}");
    }

    // A ceiling 2.3 above the crate scene gives a roof limit of 0.499, above the 0.117 budgeted height that binds, so
    // the #1437 descent still fails beneath it.
    [Fact]
    public void ARoofLimitThatDoesNotBindExcusesNoLag()
    {
        using var execution = Paced("hidden crate", out SupportCostOutput output);
        execution.Scene.Install(new BoxShape(new(2, 0.05f, 2)), Pose.At(new(0, 2.35f, 0)));
        var axis = new Vector2(output.EndFeet.X, output.EndFeet.Z);
        Assert.InRange(Math.Abs(execution.RoofLimit(axis, 0.3366)!.Value - 0.499), 0, 1e-6);
        Assert.True(execution.CheckGeometry(output, readsMatch: true).GeometryPassed, $"Paced end {output.EndFeet}");
        SupportCostOutput descended = execution.CheckGeometry(
            output with { EndFeet = output.EndFeet with { Y = 0.058931626f } }, readsMatch: true);
        Assert.True(descended.Paced);
        Assert.False(descended.GeometryPassed);
    }

    static SupportCostExecution Paced(string name, out SupportCostOutput output)
    {
        SupportCostRow row = SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Single(r => r.Name == name);
        var execution = new SupportCostExecution(row, "DryTick");
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true));
        try { output = execution.Invoke(); }
        catch
        {
            execution.Dispose();
            throw;
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
        return execution;
    }
}
