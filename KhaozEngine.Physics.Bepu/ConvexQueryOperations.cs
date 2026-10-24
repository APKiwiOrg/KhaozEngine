using System;
using System.Numerics;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Physics.Bepu;

internal enum ConvexQueryOperation { Sweep, Distance, Contacts, Penetration }
internal readonly record struct ConvexQueryRequest(ConvexQueryShape Shape, Pose Pose,
    Vector3 Displacement, ConvexQueryOperation Operation);

/// <summary>Support proposals only. Static certificates decide geometric acceptance.</summary>
internal static class ConvexQueryOperations
{
    internal static bool TryTemporalVertex(in ConvexQueryRequest request, in ConvexPlanarSource source,
        double from, double to, P direction, ref ConvexQueryWork work, out ConvexTemporalVertex target)
    {
        target = default;
        if (!work.Cell() || !work.Feature() || P.Dot(direction, direction).Sign <= 0) return false;
        int vertex = 0;
        P best = source.Vertex(0);
        for (int i = 1; i < source.VertexCount; i++)
        {
            if (!work.Feature()) return false;
            P candidate = source.Vertex(i);
            if (P.Dot(candidate - best, direction).Sign < 0) { vertex = i; best = candidate; }
        }
        double endpoint = P.Dot(P.From(request.Displacement), direction).Sign > 0 ? to : from;
        target = ConvexTemporalVertex.Create(request, source, vertex, endpoint);
        return true;
    }

    internal static Vector3 Support(in ConvexQueryShape shape, Vector3 direction)
    {
        if (!shape.IsValid || !float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z))
            throw new ArgumentException("A support proposal requires finite shape and direction values.");
        double x = direction.X, y = direction.Y, z = direction.Z;
        double radial = Math.Sqrt(x * x + z * z), norm = Math.Sqrt(x * x + y * y + z * z);
        double apex = shape.LowerY * y;
        double ring = shape.RingY * y + shape.Radius * radial;
        double head = shape.UpperY * y + shape.Radius * radial;
        Vector3 result = apex > ring && apex >= head ? new(0, shape.LowerY, 0) :
            new((float)(radial == 0 ? 0 : shape.Radius * x / radial),
                head > ring ? shape.UpperY : shape.RingY,
                (float)(radial == 0 ? 0 : shape.Radius * z / radial));
        if (shape.Margin != 0 && norm != 0)
            result += new Vector3((float)(shape.Margin * x / norm), (float)(shape.Margin * y / norm),
                (float)(shape.Margin * z / norm));
        return result;
    }
}
