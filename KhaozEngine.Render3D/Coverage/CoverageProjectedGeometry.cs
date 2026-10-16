using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Predicates for the authoritative projection of float host positions through actor * inverse(coverage).
/// For world edges u,v and coverage linear part C, det(projectXY(u*C^-1), projectXY(v*C^-1)) equals
/// dot(cross(u,v), row3(C)) / det(C). Frame preparation proves det(C) positive. Exact actor-affine expansions therefore
/// give the projected orientation sign without rounding an inverse or discarding affine-sum tails.</summary>
internal static class CoverageProjectedGeometry
{
    internal static CoverageScalar Orientation(Vector3 a, Vector3 b, Vector3 c, in CoverageCommandFrame frame) =>
        CoverageGeometry.ProjectedArea(CoverageAffinePoint.Project(a, frame.ActorModel), CoverageAffinePoint.Project(b, frame.ActorModel),
            CoverageAffinePoint.Project(c, frame.ActorModel), frame.CoverageZ);

    internal static bool InteriorsOverlap(Vector3 a0, Vector3 a1, Vector3 a2, Vector3 b0, Vector3 b1, Vector3 b2,
        in CoverageCommandFrame frame, int sign) => InteriorsOverlap(CoverageAffinePoint.Project(a0, frame.ActorModel),
            CoverageAffinePoint.Project(a1, frame.ActorModel), CoverageAffinePoint.Project(a2, frame.ActorModel),
            CoverageAffinePoint.Project(b0, frame.ActorModel), CoverageAffinePoint.Project(b1, frame.ActorModel),
            CoverageAffinePoint.Project(b2, frame.ActorModel), frame.CoverageZ, sign);

    internal static bool InteriorsOverlap(CoverageAffinePoint a0, CoverageAffinePoint a1, CoverageAffinePoint a2,
        CoverageAffinePoint b0, CoverageAffinePoint b1, CoverageAffinePoint b2, Vector3 axis, int sign)
    {
        return !(Separates(a0, a1, b0, b1, b2) || Separates(a1, a2, b0, b1, b2) || Separates(a2, a0, b0, b1, b2)
            || Separates(b0, b1, a0, a1, a2) || Separates(b1, b2, a0, a1, a2) || Separates(b2, b0, a0, a1, a2));

        bool Separates(CoverageAffinePoint u, CoverageAffinePoint v, CoverageAffinePoint p, CoverageAffinePoint q, CoverageAffinePoint r) =>
            Outside(u, v, p) && Outside(u, v, q) && Outside(u, v, r);
        bool Outside(CoverageAffinePoint u, CoverageAffinePoint v, CoverageAffinePoint p)
        {
            CoverageScalar area = CoverageGeometry.ProjectedArea(u, v, p, axis);
            return area.IsFinite && sign * area.Sign <= 0;
        }
    }
}
