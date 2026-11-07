using System.Numerics;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

/// <summary>The two real Navigation host contracts, sharing Movement's ordinary Drop steering.</summary>
internal sealed class ContactTerrainCommandDriver
{
    readonly GroundNavigation _navigation;
    readonly MoveToRange? _region;
    readonly PathFollower? _point;
    ContactDropFollower? _drop;
    internal int CompletedDrops { get; private set; }
    internal NavPath? ActivePath => _point?.ActivePath ?? _region?.ActivePathForTests;
    internal bool HasPendingDrop => ActivePath?.Waypoints.Skip(_point?.ActiveWaypointIndex ?? _region!.ActiveWaypointIndexForTests)
        .Any(waypoint => waypoint.Kind == NavWaypointKind.Drop) == true;

    internal ContactTerrainCommandDriver(GroundNavigation navigation, bool point, float acceptRadius = 0.00001f)
    {
        _navigation = navigation;
        var config = new PathFollowConfig
        {
            AcceptRadius = acceptRadius,
            VerticalAcceptTolerance = 0.001f,
            GoalRetargetTolerance = 0.1f,
            ReplanCooldownSeconds = 30
        };
        if (point) _point = new(navigation.Planner, config, navigation.Space);
        else _region = new(navigation, config);
    }

    internal RangeSteering Tick(MoveState body, MoveTuning tuning, Vector3 goalFeet, bool run,
        GroundMoveContext context, PlayerPathInput input)
    {
        if (_region is not null)
            return _region.Tick(body, tuning, ReachTarget.Point(goalFeet + Vector3.UnitY * tuning.CapsuleHalfHeight),
                0, run, ContactTerrainScene.Dt, context, input);
        var feet = body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight;
        float elapsed = ContactTerrainScene.Dt;
        bool preparing = _drop is { Preparing: true } && body.Grounded && !body.Swimming;
        if (_drop is not null) { _point!.AdvanceDropTime(elapsed); elapsed = 0; }
        var route = preparing ? _point!.TickDropPreparation(feet, goalFeet, tuning.CapsuleRadius)
            : _point!.Tick(feet, goalFeet, tuning.CapsuleRadius, elapsed);
        if (route.State == PathFollowState.Dropping)
        {
            if (_drop is null || _drop.Traversal != route.DropTraversal!.Value) _drop = new(_navigation, route.DropTraversal!.Value);
            var result = _drop.Tick(body, tuning, run, ContactTerrainScene.Dt, context, input, out bool complete, out bool replan);
            if (complete) { _point!.CompleteDrop(); _drop = null; CompletedDrops++; }
            if (replan) { _point!.Reset(); _drop = null; }
            return result;
        }
        _drop = null;
        if (route.State == PathFollowState.Arrived) return new(Vector2.Zero, RangeMoveStatus.InRange);
        if (route.State != PathFollowState.Following) return new(Vector2.Zero, RangeMoveStatus.Unreachable);
        Vector2 offset = route.ActiveWaypoint - new Vector2(feet.X, feet.Z);
        float bound = RangeApproachCore.TravelBound(body, tuning, run, ContactTerrainScene.Dt, context);
        return new(RangeApproachCore.BoundedDirection(offset, bound), RangeMoveStatus.Following);
    }

    internal void Reset() { _region?.Reset(); _point?.Reset(); _drop = null; }
}
