using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePreparationSchedulerTests
{
    static readonly TileCombatPreparationProfile Profile = new(3, 1, 7);

    [Fact]
    public void First_attempt_uses_full_lead()
    {
        TileCombatPreparationState state = default;

        Assert.True(Create(ref state, 100, 100, out TilePreparationFailure failure));

        Assert.Equal(TilePreparationFailure.None, failure);
        Assert.True(state.HasActive);
        Assert.Equal(new TileCombatPreparation(10, 20, 1, 1, 7, 100, 103, 1, 14), state.Active);
        Assert.Equal(102L, state.Active.StrikeTick);
        Assert.Equal(1UL, state.LastAttackId);
        Assert.Equal(4U, state.AttackerTeleportEpoch);
        Assert.Equal(5U, state.TargetTeleportEpoch);
    }

    [Theory]
    [InlineData(100, 0, 100, 103)]
    [InlineData(101, 0, 100, 103)]
    [InlineData(105, 0, 102, 105)]
    [InlineData(105, 108, 105, 108)]
    public void Existing_wait_overlaps_preparation(long ready, long retained, long prepare, long impact)
    {
        var state = new TileCombatPreparationState { ReadyNotBeforeTick = retained };

        Assert.True(Create(ref state, 100, ready, out _));

        Assert.Equal(prepare, state.Active.PrepareTick);
        Assert.Equal(impact, state.Active.ImpactTick);
    }

    [Fact]
    public void An_active_attempt_is_not_overwritten()
    {
        TileCombatPreparationState state = default;
        Assert.True(Create(ref state, 100, 100, out _));
        TileCombatPreparationState before = state;

        Assert.False(TileCombatPreparationScheduler.TryCreate(ref state, 101, 30, 40,
            new TileCombatPreparationProfile(4, 2, 9), 16, 120, 6, 7, out TilePreparationFailure failure));

        Assert.Equal(TilePreparationFailure.None, failure);
        Assert.Equal(before, state);
    }

    [Fact]
    public void Cancellation_retains_readiness()
    {
        TileCombatPreparationState state = default;
        Assert.True(Create(ref state, 100, 100, out _));

        Assert.True(TileCombatPreparationScheduler.TryCancel(ref state, 101,
            TileCombatPreparationEndReason.RulesUnavailable, out CombatPreparationEnded ended));

        Assert.Equal(new CombatPreparationEnded(101, 10, 20, 1, 1, 7, 103,
            TileCombatPreparationEndReason.RulesUnavailable), ended);
        Assert.False(state.HasActive);
        Assert.Equal(103L, state.ReadyNotBeforeTick);
        Assert.Equal(1UL, state.LastAttackId);
        TileCombatPreparationState cancelled = state;
        Assert.False(TileCombatPreparationScheduler.TryCancel(ref state, 102,
            TileCombatPreparationEndReason.Disengaged, out CombatPreparationEnded repeated));
        Assert.Equal(default, repeated);
        Assert.Equal(cancelled, state);

        Assert.True(Create(ref state, 102, 100, out _));
        Assert.Equal(2UL, state.Active.AttackId);
        Assert.Equal(1U, state.Active.Revision);
        Assert.Equal(105L, state.Active.ImpactTick);
    }

    [Fact]
    public void Cancelling_a_late_tick_does_not_extend_the_old_boundary()
    {
        TileCombatPreparationState state = default;
        Assert.True(Create(ref state, 100, 105, out _));

        Assert.True(TileCombatPreparationScheduler.TryCancel(ref state, 110,
            TileCombatPreparationEndReason.ParticipantUnavailable, out _));

        Assert.Equal(105L, state.ReadyNotBeforeTick);
        Assert.True(Create(ref state, 110, 100, out _));
        Assert.Equal(113L, state.Active.ImpactTick);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 9)]
    public void Completion_consumes_the_identity(bool landed, bool killed, ushort amount)
    {
        TileCombatPreparationState state = default;
        Assert.True(Create(ref state, 100, 100, out _));
        var outcome = new TileCombatEvent(10, 20, amount, 6, landed, killed);

        Assert.True(TileCombatPreparationScheduler.TryComplete(ref state, 103, outcome,
            out PreparedCombatEvent completed));

        Assert.Equal(new PreparedCombatEvent(103, 1, 1, 7, outcome), completed);
        Assert.False(state.HasActive);
        Assert.Equal(117L, state.ReadyNotBeforeTick);
        TileCombatPreparationState after = state;
        Assert.False(TileCombatPreparationScheduler.TryComplete(ref state, 103, outcome, out _));
        Assert.Equal(after, state);
        Assert.True(Create(ref state, 103, 117, out _));
        Assert.Equal(new TileCombatPreparation(10, 20, 2, 1, 7, 114, 117, 1, 14), state.Active);
    }

    [Theory]
    [InlineData(102, 10, 20)]
    [InlineData(104, 10, 20)]
    [InlineData(103, 11, 20)]
    [InlineData(103, 10, 21)]
    public void Completion_rejects_the_wrong_tick_or_participants(long tick, long attacker, long target)
    {
        TileCombatPreparationState state = default;
        Assert.True(Create(ref state, 100, 100, out _));
        TileCombatPreparationState before = state;

        Assert.False(TileCombatPreparationScheduler.TryComplete(ref state, tick,
            new TileCombatEvent(attacker, target, 4, 2, true, false), out PreparedCombatEvent completed));

        Assert.Equal(default, completed);
        Assert.Equal(before, state);
    }

    [Fact]
    public void Idle_state_cannot_cancel_or_complete_an_attempt()
    {
        var state = new TileCombatPreparationState { LastAttackId = 8, ReadyNotBeforeTick = 123 };
        TileCombatPreparationState before = state;

        Assert.False(TileCombatPreparationScheduler.TryCancel(ref state, 100,
            TileCombatPreparationEndReason.Disengaged, out CombatPreparationEnded cancelled));
        Assert.False(TileCombatPreparationScheduler.TryComplete(ref state, 100,
            new TileCombatEvent(10, 20, 4, 2, true, false), out PreparedCombatEvent completed));

        Assert.Equal(default, cancelled);
        Assert.Equal(default, completed);
        Assert.Equal(before, state);
    }

    [Fact]
    public void Invalid_profiles_and_exhaustion_never_create_an_attempt()
    {
        foreach (TileCombatPreparationProfile profile in new TileCombatPreparationProfile[]
        {
            new(0, 1, 7), new(3, 0, 7), new(3, 4, 7), new(15, 1, 7)
        })
        {
            var state = new TileCombatPreparationState { LastAttackId = 4, ReadyNotBeforeTick = 108 };
            TileCombatPreparationState before = state;

            Assert.False(TileCombatPreparationScheduler.TryCreate(ref state, 100, 10, 20, profile,
                14, 100, 4, 5, out TilePreparationFailure failure));

            Assert.Equal(TilePreparationFailure.InvalidProfile, failure);
            Assert.False(state.HasActive);
            Assert.Equal(before, state);
        }

        var exhausted = new TileCombatPreparationState { LastAttackId = ulong.MaxValue };
        TileCombatPreparationState prior = exhausted;
        Assert.False(Create(ref exhausted, 100, 100, out TilePreparationFailure exhaustedFailure));
        Assert.Equal(TilePreparationFailure.IdentityExhausted, exhaustedFailure);
        Assert.Equal(prior, exhausted);
    }

    [Theory]
    [InlineData(long.MaxValue, 0)]
    [InlineData(long.MaxValue - 10, 0)]
    [InlineData(100, long.MaxValue)]
    public void Unrepresentable_impact_or_following_readiness_is_refused_without_mutation(long tick, long ready)
    {
        var state = new TileCombatPreparationState { LastAttackId = 4, ReadyNotBeforeTick = 108 };
        TileCombatPreparationState before = state;

        Assert.False(Create(ref state, tick, ready, out TilePreparationFailure failure));

        Assert.Equal(TilePreparationFailure.TickOverflow, failure);
        Assert.Equal(before, state);
    }

    [Fact]
    public void The_last_representable_completion_retains_its_full_cadence()
    {
        TileCombatPreparationState state = default;
        Assert.True(Create(ref state, long.MaxValue - 17, 0, out _));

        Assert.True(TileCombatPreparationScheduler.TryComplete(ref state, long.MaxValue - 14,
            new TileCombatEvent(10, 20, 1, 0, true, false), out _));

        Assert.Equal(long.MaxValue, state.ReadyNotBeforeTick);
    }

    [Fact]
    public void Lead_equal_to_cadence_and_zero_presentation_key_are_valid()
    {
        TileCombatPreparationState state = default;

        Assert.True(TileCombatPreparationScheduler.TryCreate(ref state, 100, 10, 20,
            new TileCombatPreparationProfile(3, 3, 0), 3, 100, 4, 5, out TilePreparationFailure failure));

        Assert.Equal(TilePreparationFailure.None, failure);
        Assert.Equal(new TileCombatPreparation(10, 20, 1, 1, 0, 100, 103, 3, 3), state.Active);
        Assert.Equal(100L, state.Active.StrikeTick);
    }

    static bool Create(ref TileCombatPreparationState state, long tick, long ready,
        out TilePreparationFailure failure) =>
        TileCombatPreparationScheduler.TryCreate(ref state, tick, 10, 20, Profile, 14, ready, 4, 5, out failure);
}
