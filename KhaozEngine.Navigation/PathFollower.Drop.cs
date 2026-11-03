using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class PathFollower
{
    NavDropTraversal? _drop;

    /// <summary>Advances the active lock's clock without selecting a route or changing its landing.
    /// A wrapper that resumes following in this tick must pass zero elapsed time to that selection.</summary>
    internal void AdvanceDropTime(float dt)
    {
        if (_drop is null) throw new InvalidOperationException("No active Drop to advance.");
        _cooldown = MathF.Max(0f, _cooldown - dt);
    }

    /// <summary>Reconsiders a host's still-supported preparation through the ordinary query site.
    /// The host must advance the clock once first and must never call this after issuing departure.
    /// Membership or a due refresh may release the untraversed lock without acknowledging a landing.</summary>
    internal PathFollowOutput TickDropPreparation(Vector3 feet, NavGoalRegion goal, float agentRadius)
    {
        if (_drop is null) throw new InvalidOperationException("No active Drop to prepare.");
        return TickCore(feet, goal.Anchor, agentRadius, 0f, goal, preparingDrop: true);
    }

    /// <summary>Acknowledges the active Drop's supported landing. The host must verify actual non-swimming
    /// support at the named landing with its movement controller. Advances exactly this transition and permits
    /// subsequent goal retargeting. A final Complete Drop remains a reached-terminal witness for the next
    /// tick's current region membership or point-intent check. Navigation itself reads no physics.</summary>
    /// <exception cref="InvalidOperationException">There is no active Drop to acknowledge.</exception>
    public void CompleteDrop()
    {
        if (_drop is null) throw new InvalidOperationException("No active Drop to complete.");
        _index++;
        _drop = null;
    }

    bool HasPendingDrop()
    {
        if (_path is null) return false;
        for (int i = _index; i < _path.Waypoints.Count; i++)
            if (_path.Waypoints[i].Kind == NavWaypointKind.Drop) return true;
        return false;
    }

    // Whether this tick's waypoint advance stops at an unlocked Drop. Reads without consuming, so a point
    // Partial route keeps its consumption after query eligibility.
    bool DropIsNext(Vector3 position, Vector2 posXz)
    {
        if (_path is null || _path.Status == NavPathStatus.Unreachable) return false;
        IReadOnlyList<NavWaypoint> waypoints = _path.Waypoints;
        int? agentLayer = _space?.LayerAt(position);
        int index = _index;
        while (index < waypoints.Count && waypoints[index].Kind != NavWaypointKind.Drop && Reached(index, posXz, agentLayer))
            index++;
        return index < waypoints.Count && waypoints[index].Kind == NavWaypointKind.Drop;
    }

    bool TryDrop(out PathFollowOutput output)
    {
        output = default;
        if (_path is null || _index >= _path.Waypoints.Count ||
            _path.Waypoints[_index].Kind != NavWaypointKind.Drop) return false;
        if (_space is null || _index == 0)
            throw new InvalidOperationException("A Drop requires a physical NavSpace and a mandatory source waypoint.");
        NavWaypoint source = _path.Waypoints[_index - 1], landing = _path.Waypoints[_index];
        NavGrid from = _space.Layers[source.Layer], to = _space.Layers[landing.Layer];
        var (sx, sz) = from.CellOf(source.Position.X, source.Position.Y);
        var (tx, tz) = to.CellOf(landing.Position.X, landing.Position.Y);
        float sy = from.RestingHeightAt(sx, sz) ?? throw new InvalidOperationException("Drop source has no physical height.");
        float ty = to.RestingHeightAt(tx, tz) ?? throw new InvalidOperationException("Drop landing has no physical height.");
        Vector2 start = from.CellCenter(sx, sz), end = to.CellCenter(tx, tz);
        var link = new NavLink(source.Layer, sx, sz, landing.Layer, tx, tz) { Kind = NavLinkKind.Drop };
        _drop = new(link, new(start.X, sy, start.Y), new(end.X, ty, end.Y));
        output = DropOutput();
        return true;
    }

    PathFollowOutput DropOutput() => new()
    {
        State = PathFollowState.Dropping,
        ActiveWaypoint = new(_drop!.Value.LandingFeet.X, _drop.Value.LandingFeet.Z),
        DropTraversal = _drop,
    };
}
