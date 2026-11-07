using System;
using System.Collections.Generic;
using System.Numerics;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>One closest-point projection to a representable, strictly wet column inside its owner.</summary>
internal sealed class TileWaterWitness
{
    readonly record struct Basis(int A, int B = -1);
    readonly P[] _planes;
    readonly Basis[] _bases;
    readonly float _minX, _maxX, _minZ, _maxZ;
    readonly V _reference;

    internal TileWaterWitness(TileWaterPrism prism)
    {
        _reference = prism.Reference;
        _minX = MathF.BitIncrement(prism.Column.Minimum.X);
        _maxX = MathF.BitDecrement(prism.Column.Maximum.X);
        _minZ = MathF.BitIncrement(prism.Column.Minimum.Z);
        _maxZ = MathF.BitDecrement(prism.Column.Maximum.Z);
        var planes = new List<P>(8) { prism.Planes[0], prism.Planes[1], prism.Planes[2] };
        P cut = prism.Planes[5];
        I retreat = I.Point(prism.Column.Maximum.Y) - I.Point(MathF.BitDecrement(prism.Column.Maximum.Y)) + I.Point(prism.HeightError);
        planes.Add(new(cut.Normal, cut.Offset - retreat));
        planes.Add(new(V.Point(-1, 0, 0), _reference.X - I.Point(_minX)));
        planes.Add(new(V.Point(1, 0, 0), I.Point(_maxX) - _reference.X));
        planes.Add(new(V.Point(0, 0, -1), _reference.Z - I.Point(_minZ)));
        planes.Add(new(V.Point(0, 0, 1), I.Point(_maxZ) - _reference.Z));
        _planes = planes.ToArray();
        var bases = new List<Basis> { new(-1) };
        for (int a = 0; a < _planes.Length; a++)
        {
            if (!TileWaterLinearRank.Independent(_planes, a, -1, -1)) continue;
            bases.Add(new(a));
            for (int b = a + 1; b < _planes.Length; b++)
                if (TileWaterLinearRank.Independent(_planes, a, b, -1)) bases.Add(new(a, b));
        }
        _bases = bases.ToArray();
    }

    internal bool TryLocate(double worldX, double worldZ, out Vector2 column)
    {
        column = default;
        if (_minX > _maxX || _minZ > _maxZ) return false;
        V point = new V(I.Point(worldX), I.Point(0), I.Point(worldZ)) - _reference;
        double nearest = double.PositiveInfinity;
        V selected = default;
        foreach (Basis basis in _bases)
        {
            V q = point;
            if (basis.A >= 0)
            {
                P a = _planes[basis.A];
                if (basis.B < 0)
                    q -= a.Normal * ((V.Dot(a.Normal, point) - a.Offset) / a.Normal.SquaredLength);
                else
                {
                    P b = _planes[basis.B];
                    I determinant = a.Normal.X * b.Normal.Z - a.Normal.Z * b.Normal.X;
                    if (determinant.Low <= 0 && determinant.High >= 0) continue;
                    q = new((a.Offset * b.Normal.Z - b.Offset * a.Normal.Z) / determinant, I.Point(0),
                        (a.Normal.X * b.Offset - b.Normal.X * a.Offset) / determinant);
                }
            }
            bool feasible = true;
            foreach (P plane in _planes)
                if (V.Dot(plane.Normal, q).Low > plane.Offset.High) { feasible = false; break; }
            if (!feasible) continue;
            double distance = (point - q).SquaredLength.Low;
            if (distance >= nearest) continue;
            nearest = distance;
            selected = q + _reference;
        }
        if (!double.IsFinite(nearest)) return false;
        column = new(Math.Clamp((float)TileWaterConvexShape.Mid(selected.X), _minX, _maxX),
            Math.Clamp((float)TileWaterConvexShape.Mid(selected.Z), _minZ, _maxZ));
        return float.IsFinite(column.X) && float.IsFinite(column.Y);
    }
}
