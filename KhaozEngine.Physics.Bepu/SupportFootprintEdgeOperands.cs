using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

internal readonly record struct SupportFootprintEdgeOperands(FeatureNumber LengthSquared, FeatureNumber Dot,
    FeatureNumber Cross, FeatureNumber ProjectedDistanceSquared, FeatureNumber DeltaX, FeatureNumber DeltaZ);

/// <summary>Algebra operands scoped to one polygon materialization and one exact footprint request.
/// The admission disc, support disc and two proposals retain their own radii, strata and decisions.</summary>
internal sealed class SupportFootprintEdgeWorkspace
{
    readonly SupportFootprintEdgeOperands[][] _rows = [[], [], []];
    readonly double[] _x = new double[3], _z = new double[3];
    SupportFootprintRequest _request;
    int _used;
    bool _active;

    internal void Clear()
    {
        _active = false;
        _used = 0;
        _request = default;
    }

    internal void Begin(in SupportFootprintRequest request)
    {
        _request = request;
        _used = 0;
        _active = true;
    }

    internal bool Shares(in SupportFootprintProposal proposal) => _active &&
        (Same(proposal, _request.AxisProposal) || Same(proposal, _request.FootProposal));

    internal SupportFootprintEdgeOperands Read(SupportPolygon polygon, int index, FeaturePoint axis, bool share = true)
    {
        if (!_active || !share || !axis.X.IsExact || !axis.Z.IsExact || !axis.Y.IsExact || axis.Y.Value != 0)
            return Unshared(polygon.Vertices[index], polygon.Edges[index], axis);
        int row = 0;
        while (row < _used && (!Same(_x[row], axis.X.Value) || !Same(_z[row], axis.Z.Value))) row++;
        if (row == _used)
        {
            if (_used == _rows.Length) return Unshared(polygon.Vertices[index], polygon.Edges[index], axis);
            using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportEdgeSetup);
            int count = polygon.Edges.Length;
            if (_rows[row].Length < count) _rows[row] = new SupportFootprintEdgeOperands[count];
            for (int i = 0; i < count; i++) _rows[row][i] = Evaluate(polygon.Vertices[i], polygon.Edges[i], axis);
            _x[row] = axis.X.Value;
            _z[row] = axis.Z.Value;
            _used++;
        }
        return _rows[row][index];
    }

    static SupportFootprintEdgeOperands Unshared(FeaturePoint vertex, FeaturePoint edge, FeaturePoint axis)
    {
        using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportEdgeSetup);
        return Evaluate(vertex, edge, axis);
    }

    static SupportFootprintEdgeOperands Evaluate(FeaturePoint vertex, FeaturePoint edge, FeaturePoint axis)
    {
        FeatureNumber a = edge.X.Multiply(edge.X, exactZeroProducts: true)
            .Add(edge.Z.Multiply(edge.Z, exactZeroProducts: true));
        FeatureNumber dx = vertex.X.Subtract(axis.X), dz = vertex.Z.Subtract(axis.Z);
        FeatureNumber dot = dx.Multiply(edge.X, exactZeroProducts: true).Add(dz.Multiply(edge.Z, exactZeroProducts: true));
        FeatureNumber cross = dx.Multiply(edge.Z, exactZeroProducts: true).Subtract(dz.Multiply(edge.X, exactZeroProducts: true));
        FeatureNumber squared = dx.Multiply(dx, exactZeroProducts: true).Add(dz.Multiply(dz, exactZeroProducts: true));
        return new(a, dot, cross, squared, dx, dz);
    }

    static bool Same(in SupportFootprintProposal a, in SupportFootprintProposal b) =>
        ReferenceEquals(a.Probe, b.Probe) && a.Probe is not null &&
        Same(a.Probe.Radius, b.Probe.Radius) && Same(a.Probe.Length, b.Probe.Length) && Same(a.Distance, b.Distance) &&
        Same(a.Start.Position.X, b.Start.Position.X) && Same(a.Start.Position.Y, b.Start.Position.Y) &&
        Same(a.Start.Position.Z, b.Start.Position.Z) && Same(a.Start.Orientation.X, b.Start.Orientation.X) &&
        Same(a.Start.Orientation.Y, b.Start.Orientation.Y) && Same(a.Start.Orientation.Z, b.Start.Orientation.Z) &&
        Same(a.Start.Orientation.W, b.Start.Orientation.W);

    static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
}
