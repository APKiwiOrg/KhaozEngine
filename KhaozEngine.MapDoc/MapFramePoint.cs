using System;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc;

/// <summary>A frame-local horizontal position with an absolute world-datum height.</summary>
public readonly record struct MapFramePoint(WorldFrame Frame, Vector3 Local)
{
    public MapExactXz ExactWorldXz()
    {
        if (!float.IsFinite(Local.X) || !float.IsFinite(Local.Z)) throw new ArgumentException("point XZ must be finite");
        return new(
            new MapExactValue((long)Frame.X * (long)WorldFrame.Grid, 1).Add(MapExactValue.FromSingle(Local.X)),
            new MapExactValue((long)Frame.Z * (long)WorldFrame.Grid, 1).Add(MapExactValue.FromSingle(Local.Z)));
    }

    public MapExactValue ExactY() => MapExactValue.FromSingle(Local.Y);
}
