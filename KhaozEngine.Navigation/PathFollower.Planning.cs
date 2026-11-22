using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Navigation;

public sealed partial class PathFollower
{
    // Value-type specializations compile out request work on the synchronous path. The mover shares
    // the selection so neither one tests the option inside a waypoint loop or twice on a mover tick.
    /// <summary>Tick specialization for synchronous queries.</summary>
    internal readonly struct SynchronousPlanning;

    /// <summary>Tick specialization that also handles fixed-quota region requests.</summary>
    internal readonly struct IncrementalPlanning;

    Vector3 _requestOrigin;
    Vector3 _requestAnchor;

    /// <summary>Whether this follower uses requests for region planning.</summary>
    internal bool UsesPlanning => _config.Planning is not null;

    /// <summary>True while waiting for a lease, spending slices, or holding a finished result for adoption.</summary>
    internal bool PlanningPending => _request is { State: not PathPlanState.Cancelled };

    /// <summary>Releases a leased or waiting request and drops any ready result without resetting the route.</summary>
    internal void CancelPlanning()
    {
        if (_request is null) return;
        _request.Cancel();
        _request = null;
    }

    /// <summary>Resets from a tick that has already selected its planning mode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Reset<TPlanning>() where TPlanning : struct
    {
        if (typeof(TPlanning) == typeof(IncrementalPlanning)) CancelPlanning();
        ResetState();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    PathFollowOutput TickCore(Vector3 position, Vector3 goal, float agentRadius, float dt, NavGoalRegion? region,
        bool preparingDrop = false)
        => UsesPlanning
            ? TickCore<IncrementalPlanning>(position, goal, agentRadius, dt, region, preparingDrop)
            : TickCore<SynchronousPlanning>(position, goal, agentRadius, dt, region, preparingDrop);

    /// <summary>Ticks a region caller using the planning mode it already selected.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PathFollowOutput TickRegion<TPlanning>(Vector3 feet, NavGoalRegion goal, float radius, float dt)
        where TPlanning : struct
    {
        ValidateRegionTick(goal);
        return TickCore<TPlanning>(feet, goal.Anchor, radius, dt, goal, preparingDrop: false);
    }

    /// <summary>Ticks the supported preparation of a caller that already selected its planning mode.
    /// The host must have advanced the Drop cooldown once.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PathFollowOutput TickDropPreparation<TPlanning>(Vector3 feet, NavGoalRegion goal, float radius)
        where TPlanning : struct
    {
        if (_drop is null) throw new InvalidOperationException("No active Drop to prepare.");
        return TickCore<TPlanning>(feet, goal.Anchor, radius, 0f, goal, preparingDrop: true);
    }

    void ValidatePlanning()
    {
        if (_config.Planning is not { } planning) return;
        if (_planner is not IIncrementalRegionPathPlanner { SupportsRequests: true } incremental)
            throw new ArgumentException("Planning needs a region planner that supports requests.", "config");
        incremental.ValidateWorkspaces(planning.Workspaces, _config.Budget);
    }

    bool BeginPlanning(Vector3 position, NavGoalRegion region, float radius)
    {
        PathPlanningConfig planning = _config.Planning!;
        PathPlanRequest request = ((IIncrementalRegionPathPlanner)_planner)
            .BeginPath(position, region, radius, _config.Budget, planning.Workspaces);
        _request = request;
        _requestOrigin = position;
        _requestAnchor = region.Anchor;
        _path = null;
        _index = 0;
        _drop = null;
        _freshRegionTerminal = false;
        request.Advance(planning.Quota);
        if (request.Result is not { } path) return false;

        Adopt(path);
        return true;
    }

    bool ContinuePlanning(Vector3 position, NavGoalRegion region, float radius)
    {
        PathPlanRequest request = _request!;
        if (request.State == PathPlanState.Pending)
        {
            request.Advance(_config.Planning!.Quota);
            // Even its final slice holds. The next owner call decides whether to adopt.
            return false;
        }
        if (request.Result is { } path && request.StartsFrom(position))
        {
            Adopt(path);
            return true;
        }

        _request = null;
        return BeginPlanning(position, region, radius);
    }

    void Adopt(NavPath path)
    {
        _request = null;
        _path = path;
        _freshRegionTerminal = false;
        _index = 0;
        _plannedGoalXz = new(_requestAnchor.X, _requestAnchor.Z);
        _plannedGoalY = _requestAnchor.Y;
        _planOriginXz = new(_requestOrigin.X, _requestOrigin.Z);
        _cooldown = _config.ReplanCooldownSeconds;
    }

    static PathFollowOutput HoldForPlanning() => new() { State = PathFollowState.WaitingForPath };
}
