using System;
using System.Numerics;
using KhaozEngine.Navigation;
using static KhaozEngine.Movement.GroundNavigation;

namespace KhaozEngine.Movement;

/// <summary>One indivisible effort in directed segment admission, or Done when no effort remains.</summary>
internal enum NavSegmentOperation
{
    EndpointStart,
    FootprintRow,
    FootprintColumn,
    EndpointLayer,
    SegmentStart,
    CellCrossing,
    Done,
}

/// <summary>Resumes a single segment through its from endpoint, to endpoint, swept footprint and directed crossings.
/// Returns its decision only after all required checks finish.</summary>
internal struct NavSegmentRequest(GroundNavigation navigation, Vector3 from, Vector3 to)
{
    enum Stage { From, To, SweepStart, Sweep, Crossing, Done }

    Stage _stage;
    NavEndpointRequest _endpoint = new(navigation, from);
    NavFootprintRequest _sweep;
    Node _from, _to;
    int _x, _z, _sx, _sz;
    double _nextX, _nextZ, _strideX, _strideZ;

    internal bool? Result { get; private set; }

    internal long WorkSpent { get; private set; }

    /// <summary>The next operation, exposed for a caller combining search and segment work in one slice.</summary>
    internal NavSegmentOperation NextOperation => _stage switch
    {
        Stage.From or Stage.To => _endpoint.NextOperation,
        Stage.SweepStart => NavSegmentOperation.SegmentStart,
        Stage.Sweep => _sweep.NextOperation,
        Stage.Crossing => NavSegmentOperation.CellCrossing,
        _ => NavSegmentOperation.Done,
    };

    /// <summary>Spends whole operations up to the quota, allowing the first operation by itself to exceed it.</summary>
    internal void Advance(int quota)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quota, 1);
        int spent = 0;
        try
        {
            while (NextOperation != NavSegmentOperation.Done)
            {
                int weight = WeightOf(NextOperation);
                if (spent != 0 && weight > quota - spent) return;
                Step();
                spent += weight;
            }
        }
        catch
        {
            _stage = Stage.Done;
            throw;
        }
        finally
        {
            WorkSpent += spent;
        }
    }

    /// <summary>The fixed charge for an admission operation, shared with profile start resolution.</summary>
    internal static int WeightOf(NavSegmentOperation operation) => operation switch
    {
        NavSegmentOperation.EndpointStart or NavSegmentOperation.SegmentStart => PathWorkWeights.FootprintSetup,
        NavSegmentOperation.FootprintRow => PathWorkWeights.FootprintRow,
        NavSegmentOperation.FootprintColumn => PathWorkWeights.FootprintColumn,
        NavSegmentOperation.EndpointLayer => PathWorkWeights.EndpointLayer,
        NavSegmentOperation.CellCrossing => PathWorkWeights.CellCrossing,
        _ => throw new InvalidOperationException("A finished segment has no next operation."),
    };

    /// <summary>Runs one operation after the owner admits its charge.</summary>
    internal void Step()
    {
        switch (_stage)
        {
            case Stage.From:
            case Stage.To:
                StepEndpoint();
                break;
            case Stage.SweepStart:
                _sweep = new NavFootprintRequest(navigation.Footprint, from, to);
                _sweep.Start();
                FinishSweep();
                break;
            case Stage.Sweep:
                _sweep.Step();
                FinishSweep();
                break;
            case Stage.Crossing:
                StepCrossing();
                break;
        }
    }

    void StepEndpoint()
    {
        _endpoint.Step();
        if (_endpoint.NextOperation != NavSegmentOperation.Done) return;
        if (_endpoint.Result is not Node node)
        {
            Complete(false);
            return;
        }

        if (_stage == Stage.From)
        {
            _from = node;
            _endpoint = new NavEndpointRequest(navigation, to);
            _stage = Stage.To;
        }
        else
        {
            _to = node;
            _stage = Stage.SweepStart;
        }
    }

    void FinishSweep()
    {
        if (_sweep.NextOperation != NavSegmentOperation.Done)
        {
            _stage = Stage.Sweep;
            return;
        }
        if (!_sweep.Accepted)
        {
            Complete(false);
            return;
        }

        _stage = Stage.Crossing;
        if (_from.Layer != _to.Layer) return;
        NavGrid grid = navigation.Space.Layers[_from.Layer];
        _x = _from.X;
        _z = _from.Z;
        double dx = (double)to.X - from.X, dz = (double)to.Z - from.Z;
        _sx = Math.Sign(dx);
        _sz = Math.Sign(dz);
        _nextX = SameCell(_x, _to.X) ? double.PositiveInfinity : Crossing(from.X, dx, grid.OriginX, grid.CellSize, _x, _sx);
        _nextZ = SameCell(_z, _to.Z) ? double.PositiveInfinity : Crossing(from.Z, dz, grid.OriginZ, grid.CellSize, _z, _sz);
        _strideX = CrossingStride(grid, dx, _sx);
        _strideZ = CrossingStride(grid, dz, _sz);
        if (_x == _to.X && _z == _to.Z) Complete(true);
    }

    void StepCrossing()
    {
        if (_from.Layer != _to.Layer)
        {
            Complete(navigation.Graph.CanWalk(_from.Layer, _from.X, _from.Z, _to.Layer, _to.X, _to.Z));
            return;
        }

        NextCrossing(_x, _z, _sx, _sz, _nextX, _nextZ, out int nx, out int nz);
        if (!navigation.Graph.CanWalk(_from.Layer, _x, _z, _from.Layer, nx, nz))
        {
            Complete(false);
            return;
        }

        if (!SameCell(nx, _x)) _nextX = AfterCrossing(nx, _to.X, _nextX, _strideX);
        if (!SameCell(nz, _z)) _nextZ = AfterCrossing(nz, _to.Z, _nextZ, _strideZ);
        _x = nx;
        _z = nz;
        if (_x == _to.X && _z == _to.Z) Complete(true);
    }

    void Complete(bool admitted)
    {
        Result = admitted;
        _stage = Stage.Done;
    }
}
