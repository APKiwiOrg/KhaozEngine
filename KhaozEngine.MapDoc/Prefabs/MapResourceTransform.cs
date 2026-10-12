using System;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>One semantic point freeze after binary64 pose composition. SinCos is a platform libm operation,
/// not a cross-platform determinism promise. Backend presentation never feeds this kernel.</summary>
public sealed record MapResourceTransform(MapEffectiveTransform Transform)
{
    internal const long Quantum = 1048576;
    public MapExactPoint Point(MapExactPoint local)
    {
        var xz = Xz(new(local.X, local.Z));
        return new(xz.X, Height(local.Y), xz.Z);
    }
    internal MapExactXz Xz(MapExactXz local)
    {
        MapEffectiveTransform.Valid(Transform);
        var (s, c) = Math.SinCos(Transform.YawRadians);
        double x = local.X.ToDouble() * Transform.Scale, z = local.Z.ToDouble() * Transform.Scale;
        return new(Freeze(Transform.X + c * x + s * z), Freeze(Transform.Z - s * x + c * z));
    }

    public MapExactValue Height(MapExactValue local)
    {
        MapEffectiveTransform.Valid(Transform);
        return MapExactValue.FromSingle(Freeze(Transform.Y + local.ToDouble() * Transform.Scale).ToSingle());
    }

    internal static MapExactValue Freeze(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > 1000000)
            throw new MapDocumentException("Effective coordinate is not representable within the installed metre limit.");
        try { return new(checked((long)Math.Round(value * Quantum, MidpointRounding.ToEven)), Quantum); }
        catch (OverflowException) { throw new MapExactOverflowException(); }
    }

    public Vector3 ToFrame(MapExactPoint world, WorldFrame frame)
    {
        float x = world.X.Subtract(new((long)frame.X * (long)WorldFrame.Grid, 1)).ToSingle();
        float z = world.Z.Subtract(new((long)frame.Z * (long)WorldFrame.Grid, 1)).ToSingle();
        float y = world.Y.ToSingle();
        if (!float.IsFinite(y) || (double)x * x + (double)z * z > (double)WorldFrame.MaxLocalRadius * WorldFrame.MaxLocalRadius)
            throw new MapDocumentException("Effective coordinate exceeds frame radius.");
        return new(x, y, z);
    }

    public Vector3 Normal(Vector3 local)
    {
        MapEffectiveTransform.Valid(Transform);
        var (s, c) = Math.SinCos(Transform.YawRadians);
        return Vector3.Normalize(new((float)(c * local.X + s * local.Z), local.Y, (float)(-s * local.X + c * local.Z)));
    }
}
