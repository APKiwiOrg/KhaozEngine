using System;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

/// <summary>
/// The rules every route request keeps whatever planner made it: no result before Ready, a finished request
/// spends nothing more, Cancel and Dispose are idempotent and release once, and a quota below one unit is
/// refused. The double does only the work it is told to, so each rule here is the base request's.
/// </summary>
public class PathPlanRequestContractTests
{
    static readonly NavPath Route = new(NavPathStatus.Complete, new[] { new NavWaypoint(Vector2.One, 0) });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResultIsNullUntilReady(bool unreachable)
    {
        NavPath result = unreachable ? NavPath.Unreachable : Route;
        using var request = new FixedWorkRequest(3, result);
        Assert.False(request.Released);
        Assert.Equal(PathPlanState.Pending, request.State);
        Assert.Null(request.Result);

        request.Advance(new PathWorkBudget(2));
        Assert.Equal(PathPlanState.Pending, request.State);
        Assert.Null(request.Result);
        Assert.Equal(2, request.WorkSpent);
        Assert.False(request.Released);

        // Unreachable is a completed request like any other result.
        request.Advance(new PathWorkBudget(2));
        Assert.Equal(PathPlanState.Ready, request.State);
        Assert.Same(result, request.Result);
        Assert.Equal(3, request.WorkSpent);
        Assert.True(request.Released);
        request.Cancel();
        request.Dispose();
        Assert.Same(result, request.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdvanceAfterReadyOrCancelSpendsNothing(bool cancel)
    {
        using var request = new FixedWorkRequest(cancel ? 5 : 1, Route);
        request.Advance(new PathWorkBudget(1));
        if (cancel) request.Cancel();
        Assert.Equal(cancel ? PathPlanState.Cancelled : PathPlanState.Ready, request.State);
        NavPath? result = request.Result;

        request.Advance(new PathWorkBudget(8));
        Assert.Equal(cancel ? PathPlanState.Cancelled : PathPlanState.Ready, request.State);
        Assert.Same(result, request.Result);
        Assert.Equal(1, request.WorkSpent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelAndDisposeReleaseOnce(bool dispose)
    {
        // The double refuses a second release, so all later Cancel and Dispose calls must be harmless.
        using var request = new FixedWorkRequest(5, Route);
        request.Advance(new PathWorkBudget(1));
        Assert.False(request.Released);

        if (dispose) request.Dispose();
        else request.Cancel();
        Assert.True(request.Released);

        request.Cancel();
        request.Dispose();
        Assert.Equal(PathPlanState.Cancelled, request.State);
        Assert.Null(request.Result);
        Assert.Equal(1, request.WorkSpent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnOperationMustSpendPositiveWork(int units)
    {
        using var request = new FixedWorkRequest(3, Route);
        Assert.Throws<ArgumentOutOfRangeException>(() => request.RecordWork(units));
        Assert.Equal(0, request.WorkSpent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOperationCannotSpendWorkAfterReadyOrCancel(bool cancel)
    {
        using var request = new FixedWorkRequest(cancel ? 5 : 1, Route);
        request.Advance(new PathWorkBudget(1));
        if (cancel) request.Cancel();

        Assert.Throws<InvalidOperationException>(() => request.RecordWork(1));
        Assert.Equal(1, request.WorkSpent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void BudgetBelowOneIsRefused(int units)
    {
        Assert.Equal(1, new PathWorkBudget(1).Units);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PathWorkBudget(units));
    }

    [Fact]
    public void DefaultBudgetIsRefusedAndSpendsNothing()
    {
        using var request = new FixedWorkRequest(3, Route);
        Assert.Throws<ArgumentOutOfRangeException>(() => request.Advance(default));
        Assert.Equal(PathPlanState.Pending, request.State);
        Assert.Equal(0, request.WorkSpent);

        request.Advance(new PathWorkBudget(1));
        Assert.Equal(1, request.WorkSpent);
    }

    /// <summary>Finishes after a fixed number of one-unit operations and refuses a second release.</summary>
    sealed class FixedWorkRequest(int units, NavPath result) : PathPlanRequest
    {
        int _left = units;
        bool _released;

        internal bool Released => _released;

        internal void RecordWork(int units) => Spend(units);

        public override bool StartsFrom(Vector3 feet) => feet == Vector3.Zero;

        private protected override void AdvanceCore(PathWorkBudget budget)
        {
            int spend = Math.Min(budget.Units, _left);
            _left -= spend;
            Spend(spend);
            if (_left == 0) Complete(result);
        }

        private protected override void Release()
        {
            if (_released) throw new InvalidOperationException("A request releases its workspace once.");
            _released = true;
        }
    }
}
