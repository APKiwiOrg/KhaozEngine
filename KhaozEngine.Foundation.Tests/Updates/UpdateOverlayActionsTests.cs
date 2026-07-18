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
}
