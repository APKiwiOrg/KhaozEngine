using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Objectives;
using Xunit;

namespace KhaozEngine.Tests;

public class ObjectiveTrackerTests
{
    private static List<ObjectiveCompletion> Capture(ObjectiveTracker tracker)
    {
        var fired = new List<ObjectiveCompletion>();
        tracker.ObjectiveCompleted += fired.Add;
        return fired;
    }

    // ----- counters + scopes --------------------------------------------------

    [Fact]
    public void Report_Accumulates_InBothScopes()
    {
        var t = new ObjectiveTracker();
        t.Report("ore.mined", 3);
        t.Report("ore.mined");           // default amount 1
        t.Report("ore.mined", 6);

        Assert.Equal(10, t.GetSum("ore.mined", MetricScope.Persistent));
        Assert.Equal(10, t.GetSum("ore.mined", MetricScope.Session));
    }

    [Fact]
    public void Observe_TracksMax_NeverLowers_InBothScopes()
    {
        var t = new ObjectiveTracker();
        t.Observe("depth.max", 50);
        t.Observe("depth.max", 120);
        t.Observe("depth.max", 90);      // lower: ignored

        Assert.Equal(120, t.GetMax("depth.max", MetricScope.Persistent));
        Assert.Equal(120, t.GetMax("depth.max", MetricScope.Session));
    }

    [Fact]
    public void ReportAndObserve_OnSameKey_TrackSumAndMaxIndependently()
    {
        var t = new ObjectiveTracker();
        t.Report("k", 5);
        t.Observe("k", 40);
        t.Report("k", 5);

        Assert.Equal(10, t.GetSum("k", MetricScope.Session));
        Assert.Equal(40, t.GetMax("k", MetricScope.Session));
    }

    [Fact]
    public void ResetScope_Session_ClearsSession_KeepsPersistent()
    {
        var t = new ObjectiveTracker();
        t.Report("bars", 100);
        t.Observe("depth", 200);

        t.ResetScope(MetricScope.Session);

        Assert.Equal(0, t.GetSum("bars", MetricScope.Session));
        Assert.Equal(0, t.GetMax("depth", MetricScope.Session));
        Assert.Equal(100, t.GetSum("bars", MetricScope.Persistent));
        Assert.Equal(200, t.GetMax("depth", MetricScope.Persistent));
    }

    [Fact]
    public void ResetScope_All_ClearsBoth()
    {
        var t = new ObjectiveTracker();
        t.Report("bars", 100);
        t.ResetScope(MetricScope.All);
        Assert.Equal(0, t.GetSum("bars", MetricScope.Persistent));
        Assert.Equal(0, t.GetSum("bars", MetricScope.Session));
    }

    // ----- condition kinds ----------------------------------------------------

    [Fact]
    public void AtLeast_Completes_WhenSumReachesTarget()
    {
        var t = new ObjectiveTracker();
        var fired = Capture(t);
        t.Register(ObjectiveDefinition.Create("copper",
            ObjectiveCondition.AtLeast("bars.copper", 500, MetricScope.Persistent)));

        t.Report("bars.copper", 499);
        Assert.False(t.IsComplete("copper"));
        t.Report("bars.copper", 1);

        Assert.True(t.IsComplete("copper"));
        Assert.Single(fired);
        Assert.Equal("copper", fired[0].ObjectiveId);
    }

    [Fact]
    public void Reached_Completes_OnPeak_NotAccumulation()
    {
        var t = new ObjectiveTracker();
        t.Register(ObjectiveDefinition.Create("deep",
            ObjectiveCondition.Reached("depth.max", 200, MetricScope.Persistent)));

        // Many small observes never accumulate to the target - only the peak matters.
        t.Observe("depth.max", 50);
        t.Observe("depth.max", 60);
        t.Observe("depth.max", 199);
        Assert.False(t.IsComplete("deep"));
        t.Observe("depth.max", 200);
        Assert.True(t.IsComplete("deep"));
    }

    [Fact]
    public void AtMost_AsConstraint_BlocksCompletionOnceExceeded()
    {
        var t = new ObjectiveTracker();
        // "reach depth 100 this run with no upgrades bought"
        t.Register(new ObjectiveDefinition("purist", new[]
        {
            ObjectiveCondition.Reached("depth.max", 100, MetricScope.Session),
            ObjectiveCondition.AtMost("upgrades.bought", 0, MetricScope.Session),
        }));

        t.Report("upgrades.bought", 1);   // violates the constraint
        t.Observe("depth.max", 150);       // positive goal met...
        Assert.False(t.IsComplete("purist")); // ...but constraint fails, so no completion
    }

    [Fact]
    public void AndComposition_Completes_OnlyWhenAllConditionsHold()
    {
        var t = new ObjectiveTracker();
        t.Register(new ObjectiveDefinition("both", new[]
        {
            ObjectiveCondition.AtLeast("a", 10, MetricScope.Session),
            ObjectiveCondition.AtLeast("b", 5, MetricScope.Session),
        }));

        t.Report("a", 10);
        Assert.False(t.IsComplete("both"));
        t.Report("b", 5);
        Assert.True(t.IsComplete("both"));
    }

    [Fact]
    public void PuristRun_Completes_WhenDepthReachedWithNoUpgrades()
    {
        var t = new ObjectiveTracker();
        t.Register(new ObjectiveDefinition("purist", new[]
        {
            ObjectiveCondition.Reached("depth.max", 100, MetricScope.Session),
            ObjectiveCondition.AtMost("upgrades.bought", 0, MetricScope.Session),
        }));

        t.Observe("depth.max", 120);       // no upgrades reported, so sum stays 0 <= 0
        Assert.True(t.IsComplete("purist"));
    }

    // ----- constraint-only objectives -----------------------------------------

    private static ObjectiveDefinition NoUpgrades(double target = 0) =>
        ObjectiveDefinition.Create("frugal", ObjectiveCondition.AtMost("upgrades.bought", target, MetricScope.Session));

    [Fact]
    public void AtMostOnly_DoesNotCompleteAtRegister()
    {
        var t = new ObjectiveTracker();
        var fired = Capture(t);
        t.Register(NoUpgrades());          // nothing reported yet: 0 <= 0 holds, but the run has not happened

        Assert.False(t.IsComplete("frugal"));
        Assert.Empty(fired);
    }

    [Fact]
    public void AtMostOnly_DoesNotCompleteOnAReportThatStaysUnderTarget()
    {
        var t = new ObjectiveTracker();
        var fired = Capture(t);
        t.Register(NoUpgrades(target: 3));

        t.Report("upgrades.bought", 1);    // still within the constraint, but the run is not over either

        Assert.False(t.IsComplete("frugal"));
        Assert.Empty(fired);
    }

    [Fact]
    public void AtMostOnly_CompletesOnTheGamesExplicitEvaluateAll()
    {
        var t = new ObjectiveTracker();
        var fired = Capture(t);
        t.Register(NoUpgrades());

        t.EvaluateAll();                   // the game's end-of-run call

        Assert.True(t.IsComplete("frugal"));
        Assert.Single(fired);
        Assert.Equal("frugal", fired[0].ObjectiveId);
    }

    [Fact]
    public void AtMostOnly_ViolatedBeforeEvaluateAll_NeverCompletes()
    {
        var t = new ObjectiveTracker();
        t.Register(NoUpgrades());

        t.Report("upgrades.bought", 1);    // violated
        t.EvaluateAll();

        Assert.False(t.IsComplete("frugal"));
    }

    [Fact]
    public void AtMostOnly_IsNotCompletedByRestore_ButARestoredIdStillBinds()
    {
        var t = new ObjectiveTracker();
        var fired = Capture(t);
        t.Register(NoUpgrades());
        t.Restore(new ObjectivesSnapshot());   // empty counters: "not violated" is not evidence of a finished run

        Assert.False(t.IsComplete("frugal"));
        Assert.Empty(fired);

        var done = new ObjectiveTracker();
        done.Register(NoUpgrades());
        done.Restore(new ObjectivesSnapshot { Completed = new List<string> { "frugal" } });
        Assert.True(done.IsComplete("frugal"));
    }

    // ----- completion semantics ----------------------------------------------

    [Fact]
    public void Completion_FiresExactlyOnce_EvenPastTarget()
    {
        var t = new ObjectiveTracker();
        var fired = Capture(t);
        t.Register(ObjectiveDefinition.Create("x",
            ObjectiveCondition.AtLeast("k", 10, MetricScope.Persistent)));

        t.Report("k", 10);   // completes
        t.Report("k", 10);   // already complete
        t.Report("k", 10);

        Assert.Single(fired);
    }

    [Fact]
    public void Completion_EchoesOpaqueMetadata()
    {
        var t = new ObjectiveTracker();
        var fired = Capture(t);
        t.Register(new ObjectiveDefinition("hard",
            new[] { ObjectiveCondition.Reached("depth.max", 200, MetricScope.Persistent) },
            metadata: "tier:hard"));

        t.Observe("depth.max", 200);
        Assert.Single(fired);
        Assert.Equal("tier:hard", fired[0].Metadata);
    }

    // ----- index-by-key guard -------------------------------------------------

    [Fact]
    public void UnrelatedKeyReports_DoNotAffectAnObjective()
    {
        var t = new ObjectiveTracker();
        t.Register(ObjectiveDefinition.Create("gold",
            ObjectiveCondition.AtLeast("gold", 10, MetricScope.Session)));

        t.Report("gold", 9);
        for (int i = 0; i < 1000; i++)
            t.Report("silver", 100);   // a key no objective watches

        Assert.False(t.IsComplete("gold"));
        t.Report("gold", 1);
        Assert.True(t.IsComplete("gold"));
    }

    [Fact]
    public void ReportingOneKey_CompletesOnlyObjectivesWatchingThatKey()
    {
        var t = new ObjectiveTracker();
        var fired = Capture(t);
        t.Register(ObjectiveDefinition.Create("a", ObjectiveCondition.AtLeast("ka", 1, MetricScope.Session)));
        t.Register(ObjectiveDefinition.Create("b", ObjectiveCondition.AtLeast("kb", 1, MetricScope.Session)));

        t.Report("ka", 1);

        Assert.True(t.IsComplete("a"));
        Assert.False(t.IsComplete("b"));
        Assert.Single(fired);
        Assert.Equal("a", fired[0].ObjectiveId);
    }

    // ----- progress introspection --------------------------------------------

    [Fact]
    public void GetProgress_ReportsCurrentTargetAndSatisfaction()
    {
        var t = new ObjectiveTracker();
        t.Register(new ObjectiveDefinition("both", new[]
        {
            ObjectiveCondition.AtLeast("a", 10, MetricScope.Session),
            ObjectiveCondition.Reached("b", 100, MetricScope.Session),
        }));
        t.Report("a", 4);
        t.Observe("b", 100);

        var p = t.GetProgress("both");
        Assert.False(p.IsComplete);
        Assert.Equal(2, p.Conditions.Count);

        Assert.Equal(4, p.Conditions[0].Current);
        Assert.Equal(10, p.Conditions[0].Target);
        Assert.False(p.Conditions[0].IsSatisfied);

        Assert.Equal(100, p.Conditions[1].Current);
        Assert.True(p.Conditions[1].IsSatisfied);
    }

    [Fact]
    public void GetProgress_UnknownId_Throws()
        => Assert.Throws<System.ArgumentException>(() => new ObjectiveTracker().GetProgress("nope"));

    // ----- persistence --------------------------------------------------------

    [Fact]
    public void Snapshot_RoundTrip_PreservesCountersAndCompletion()
    {
        var a = new ObjectiveTracker();
        a.Register(ObjectiveDefinition.Create("copper",
            ObjectiveCondition.AtLeast("bars.copper", 500, MetricScope.Persistent)));
        a.Register(ObjectiveDefinition.Create("iron",
            ObjectiveCondition.AtLeast("bars.iron", 100, MetricScope.Session)));
        a.Report("bars.copper", 500);   // completes copper
        a.Report("bars.iron", 40);       // partial
        a.Observe("depth.max", 175);

        ObjectivesSnapshot snap = a.Capture();

        var b = new ObjectiveTracker();
        var firedB = Capture(b);
        b.Register(ObjectiveDefinition.Create("copper",
            ObjectiveCondition.AtLeast("bars.copper", 500, MetricScope.Persistent)));
        b.Register(ObjectiveDefinition.Create("iron",
            ObjectiveCondition.AtLeast("bars.iron", 100, MetricScope.Session)));
        b.Restore(snap);

        Assert.True(b.IsComplete("copper"));       // completion survived
        Assert.Empty(firedB);                       // ...without re-firing
        Assert.False(b.IsComplete("iron"));
        Assert.Equal(500, b.GetSum("bars.copper", MetricScope.Persistent));
        Assert.Equal(40, b.GetSum("bars.iron", MetricScope.Session));
        Assert.Equal(175, b.GetMax("depth.max", MetricScope.Persistent));
    }

    [Fact]
    public void Restore_SurfacesObjectiveAlreadySatisfiedByRestoredCounters()
    {
        // A challenge added in a patch: the player's restored lifetime total already meets it.
        var snap = new ObjectivesSnapshot
        {
            Metrics = new List<MetricCellSnapshot>
            {
                new() { Key = "bars.copper", Scope = MetricScope.Persistent, Sum = 600 },
            },
        };

        var t = new ObjectiveTracker();
        var fired = Capture(t);
        t.Register(ObjectiveDefinition.Create("copper.master",
            ObjectiveCondition.AtLeast("bars.copper", 500, MetricScope.Persistent)));
        t.Restore(snap);

        Assert.True(t.IsComplete("copper.master"));
        Assert.Single(fired);
    }

    [Fact]
    public void Restore_CompletedIdBeforeRegister_BindsSilently()
    {
        var snap = new ObjectivesSnapshot { Completed = new List<string> { "x" } };

        var t = new ObjectiveTracker();
        var fired = Capture(t);
        t.Restore(snap);                       // no definitions registered yet
        t.Register(ObjectiveDefinition.Create("x",
            ObjectiveCondition.AtLeast("k", 10, MetricScope.Session)));

        Assert.True(t.IsComplete("x"));
        Assert.Empty(fired);                    // completed before the save; must not re-fire
    }

    [Fact]
    public void Capture_IsDeterministic_ForIdenticalOperationSequences()
    {
        static ObjectivesSnapshot Build()
        {
            var t = new ObjectiveTracker();
            t.Register(ObjectiveDefinition.Create("o1", ObjectiveCondition.AtLeast("z", 5, MetricScope.Persistent)));
            t.Register(ObjectiveDefinition.Create("o2", ObjectiveCondition.Reached("a", 3, MetricScope.Session)));
            t.Report("z", 5);
            t.Observe("a", 4);
            t.Report("m", 2);
            return t.Capture();
        }

        ObjectivesSnapshot x = Build();
        ObjectivesSnapshot y = Build();

        Assert.Equal(x.Completed, y.Completed);
        Assert.Equal(x.Metrics.Count, y.Metrics.Count);
        for (int i = 0; i < x.Metrics.Count; i++)
        {
            Assert.Equal(x.Metrics[i].Key, y.Metrics[i].Key);
            Assert.Equal(x.Metrics[i].Scope, y.Metrics[i].Scope);
            Assert.Equal(x.Metrics[i].Sum, y.Metrics[i].Sum);
            Assert.Equal(x.Metrics[i].Max, y.Metrics[i].Max);
        }
    }

    // ----- validation + localization -----------------------------------------

    [Fact]
    public void Register_DuplicateId_Throws()
    {
        var t = new ObjectiveTracker();
        t.Register(ObjectiveDefinition.Create("dup", ObjectiveCondition.AtLeast("k", 1, MetricScope.Session)));
        Assert.Throws<System.ArgumentException>(() =>
            t.Register(ObjectiveDefinition.Create("dup", ObjectiveCondition.AtLeast("k", 1, MetricScope.Session))));
    }

    [Fact]
    public void Condition_MultiScope_Throws()
        => Assert.Throws<System.ArgumentException>(() =>
            ObjectiveCondition.AtLeast("k", 1, MetricScope.All));

    [Fact]
    public void Definition_EmptyConditions_Throws()
        => Assert.Throws<System.ArgumentException>(() =>
            new ObjectiveDefinition("x", System.Array.Empty<ObjectiveCondition>()));

    [Fact]
    public void Definition_CarriesLocalizedNameByStringId()
    {
        var name = new StringId("Objective.Copper.Name");
        var def = new ObjectiveDefinition("copper",
            new[] { ObjectiveCondition.AtLeast("bars.copper", 500, MetricScope.Persistent) },
            name: name);

        Assert.Equal(name, def.Name);
        Assert.Null(def.Description);
    }
}
