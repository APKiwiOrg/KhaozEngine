using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Windowing;

/// <summary>Stable-ID touch ownership, centroid transforms and tap arbitration.</summary>
internal sealed class TouchPanZoomGesture
{
    readonly Dictionary<long, Vector2> _previous = new();
    readonly Dictionary<long, Vector2> _origins = new();
    readonly Dictionary<long, Vector2> _latest = new();
    DragMotionTracker? _drag;
    bool _admitted, _everPair, _twoTapEligible, _pairCommitted;
    double _duration;
    Vector2 _twoTapCentre;

    internal bool IsActive { get; private set; }
    internal bool Started { get; private set; }
    internal bool Tapped { get; private set; }
    internal Vector2 TapPosition { get; private set; }
    internal Vector2 ReleaseVelocity { get; private set; }

    internal void Reset()
    {
        _previous.Clear();
        _origins.Clear();
        _latest.Clear();
        _drag = null;
        IsActive = Started = Tapped = _admitted = _everPair = _twoTapEligible = _pairCommitted = false;
        ReleaseVelocity = Vector2.Zero;
        _duration = 0;
    }

    internal void Update(IReadOnlyList<TouchPoint> touches, PointerCoordinates coordinates,
        PanZoomOptions options, float dtSeconds, Func<Vector2, bool> admits,
        Action<Vector2> pan, Action<float, Vector2> zoom)
    {
        Started = Tapped = false;
        ReleaseVelocity = Vector2.Zero;
        var positions = new Dictionary<long, Vector2>();
        var current = new Dictionary<long, Vector2>();
        var begun = new HashSet<long>();
        bool frameValid = true;
        int activeContacts = 0;
        foreach (TouchPoint contact in touches)
        {
            if (contact.Phase != TouchPhase.Ended) activeContacts++;
            Vector2 position = coordinates.PositionFromPixels(contact.Position);
            if (!DragMotionTracker.IsFinite(position) || !positions.TryAdd(contact.Id, position))
            {
                frameValid = false;
                continue;
            }
            if (contact.Phase == TouchPhase.Began) begun.Add(contact.Id);
            if (contact.Phase != TouchPhase.Ended) current.Add(contact.Id, position);
        }
        _admitted &= frameValid;
        _twoTapEligible &= frameValid;
        if (!IsActive)
        {
            if (activeContacts == 0) return;
            Started = IsActive = true;
            _admitted = frameValid && activeContacts <= 2;
            foreach (var point in current)
            {
                _admitted &= begun.Contains(point.Key) && admits(point.Value);
                _origins[point.Key] = _latest[point.Key] = point.Value;
            }
            _everPair = current.Count == 2;
            _twoTapEligible = _everPair && _admitted;
            if (current.Count == 1) BeginSingle(current, options, false);
            SetPrevious(current);
            UpdateTapCentre();
            return;
        }

        _duration += dtSeconds;
        foreach (var point in current)
        {
            if (_previous.ContainsKey(point.Key)) continue;
            bool replacement = _origins.ContainsKey(point.Key);
            _admitted &= begun.Contains(point.Key) && admits(point.Value);
            _origins[point.Key] = point.Value;
            _latest[point.Key] = point.Value;
            if (!replacement && !_everPair && current.Count == 2)
                _twoTapEligible = _admitted && _duration <= 0.1f && (_drag?.TapEligible ?? true);
            else _twoTapEligible = false;
        }
        foreach (var point in positions)
        {
            if (!_origins.TryGetValue(point.Key, out Vector2 origin)) continue;
            _latest[point.Key] = point.Value;
            if (Vector2.Distance(point.Value, origin) > options.DragSlop)
            {
                _twoTapEligible = false;
                if (_everPair || current.Count == 2) _pairCommitted = true;
            }
        }
        if (current.Count > 2) _admitted = _twoTapEligible = false;
        _twoTapEligible &= _duration <= options.TapMaxDuration && _origins.Count == 2;
        UpdateTapCentre();

        bool sameSingle = _previous.Count == 1 && current.Count == 1
            && ContainsSameIds(_previous, current);
        if (_admitted && _previous.Count == 2 && ContainsSameIds(_previous, positions))
            ApplyPair(positions, pan, zoom);
        else if (_admitted && sameSingle)
        {
            Vector2 position = First(current);
            _drag!.Move(position, dtSeconds);
            pan(_drag.DragDelta);
        }

        if (activeContacts == 0)
        {
            if (_admitted && _twoTapEligible) zoom(0.5f, _twoTapCentre);
            else if (_admitted && _previous.Count == 1 && _drag != null)
            {
                long id = FirstId(_previous);
                Vector2 final = positions.TryGetValue(id, out Vector2 ended) ? ended : _previous[id];
                _drag.Release(final, dtSeconds);
                pan(_drag.DragDelta);
                ReleaseVelocity = _drag.ReleaseVelocity;
                Tapped = !_everPair && _origins.Count == 1 && _drag.TapEligible;
                TapPosition = final;
            }
            IsActive = false;
            _previous.Clear();
            _origins.Clear();
            _latest.Clear();
            _duration = 0;
            _drag = null;
            _everPair = _twoTapEligible = _pairCommitted = false;
            return;
        }

        if (current.Count == 2)
        {
            _everPair = true;
            _drag = null;
        }
        else if (!sameSingle) BeginSingle(current, options, _pairCommitted);
        SetPrevious(current);
    }

    void ApplyPair(Dictionary<long, Vector2> positions, Action<Vector2> pan, Action<float, Vector2> zoom)
    {
        var ids = new List<long>(_previous.Keys);
        ids.Sort();
        Vector2 a = _previous[ids[0]], b = _previous[ids[1]];
        Vector2 nextA = positions[ids[0]], nextB = positions[ids[1]];
        float oldDistance = Vector2.Distance(a, b), nextDistance = Vector2.Distance(nextA, nextB);
        if (!float.IsFinite(oldDistance) || !float.IsFinite(nextDistance)
            || oldDistance <= 1e-5f || nextDistance <= 1e-5f) return;
        Vector2 centre = a * 0.5f + b * 0.5f;
        Vector2 nextCentre = nextA * 0.5f + nextB * 0.5f;
        float factor = nextDistance / oldDistance;
        Vector2 delta = nextCentre - centre;
        bool zooms = float.IsFinite(factor) && factor > 0 && factor != 1 && DragMotionTracker.IsFinite(centre);
        bool pans = DragMotionTracker.IsFinite(delta) && delta != Vector2.Zero;
        if (!zooms && !pans) return;
        _pairCommitted = true;
        _twoTapEligible = false;
        if (zooms) zoom(factor, centre);
        if (pans) pan(delta);
    }

    void BeginSingle(Dictionary<long, Vector2> current, PanZoomOptions options, bool alreadyDragging)
    {
        if (current.Count != 1) return;
        _drag = new(options.DragSlop, options.TapMaxDuration, options.StationaryReleaseCutoff);
        _drag.Begin(First(current), alreadyDragging);
    }

    void SetPrevious(Dictionary<long, Vector2> current)
    {
        _previous.Clear();
        foreach (var point in current) _previous.Add(point.Key, point.Value);
    }

    void UpdateTapCentre()
    {
        if (_latest.Count != 2) return;
        _twoTapCentre = Vector2.Zero;
        foreach (Vector2 position in _latest.Values) _twoTapCentre += position * 0.5f;
    }

    static bool ContainsSameIds(Dictionary<long, Vector2> previous, Dictionary<long, Vector2> current)
    {
        foreach (long id in previous.Keys) if (!current.ContainsKey(id)) return false;
        return true;
    }

    static long FirstId(Dictionary<long, Vector2> points)
    {
        foreach (long id in points.Keys) return id;
        throw new InvalidOperationException("No touch contact.");
    }

    static Vector2 First(Dictionary<long, Vector2> points) => points[FirstId(points)];
}
