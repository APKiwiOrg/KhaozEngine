using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

public sealed class UpdateOverlayActionsTests
{
    [Theory]
    [InlineData(UpdateState.Idle, OverlayAction.None)]
    [InlineData(UpdateState.Checking, OverlayAction.None)]
    [InlineData(UpdateState.UpdateAvailable, OverlayAction.Download)]
    [InlineData(UpdateState.Downloading, OverlayAction.None)]
    [InlineData(UpdateState.ReadyToApply, OverlayAction.Apply)]
    [InlineData(UpdateState.Applying, OverlayAction.None)]
    [InlineData(UpdateState.Failed, OverlayAction.Retry)]
    public void ResolveAction_maps_state(UpdateState state, OverlayAction expected) =>
        Assert.Equal(expected, UpdateOverlayActions.ResolveAction(state));

    [Theory]
    [InlineData(UpdateState.Idle, OverlayAction.None)]
    [InlineData(UpdateState.Checking, OverlayAction.None)]
    [InlineData(UpdateState.UpdateAvailable, OverlayAction.Download)]
    [InlineData(UpdateState.Downloading, OverlayAction.None)]
    [InlineData(UpdateState.ReadyToApply, OverlayAction.Apply)]
    [InlineData(UpdateState.Applying, OverlayAction.None)]
    [InlineData(UpdateState.Failed, OverlayAction.Retry)]
    public void ResolveAction_over_a_status_matches_the_state_map_while_retries_remain(
        UpdateState state, OverlayAction expected) =>
        Assert.Equal(expected, UpdateOverlayActions.ResolveAction(new FakeUpdateStatus { State = state }));

    [Fact]
    public void A_spent_apply_budget_stops_offering_the_retry()
    {
        var spent = new FakeUpdateStatus { State = UpdateState.Failed, ApplyAttemptsExhausted = true };

        Assert.Equal(OverlayAction.None, UpdateOverlayActions.ResolveAction(spent));
        // Only the Failed mapping changes: an offer that came back after the failures is still actionable.
        spent.State = UpdateState.UpdateAvailable;
        Assert.Equal(OverlayAction.Download, UpdateOverlayActions.ResolveAction(spent));
        spent.State = UpdateState.ReadyToApply;
        Assert.Equal(OverlayAction.Apply, UpdateOverlayActions.ResolveAction(spent));
    }
}
