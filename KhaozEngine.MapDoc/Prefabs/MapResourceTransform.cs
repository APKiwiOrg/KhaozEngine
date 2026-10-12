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
    internal const double MaxCoordinateMetres = 1_000_000;
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

    /// <summary>The released binary32 water-placement adapter. R5 effective resources use Point and Height,
    /// while the pre-existing bounded placement API retains its results and representability refusals.</summary>
    internal MapExactXz ReleasedWaterXz(MapExactXz local)
    {
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Transform.YawRadians);
        var world = Vector3.Transform(new Vector3(local.X.ToSingle(), 0, local.Z.ToSingle()) * (float)Transform.Scale, yaw)
            + new Vector3((float)Transform.X, (float)Transform.Y, (float)Transform.Z);
        if (!float.IsFinite(world.X) || !float.IsFinite(world.Z)) throw new MapExactOverflowException();
        return new(MapExactValue.FromSingle(world.X), MapExactValue.FromSingle(world.Z));
    }
    internal float ReleasedWaterHeight(float local) => (float)Transform.Y + (float)Transform.Scale * local;

    internal static MapExactValue Freeze(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > MaxCoordinateMetres)
            throw new MapDocumentException("Effective coordinate is not representable within the installed metre limit.");
        try { return new(checked((long)Math.Round(value * Quantum, MidpointRounding.ToEven)), Quantum); }
        catch (OverflowException) { throw new MapExactOverflowException(); }
    }

    internal static void RequireWithinWorld(string owner, double x, double y, double z)
    {
        if (Math.Abs(x) > MaxCoordinateMetres || Math.Abs(y) > MaxCoordinateMetres || Math.Abs(z) > MaxCoordinateMetres)
            throw new MapDocumentException(FormattableString.Invariant(
                $"Static '{owner}' at ({x}, {y}, {z}) lies beyond {MaxCoordinateMetres:N0} m from the world origin on an axis."));
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
