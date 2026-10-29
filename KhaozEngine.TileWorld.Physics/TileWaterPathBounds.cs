using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Directed lateral bounds of a whole framed capsule path.</summary>
internal readonly struct TileWaterPathBounds
{
    readonly I _x, _z;

    internal TileWaterPathBounds(in MovementMediumSweepQuery query, Vector3 origin)
    {
        I x = I.Point(query.Body.Centre.X) + I.Point(origin.X), z = I.Point(query.Body.Centre.Z) + I.Point(origin.Z);
        I endX = x + I.Point(query.Delta.X), endZ = z + I.Point(query.Delta.Z);
        I radius = new(-query.Body.Radius, query.Body.Radius);
        _x = new I(Math.Min(x.Low, endX.Low), Math.Max(x.High, endX.High)) + radius;
        _z = new I(Math.Min(z.Low, endZ.Low), Math.Max(z.High, endZ.High)) + radius;
    }

    internal bool Meets(Vector3 minimum, Vector3 maximum) => _x.High >= minimum.X && _x.Low <= maximum.X &&
        _z.High >= minimum.Z && _z.Low <= maximum.Z;
}
