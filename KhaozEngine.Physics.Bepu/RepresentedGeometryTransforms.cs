using System.Numerics;
using BepuUtilities;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Three complete scalar enclosures. Default and any unresolved component refuse the vector.</summary>
internal readonly struct GeometryVector
{
    public GeometryInterval X { get; }
    public GeometryInterval Y { get; }
    public GeometryInterval Z { get; }
    public bool IsResolved => X.IsResolved && Y.IsResolved && Z.IsResolved;

    public GeometryVector(GeometryInterval x, GeometryInterval y, GeometryInterval z)
    {
        this = default;
        if (!x.IsResolved || !y.IsResolved || !z.IsResolved) return;
        X = x;
        Y = y;
        Z = z;
    }
}

/// <summary>Outward arithmetic for a supplied represented row-basis matrix.
/// This does not establish matrix/pose correspondence, rigidity, shape ownership or geometric eligibility.</summary>
internal static class RepresentedGeometryTransforms
{
    public static GeometryVector Point(in Matrix3x3 matrix, Vector3 translation, Vector3 local)
    {
        GeometryVector rotated = Direction(matrix, local);
        if (!rotated.IsResolved) return default;
        return new(AddTranslation(rotated.X, translation.X), AddTranslation(rotated.Y, translation.Y),
            AddTranslation(rotated.Z, translation.Z));
    }

    public static GeometryVector Direction(in Matrix3x3 matrix, Vector3 local) => new(
        Dot(local, matrix.X.X, matrix.Y.X, matrix.Z.X),
        Dot(local, matrix.X.Y, matrix.Y.Y, matrix.Z.Y),
        Dot(local, matrix.X.Z, matrix.Y.Z, matrix.Z.Z));

    static GeometryInterval AddTranslation(GeometryInterval value, float translation) =>
        value.Add(GeometryInterval.Exact(translation)).EncloseSingleRounding();

    static GeometryInterval Product(float a, float b) =>
        GeometryInterval.Exact(a).Multiply(GeometryInterval.Exact(b)).EncloseSingleRounding();

    static GeometryInterval Dot(Vector3 v, float x, float y, float z) =>
        Product(v.X, x).Add(Product(v.Y, y)).EncloseSingleRounding()
            .Add(Product(v.Z, z)).EncloseSingleRounding();
}
