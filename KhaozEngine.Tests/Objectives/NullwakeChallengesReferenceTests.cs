using System.Collections.Generic;
using KhaozEngine.Objectives;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// The reference consumer from the Objectives spec: Nullwake's Challenges system. This is game-side code
/// (rewards / points / tiers live entirely here), built against the framework's integration contract to prove
/// the seams hold. It is NOT part of the engine - it lives in the test project as an executable example.
/// </summary>
public class NullwakeChallengesReferenceTests
{
    /// <summary>Game-side wrapper: maps Nullwake's TotalXxx -&gt; Persistent and RunXxx -&gt; Session, and reads the
    /// tier tag off completion metadata to pick a normal vs hard point pool.</summary>
    private sealed class NullwakeChallenges
    {
        private readonly ObjectiveTracker _objectives = new();

        public int NormalPoints { get; private set; }
        public int HardPoints { get; private set; }
        public readonly List<string> Completed = new();

        public NullwakeChallenges()
        {
            _objectives.ObjectiveCompleted += OnCompleted;

            _objectives.Register(ObjectiveDefinition.Create("copper.500",
                ObjectiveCondition.AtLeast("bars.copper", 500, MetricScope.Persistent)));

            _objectives.Register(ObjectiveDefinition.Create("iron.run.100",
                ObjectiveCondition.AtLeast("bars.iron", 100, MetricScope.Session)));

            _objectives.Register(new ObjectiveDefinition("depth.200",
                new[] { ObjectiveCondition.Reached("depth.max", 200, MetricScope.Persistent) },
                metadata: "tier:hard"));

            _objectives.Register(new ObjectiveDefinition("purist.100", new[]
            {
                ObjectiveCondition.Reached("depth.max", 100, MetricScope.Session),
                ObjectiveCondition.AtMost("upgrades.bought", 0, MetricScope.Session),
            }));
        }

        private void OnCompleted(ObjectiveCompletion c)
        {
            Completed.Add(c.ObjectiveId);
            if (c.Metadata as string == "tier:hard")
                HardPoints += 10;
            else
                NormalPoints += 5;
        }

        // Event sites.
        public void MineBar(string metal) => _objectives.Report($"bars.{metal}");
        public void Descend(int depth) => _objectives.Observe("depth.max", depth);
        public void BuyUpgrade() => _objectives.Report("upgrades.bought");

        // Nullwake's run boundary.
        public void Wake() => _objectives.ResetScope(MetricScope.Session);

        public bool IsComplete(string id) => _objectives.IsComplete(id);
        public ObjectivesSnapshot Save() => _objectives.Capture();
    }

    [Fact]
    public void PuristRun_AwardsHardTier_AndClearsOnWake()
    {
        var game = new NullwakeChallenges();

        // A clean deep dive: reach 120 this run, buy nothing.
        for (int i = 0; i < 100; i++) game.MineBar("iron");
        game.Descend(120);

        Assert.True(game.IsComplete("iron.run.100"));   // 100 iron this run
        Assert.True(game.IsComplete("purist.100"));      // depth 100 session, no upgrades
        Assert.False(game.IsComplete("depth.200"));      // not that deep yet

        // Next run: session resets, so run-scoped goals restart; lifetime goals persist.
        game.Wake();
        game.BuyUpgrade();                                // would have blocked purist - but it already completed
        Assert.True(game.IsComplete("purist.100"));       // idempotent: stays complete
    }

    [Fact]
    public void LifetimeCopper_AccumulatesAcrossRuns_UnaffectedByWake()
    {
        var game = new NullwakeChallenges();

        for (int i = 0; i < 300; i++) game.MineBar("copper");
        game.Wake();                                      // new run - Persistent is untouched
        Assert.False(game.IsComplete("copper.500"));
        for (int i = 0; i < 200; i++) game.MineBar("copper");

        Assert.True(game.IsComplete("copper.500"));       // 500 lifetime across two runs
        Assert.Equal(5, game.NormalPoints);               // normal-tier award
    }

    [Fact]
    public void HardTierDepth_RoutesToHardPointPool()
    {
        var game = new NullwakeChallenges();
        game.BuyUpgrade();                                // block the purist constraint so only depth.200 completes
        game.Descend(200);

        Assert.True(game.IsComplete("depth.200"));
        Assert.False(game.IsComplete("purist.100"));      // an upgrade was bought this run
        Assert.Equal(10, game.HardPoints);                // tier:hard metadata routed to the hard pool
        Assert.Equal(0, game.NormalPoints);
    }
}
