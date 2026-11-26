using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

internal readonly record struct ClothFacetContact(Offset Residual, Vector3 Weights, Vector3 OpposingWeights)
{
    internal double SquaredDistance => Residual.LengthSquared;

    internal static ClothFacetContact Closest(in ClothFacet first, in ClothFacet second)
    {
        ClothFacetContact result = ClosestEdges(first, second);
        if (result.SquaredDistance == 0) return result;
        ClothFacetContact reversed = ClosestEdges(second, first);
        return reversed.SquaredDistance < result.SquaredDistance
            ? new ClothFacetContact(reversed.Residual * -1, reversed.OpposingWeights, reversed.Weights)
            : result;
    }

    private static ClothFacetContact ClosestEdges(in ClothFacet first, in ClothFacet second)
    {
        ChainContactTriangle triangle = first.Triangle;
        ClothFacetContact result = Query(second.A, second.B, Vector3.UnitX, Vector3.UnitY);
        ClothFacetContact next = Query(second.B, second.C, Vector3.UnitY, Vector3.UnitZ);
        if (next.SquaredDistance < result.SquaredDistance) result = next;
        next = Query(second.C, second.A, Vector3.UnitZ, Vector3.UnitX);
        if (next.SquaredDistance < result.SquaredDistance) result = next;
        return result;

        ClothFacetContact Query(Vector3 a, Vector3 b, Vector3 weightA, Vector3 weightB)
        {
            ChainContactTriangle.Contact hit = triangle.Closest(new Axis(new Offset(a), new Offset(b)), out _);
            return new ClothFacetContact(hit.Residual, hit.Weights.ToVector3(),
                weightA * (float)hit.Fraction.StartWeight + weightB * (float)hit.Fraction.EndWeight);
        }
    }
}

internal readonly record struct ClothPosedHull(ClothSupportIdentity Identity, ClothSupportHull Hull,
    ClothHullPose Opening, ClothHullPose Closing)
{
    internal float Curvature
    {
        get
        {
            float angle = ClothContactSweep.Angle(Opening.Rotation, Closing.Rotation);
            return Hull.Radius * (angle * angle * MathF.Max(Opening.Scale, Closing.Scale) +
                2 * angle * MathF.Abs(Closing.Scale - Opening.Scale));
        }
    }

    internal ClothBounds SweptBounds
    {
        get
        {
            var bounds = new ClothBounds(Opening.World(Hull.Vertices[0]), Opening.World(Hull.Vertices[0]));
            foreach (Vector3 vertex in Hull.Vertices)
            {
                Vector3 first = Opening.World(vertex), last = Closing.World(vertex);
                bounds = ClothBounds.Enclose(bounds, new ClothBounds(Vector3.Min(first, last), Vector3.Max(first, last)));
            }
            return bounds.Expand(Curvature * .125f);
        }
    }

    internal Vector3 SurfaceDisplacement(Vector3 localPoint) => Closing.World(localPoint) - Opening.World(localPoint);
}

internal readonly struct ClothHullSweep(ClothFacet first, ClothFacet last, ClothPosedHull support) : IClothSweepPair
{
    public float Curvature { get; } = support.Curvature;

    public ClothSweepSample Sample(float time)
    {
        HullContact contact = Witness(time);
        return new ClothSweepSample(contact.Distance, contact.Normal);
    }

    public float Gap(float time, Vector3 normal)
    {
        ClothHullPose pose = PoseAt(time);
        Vector3 localNormal = Vector3.Transform(normal, Quaternion.Conjugate(pose.Rotation));
        float maximum = float.NegativeInfinity;
        foreach (Vector3 vertex in support.Hull.Vertices)
            maximum = MathF.Max(maximum, Vector3.Dot(vertex, localNormal));
        return first.At(last, time).MinProjection(normal) -
            (Vector3.Dot(pose.Translation, normal) + pose.Scale * maximum);
    }

    internal HullContact Witness(float time)
    {
        ClothHullPose pose = PoseAt(time);
        ClothFacet world = first.At(last, time);
        var local = new ClothFacet(pose.Local(world.A), pose.Local(world.B), pose.Local(world.C));
        ReadOnlySpan<Vector3> vertices = support.Hull.Vertices;
        ClothFacetContact best = default;
        Vector3 surface = default;
        double squared = double.PositiveInfinity;
        bool inside = true;
        int feature = 0;
        foreach (ClothTriangle face in support.Hull.Faces)
        {
            var hullFace = new ClothFacet(vertices[face.A], vertices[face.B], vertices[face.C]);
            var normal = hullFace.Triangle.Normal;
            inside &= Offset.Dot(normal, new Offset(local.A) - new Offset(hullFace.A)) < 0;
            ClothFacetContact contact = ClothFacetContact.Closest(local, hullFace);
            if (contact.SquaredDistance < squared)
            {
                squared = contact.SquaredDistance;
                best = contact;
                surface = face.Blend(vertices, contact.OpposingWeights);
                feature = (face.A << 8) | (face.B << 4) | face.C;
            }
        }
        double distance = Math.Sqrt(squared) * (inside ? -1 : 1);
        Vector3 direction = distance != 0 ? Vector3.Transform((best.Residual * (1 / distance)).ToVector3(), pose.Rotation)
            : Vector3.Zero;
        return new HullContact((float)(distance * pose.Scale), direction, best.Weights, surface, feature);
    }

    private ClothHullPose PoseAt(float time) =>
        time == 0 ? support.Opening : time == 1 ? support.Closing : support.Opening.At(support.Closing, time);

    internal readonly record struct HullContact(float Distance, Vector3 Normal, Vector3 Weights, Vector3 LocalPoint, int Feature);
}
