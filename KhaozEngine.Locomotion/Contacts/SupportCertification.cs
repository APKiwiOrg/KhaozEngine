using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>How one certified neighborhood member may support a body. <see cref="Refused"/> marks a member that
/// contributes nothing.</summary>
internal enum CertifiedSupportKind : byte { Refused, Walkable, Steep }

/// <summary>One neighborhood member's support at the body axis. <see cref="Lower"/> and <see cref="Upper"/> enclose
/// the contribution height: the minimum of the member's plane at the axis, its witness height and the planes at the
/// axis of the joined members that cap it. <see cref="Normal"/> is the member whose plane wins that minimum,
/// <see cref="Witness"/> the member's own witness and <see cref="FeatureId"/> its element id. A
/// <see cref="CertifiedSupportKind.Refused"/> contribution carries no usable values.</summary>
internal readonly record struct SupportContribution(CertifiedSupportKind Kind, double Lower, double Upper,
    Vector3 Normal, Vector3 Witness, int FeatureId);

/// <summary>Turns a complete support neighborhood into certified support contributions at an axis.</summary>
internal static class SupportCertification
{
    // The represented query band is slightly inside the exact 0.1 mm limit. It never widens contact.
    internal const float ContactBand = 0.0001f;

    /// <summary>Writes one contribution per member, in element order, and returns the count. Refuses (returns -1)
    /// when the result is not Complete, the axis or slope limit is out of domain, or a member's contribution is not
    /// finite. Throws for an expired, foreign or wrong-receiver result, and for spans shorter than the published
    /// elements and join rows. A member whose normal's upward lower bound reaches zero writes a contribution of
    /// kind Refused that callers skip.</summary>
    /// <remarks>Every member's errors propagate into its interval through outward-rounded arithmetic, so there is no
    /// fixed error threshold. A member is walkable when its normal's upward lower bound reaches
    /// <paramref name="cosMaxSlope"/>, so a normal straddling the limit is steep. A walkable polygon contributes
    /// <c>min(plane at the axis, witness height, plane at the axis of every walkable polygon joined to it)</c>. A
    /// steep polygon follows the phase 1 steep rule over its joined set: the minimum with the plane at the axis of
    /// every joined polygon whose upward bound is positive. A tangent element is never joined and contributes
    /// <c>min(tangent plane at the axis, witness height)</c>.</remarks>
    internal static int CertifyNeighborhood(IPhysicsCapsuleFeatures capability, IPhysicsQueryLease lease,
        in SupportNeighborhoodResult result, ReadOnlySpan<SupportElement> elements, ReadOnlySpan<ulong> joins,
        Vector2 axis, float cosMaxSlope, Span<SupportContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(lease);
        if (result.Status != CapsuleFeatureStatus.Complete) return -1;
        capability.AssertNeighborhoodCurrent(result, lease);
        int count = result.Elements, stride = result.JoinWordsPerRow;
        if (elements.Length < count || contributions.Length < count || joins.Length < count * stride)
            throw new ArgumentException("The spans must hold every published element, join row and contribution.");
        if (!float.IsFinite(cosMaxSlope) || cosMaxSlope < 0 || cosMaxSlope > 1 || !float.IsFinite(axis.X) ||
            !float.IsFinite(axis.Y))
            return -1;

        // Each member's own plane at the axis, read again when it caps a joined member.
        Span<double> planes = stackalloc double[2 * SupportNeighborhoodResult.MaximumElements];
        for (int i = 0; i < count; i++)
        {
            SupportElement member = elements[i];
            if (!Finite(member)) return -1;
            double upward = LowerBound(member.Normal.Y, member.NormalError);
            // A member that may face sideways or down has no bounded plane at the axis and supports nothing.
            if (!(upward > 0))
            {
                contributions[i] = default;
                continue;
            }
            if (!PlaneAtAxis(member.Witness, member.PositionErrorMetres, member.Normal, member.NormalError, upward,
                    axis, out planes[2 * i], out planes[2 * i + 1]))
                return -1;
            CertifiedSupportKind kind =
                upward >= cosMaxSlope ? CertifiedSupportKind.Walkable : CertifiedSupportKind.Steep;
            contributions[i] = new(kind, 0, 0, member.Normal, member.Witness, member.ElementId);
        }

        for (int i = 0; i < count; i++)
        {
            SupportContribution own = contributions[i];
            if (own.Kind == CertifiedSupportKind.Refused) continue;
            PlaneMinimum minimum = PlaneMinimum.Empty;
            minimum.Offer(i, planes[2 * i], planes[2 * i + 1]);
            if (elements[i].Kind == SupportElementKind.Polygon)
            {
                ReadOnlySpan<ulong> row = joins.Slice(i * stride, stride);
                for (int j = 0; j < count; j++)
                {
                    if (j == i || (row[j / 64] & (1UL << (j % 64))) == 0) continue;
                    if (elements[j].Kind != SupportElementKind.Polygon) continue;
                    CertifiedSupportKind joined = contributions[j].Kind;
                    // A walkable member is capped by walkable joins only, a steep one by every rising join.
                    if (joined == CertifiedSupportKind.Refused ||
                        (own.Kind == CertifiedSupportKind.Walkable && joined != CertifiedSupportKind.Walkable))
                        continue;
                    minimum.Offer(j, planes[2 * j], planes[2 * j + 1]);
                }
            }
            SupportElement member = elements[i];
            double lower = Math.Min(minimum.Lower, LowerBound(member.Witness.Y, member.PositionErrorMetres));
            double upper = Math.Min(minimum.Upper, UpperBound(member.Witness.Y, member.PositionErrorMetres));
            if (!double.IsFinite(lower) || !double.IsFinite(upper) || lower > upper) return -1;
            contributions[i] = own with { Lower = lower, Upper = upper, Normal = elements[minimum.Winner].Normal };
        }
        return count;
    }

    static bool Finite(in SupportElement member) =>
        float.IsFinite(member.Normal.X) && float.IsFinite(member.Normal.Y) && float.IsFinite(member.Normal.Z) &&
        float.IsFinite(member.Witness.X) && float.IsFinite(member.Witness.Y) && float.IsFinite(member.Witness.Z) &&
        float.IsFinite(member.NormalError) && member.NormalError >= 0 &&
        float.IsFinite(member.PositionErrorMetres) && member.PositionErrorMetres >= 0;

    // The running minimum of member plane enclosures at the axis. The member with the lowest enclosure wins. Equal
    // lower bounds prefer the lower upper bound.
    struct PlaneMinimum
    {
        internal double Lower, Upper;
        internal int Winner;
        double _winnerLower, _winnerUpper;

        internal static PlaneMinimum Empty => new()
        {
            Lower = double.PositiveInfinity,
            Upper = double.PositiveInfinity,
            Winner = -1,
            _winnerLower = double.PositiveInfinity,
            _winnerUpper = double.PositiveInfinity,
        };

        internal void Offer(int member, double lower, double upper)
        {
            if (lower < _winnerLower || (lower == _winnerLower && upper < _winnerUpper))
                (Winner, _winnerLower, _winnerUpper) = (member, lower, upper);
            Lower = Math.Min(Lower, lower);
            Upper = Math.Min(Upper, upper);
        }
    }

    // Encloses w.y + (n.x (w.x - axis.X) + n.z (w.z - axis.Y)) / n.y over the witness box and the normal box.
    // Every binary64 operation is rounded outward by one step.
    static bool PlaneAtAxis(Vector3 witness, float position, Vector3 normal, float normalError, double upwardLower,
        Vector2 axis, out double lower, out double upper)
    {
        lower = upper = 0;
        // The quotient needs a strictly positive divisor to stay bounded.
        if (!(upwardLower > 0)) return false;
        (double Lo, double Hi) dx = Difference(witness.X, position, axis.X);
        (double Lo, double Hi) dz = Difference(witness.Z, position, axis.Y);
        (double Lo, double Hi) nx = (LowerBound(normal.X, normalError), UpperBound(normal.X, normalError));
        (double Lo, double Hi) nz = (LowerBound(normal.Z, normalError), UpperBound(normal.Z, normalError));
        (double Lo, double Hi) ny = (upwardLower, UpperBound(normal.Y, normalError));
        (double Lo, double Hi) px = Product(nx, dx), pz = Product(nz, dz);
        (double Lo, double Hi) rise = (Math.BitDecrement(px.Lo + pz.Lo), Math.BitIncrement(px.Hi + pz.Hi));
        (double Lo, double Hi) offset = Quotient(rise, ny);
        lower = Math.BitDecrement(LowerBound(witness.Y, position) + offset.Lo);
        upper = Math.BitIncrement(UpperBound(witness.Y, position) + offset.Hi);
        return double.IsFinite(lower) && double.IsFinite(upper) && lower <= upper;
    }

    static (double Lo, double Hi) Difference(float value, float error, float origin) =>
        (Math.BitDecrement(LowerBound(value, error) - origin), Math.BitIncrement(UpperBound(value, error) - origin));

    static (double Lo, double Hi) Product((double Lo, double Hi) a, (double Lo, double Hi) b)
    {
        double ll = a.Lo * b.Lo, lh = a.Lo * b.Hi, hl = a.Hi * b.Lo, hh = a.Hi * b.Hi;
        return (Math.BitDecrement(Math.Min(Math.Min(ll, lh), Math.Min(hl, hh))),
            Math.BitIncrement(Math.Max(Math.Max(ll, lh), Math.Max(hl, hh))));
    }

    // The divisor interval is strictly positive, so its endpoints bound the quotient.
    static (double Lo, double Hi) Quotient((double Lo, double Hi) a, (double Lo, double Hi) b)
    {
        double ll = a.Lo / b.Lo, lh = a.Lo / b.Hi, hl = a.Hi / b.Lo, hh = a.Hi / b.Hi;
        return (Math.BitDecrement(Math.Min(Math.Min(ll, lh), Math.Min(hl, hh))),
            Math.BitIncrement(Math.Max(Math.Max(ll, lh), Math.Max(hl, hh))));
    }

    static double LowerBound(float component, float error)
    {
        if (!float.IsFinite(component)) return double.NegativeInfinity;
        // Exact values retain exact zeros. Otherwise enclose subtraction downward, rather than
        // treating an uncertain bound as settled with an epsilon.
        return error == 0 ? component : Math.BitDecrement((double)component - error);
    }

    static double UpperBound(float component, float error)
    {
        if (!float.IsFinite(component)) return double.PositiveInfinity;
        return error == 0 ? component : Math.BitIncrement((double)component + error);
    }
}
