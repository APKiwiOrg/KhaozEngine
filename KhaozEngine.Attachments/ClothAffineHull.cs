using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

// Only the static fitted recovery check uses arbitrary affine transforms.
// Rigid cloth motion keeps its separate, bounded interpolation contract.
internal readonly record struct ClothAffineHull(ClothSupportIdentity Identity, ClothSupportHull Hull, Matrix4x4 Transform)
{
    internal bool TryReadVertices(Span<Vector3> destination, out ClothBounds bounds, out Vector3 center)
    {
        bounds = default;
        center = default;
        if (Transform.M14 != 0 || Transform.M24 != 0 || Transform.M34 != 0 || Transform.M44 != 1 ||
            !ChainMath.IsFinite(Transform.Translation))
            return false;

        var x = new Offset(Transform.M11, Transform.M12, Transform.M13);
        var y = new Offset(Transform.M21, Transform.M22, Transform.M23);
        var z = new Offset(Transform.M31, Transform.M32, Transform.M33);
        double determinant = Offset.Dot(x, Offset.Cross(y, z));
        if (!double.IsFinite(determinant) || determinant == 0) return false;

        ReadOnlySpan<Vector3> local = Hull.Vertices;
        for (int i = 0; i < local.Length; i++)
        {
            Vector3 point = Vector3.Transform(local[i], Transform);
            if (!ChainMath.IsFinite(point)) return false;
            destination[i] = point;
            center += point / local.Length;
            var vertex = new ClothBounds(point, point);
            bounds = i == 0 ? vertex : ClothBounds.Enclose(bounds, vertex);
        }
        if (!ChainMath.IsFinite(center)) return false;
        foreach (ClothTriangle face in Hull.Faces)
        {
            var facet = new ClothFacet(destination[face.A], destination[face.B], destination[face.C]);
            Offset normal = facet.Triangle.Normal;
            double centerSide = Offset.Dot(normal, new Offset(center) - new Offset(facet.A));
            if (!double.IsFinite(centerSide) || centerSide == 0) return false;
        }
        return true;
    }

    internal bool Clears(in ClothFacet cloth, ReadOnlySpan<Vector3> vertices, Vector3 center, float clearance)
    {
        bool inside = true;
        double requiredSquared = (double)clearance * clearance;
        foreach (ClothTriangle face in Hull.Faces)
        {
            var facet = new ClothFacet(vertices[face.A], vertices[face.B], vertices[face.C]);
            Offset normal = facet.Triangle.Normal;
            double centerSide = Offset.Dot(normal, new Offset(center) - new Offset(facet.A));
            double clothSide = Offset.Dot(normal, new Offset(cloth.A) - new Offset(facet.A));
            inside &= centerSide * clothSide >= 0;
            double distance = ClothFacetContact.Closest(cloth, facet).SquaredDistance;
            if (!double.IsFinite(distance) || distance < requiredSquared) return false;
        }
        return !inside;
    }
}
