using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;
using static KhaozEngine.Movement.RouteStraightener;

namespace KhaozEngine.Movement;

/// <summary>The next paid straightening operation. Segment delegates one charge to the current admission cursor.</summary>
internal enum RouteStraighteningOperation
{
    Done,
    ValidateFeet,
    Candidate,
    Segment,
    SideOffsets,
    KeepAnchor,
    Allocate,
    Copy,
}

/// <summary>Resumes a straightener after the search with its raw path and prewarmed waypoint storage.</summary>
internal struct RouteStraighteningCursor
{
    enum GuardLine { Centre, Plus, Minus }

    readonly RouteStraightener _straightener;
    readonly GroundNavigation _navigation;
    readonly int _startLayer;
    readonly NavPath _raw;
    readonly IReadOnlyList<NavWaypoint> _waypoints;
    readonly List<NavWaypoint> _kept;
    readonly int _count;

    int _validation, _anchor, _anchorLayer, _best, _candidate, _copy;
    Vector3 _anchorFeet, _candidateFeet;
    double _ox, _oz;
    bool _anchorKnown;
    GuardLine _line;
    NavSegmentRequest _segment;
    NavWaypoint[]? _array;

    internal RouteStraighteningCursor(RouteStraightener straightener, GroundNavigation navigation, Vector3 start,
        int startLayer, NavPath raw, bool fallBack, List<NavWaypoint> scratch)
    {
        _straightener = straightener;
        _navigation = navigation;
        _startLayer = startLayer;
        _raw = raw;
        _waypoints = raw.Waypoints;
        _count = _waypoints.Count;
        _kept = scratch;

        if (fallBack || !CanStraighten(raw) || !HasShortcutCandidates(_count))
        {
            Result = raw;
            return;
        }
        _kept.Clear();

        _anchor = -1;
        _anchorLayer = startLayer;
        _anchorFeet = start;
        _anchorKnown = true;
        NextOperation = RouteStraighteningOperation.ValidateFeet;
    }

    internal RouteStraighteningOperation NextOperation { get; private set; }

    internal NavSegmentOperation NextSegmentOperation => _segment.NextOperation;

    internal bool HasStagedArray => _array is not null;

    internal NavPath? Result { get; private set; }

    /// <summary>Joins the profile's current slice. Its first-operation allowance may already have been used.</summary>
    internal void RunSlice(int quota, ref int spent)
    {
        while (NextOperation != RouteStraighteningOperation.Done)
        {
            int weight = NextWeight();
            if (spent != 0 && weight > quota - spent) return;
            Step();
            spent += weight;
        }
    }

    int NextWeight() => NextOperation switch
    {
        RouteStraighteningOperation.ValidateFeet or RouteStraighteningOperation.KeepAnchor => PathWorkWeights.StraighteningFeet,
        RouteStraighteningOperation.Candidate => PathWorkWeights.StraighteningCandidate,
        RouteStraighteningOperation.Segment => NavSegmentRequest.WeightOf(_segment.NextOperation),
        RouteStraighteningOperation.SideOffsets => PathWorkWeights.StraighteningOffsets,
        RouteStraighteningOperation.Allocate => PathWorkWeights.StraighteningAllocate,
        RouteStraighteningOperation.Copy => PathWorkWeights.StraighteningCopy,
        _ => throw new InvalidOperationException("A finished straightener has no next operation."),
    };

    void Step()
    {
        switch (NextOperation)
        {
            case RouteStraighteningOperation.ValidateFeet: ValidateFeet(); break;
            case RouteStraighteningOperation.Candidate: TestCandidate(); break;
            case RouteStraighteningOperation.Segment: TestSegment(); break;
            case RouteStraighteningOperation.SideOffsets: StartSides(); break;
            case RouteStraighteningOperation.KeepAnchor: KeepAnchor(); break;
            case RouteStraighteningOperation.Allocate:
                _array = new NavWaypoint[_kept.Count];
                NextOperation = RouteStraighteningOperation.Copy;
                break;
            case RouteStraighteningOperation.Copy:
                NavWaypoint[] array = _array!;
                array[_copy] = _kept[_copy];
                if (++_copy == array.Length) Complete(new NavPath(_raw.Status, array));
                break;
        }
    }

    void ValidateFeet()
    {
        if (!_straightener.TryFeet(_waypoints[_validation], out _)) Complete(_raw);
        else if (++_validation == _count) StartAnchor();
    }

    void StartAnchor()
    {
        _best = NextEdge(_anchor);
        _candidate = FirstShortcut(_anchor);
        SelectCandidate();
    }

    void SelectCandidate()
        => NextOperation = _candidate < _count && _anchorKnown
            ? RouteStraighteningOperation.Candidate : RouteStraighteningOperation.KeepAnchor;

    void TestCandidate()
    {
        if (Mandatory(_waypoints, _best, _startLayer) || !IsWalk(_waypoints[_candidate]) ||
            !OnLayer(_waypoints[_candidate], _anchorLayer) ||
            !_straightener.TryFeet(_waypoints[_candidate], out _candidateFeet))
        {
            NextOperation = RouteStraighteningOperation.KeepAnchor;
            return;
        }

        StartGuard(GuardLine.Centre, _anchorFeet, _candidateFeet);
    }

    void StartGuard(GuardLine line, Vector3 from, Vector3 to)
    {
        _line = line;
        _segment = new NavSegmentRequest(_navigation, from, to);
        NextOperation = RouteStraighteningOperation.Segment;
    }

    void TestSegment()
    {
        _segment.Step();
        if (_segment.Result is not bool admitted) return;
        if (!admitted)
        {
            NextOperation = RouteStraighteningOperation.KeepAnchor;
            return;
        }

        switch (_line)
        {
            case GuardLine.Centre:
                NextOperation = RouteStraighteningOperation.SideOffsets;
                break;
            case GuardLine.Plus:
                StartGuard(GuardLine.Minus, Offset(_anchorFeet, -_ox, -_oz), Offset(_candidateFeet, -_ox, -_oz));
                break;
            case GuardLine.Minus:
                AcceptCandidate();
                break;
        }
    }

    void StartSides()
    {
        double dx = Delta(_candidateFeet.X, _anchorFeet.X), dz = Delta(_candidateFeet.Z, _anchorFeet.Z);
        double length = SideLength(dx, dz);
        if (NoSideLines(length))
        {
            AcceptCandidate();
            return;
        }

        double scale = _straightener.SideScale(_anchorLayer, length);
        _ox = SideOffsetX(dz, scale);
        _oz = SideOffsetZ(dx, scale);
        StartGuard(GuardLine.Plus, Offset(_anchorFeet, _ox, _oz), Offset(_candidateFeet, _ox, _oz));
    }

    void AcceptCandidate()
    {
        _best = _candidate++;
        SelectCandidate();
    }

    void KeepAnchor()
    {
        NavWaypoint waypoint = _waypoints[_best];
        _kept.Add(waypoint);
        _anchor = _best;
        _anchorKnown = _straightener.TryFeet(waypoint, out _anchorFeet);
        _anchorLayer = waypoint.Layer;

        if (_anchor < _count - 1) StartAnchor();
        else if (DroppedWaypoints(_kept.Count, _count)) NextOperation = RouteStraighteningOperation.Allocate;
        else Complete(_raw);
    }

    void Complete(NavPath result)
    {
        Result = result;
        NextOperation = RouteStraighteningOperation.Done;
    }
}
