using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>A contribution retains its raw member's provenance even when a joined plane supplies the normal.</summary>
internal readonly record struct FootprintContribution(int MemberIndex, SupportContribution Contribution);

internal static partial class SupportCertification
{
    /// <summary>Writes one contribution per occupied witness, in raw member then role order, and returns the count.
    /// Refuses (returns -1) when the result is not Complete, the axis or slope limit is out of domain, or a
    /// member is not finite. Walkability reads each certified upward lower bound, so slope equality is walkable.
    /// A polygon contributes <c>min(plane at the axis, disc maximum, joined planes of its kind)</c>, a steep
    /// polygon joined to a walkable one contributes nothing, and a curved witness contributes
    /// <c>min(tangent plane at the axis, witness height)</c>. A polygon without a disc maximum only joins, caps and
    /// guards.</summary>
    internal static int CertifyFootprint(IPhysicsSupportFootprint capability, IPhysicsQueryLease lease,
        in SupportFootprintResult result, ReadOnlySpan<SupportFootprintMember> members, ReadOnlySpan<ulong> joins,
        Vector2 axis, float cosMaxSlope, Span<FootprintContribution> contributions) =>
        CertifyFootprint(capability, lease, result, members, joins, axis, cosMaxSlope, contributions, default);

    // Context facts, one per member, certify the retained proposal guards separately from the published
    // contributions. A ring-only plane can establish a guard or cap another plane, but never publishes support.
    internal static int CertifyFootprint(IPhysicsSupportFootprint capability, IPhysicsQueryLease lease,
        in SupportFootprintResult result, ReadOnlySpan<SupportFootprintMember> members, ReadOnlySpan<ulong> joins,
        Vector2 axis, float cosMaxSlope, Span<FootprintContribution> contributions, Span<SupportContribution> contexts,
        FootprintScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(lease);
        if (result.Status != CapsuleFeatureStatus.Complete) return -1;
        capability.AssertFootprintCurrent(result, lease);
        int count = result.Members, stride = result.JoinWordsPerRow;
        if (members.Length < count || contributions.Length < result.Witnesses || joins.Length < count * stride ||
            !contexts.IsEmpty && contexts.Length < count)
            throw new ArgumentException("The spans must hold all footprint members, witnesses, joins and context facts.");
        if (!float.IsFinite(axis.X) || !float.IsFinite(axis.Y) || !float.IsFinite(cosMaxSlope) ||
            cosMaxSlope < 0 || cosMaxSlope > 1) return -1;
        Span<double> planes = stackalloc double[2 * count];
        Span<CertifiedSupportKind> kinds = stackalloc CertifiedSupportKind[count];
        int witnesses = 0;
        for (int i = 0; i < count; i++)
        {
            SupportFootprintMember member = members[i];
            if (!FootprintFinite(member)) return -1;
            witnesses += member.WitnessCount;
            if (!contexts.IsEmpty) contexts[i] = default;
            kinds[i] = Classify(member.UpwardLower, cosMaxSlope);
            if (member.Kind != SupportElementKind.Polygon || kinds[i] == CertifiedSupportKind.Refused) continue;
            if (!PlaneAtAxis(member.PlanePoint, member.PlanePointErrorMetres, member.Normal, member.NormalError,
                member.UpwardLower, axis, out planes[2 * i], out planes[2 * i + 1])) return -1;
        }
        if (witnesses != result.Witnesses) return -1;
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            SupportFootprintMember member = members[i];
            if (member.Kind == SupportElementKind.Polygon)
            {
                CertifiedSupportKind kind = kinds[i];
                if (kind == CertifiedSupportKind.Refused) continue;
                PlaneMinimum minimum = PlaneMinimum.Empty;
                minimum.Offer(i, planes[2 * i], planes[2 * i + 1]);
                bool suppressed = false;
                for (int j = 0; j < count; j++)
                {
                    if (j == i || members[j].Kind != SupportElementKind.Polygon ||
                        (joins[i * stride + j / 64] & (1UL << (j % 64))) == 0) continue;
                    if (kind == CertifiedSupportKind.Steep && kinds[j] == CertifiedSupportKind.Walkable)
                    { suppressed = true; break; }
                    if (kinds[j] == kind) minimum.Offer(j, planes[2 * j], planes[2 * j + 1]);
                }
                if (suppressed) continue;
                double low = member.HasDiscMaximum ? Math.Min(minimum.Lower, member.DiscHeightLower) : minimum.Lower;
                double high = member.HasDiscMaximum ? Math.Min(minimum.Upper, member.DiscHeightUpper) : minimum.Upper;
                if (!double.IsFinite(low) || !double.IsFinite(high) || low > high) return -1;
                if (scope is { } polygonScope && member.HasDiscMaximum && polygonScope.Meets(low, high))
                    RequirePolygonScope(i, kind, high, members[..count], joins, stride, planes, kinds, axis,
                        polygonScope.Bounds);
                var contribution = new SupportContribution(kind, low, high, members[minimum.Winner].Normal,
                    member.First.Position, member.ElementId);
                if (!contexts.IsEmpty) contexts[i] = contribution;
                if (member.HasDiscMaximum) contributions[written++] = new(i, contribution);
                continue;
            }
            for (int slot = 0; slot < member.WitnessCount; slot++)
            {
                SupportFootprintWitness witness = slot == 0 ? member.First : member.Second;
                CertifiedSupportKind kind = Classify(witness.UpwardLower, cosMaxSlope);
                if (kind == CertifiedSupportKind.Refused) continue;
                if (!PlaneAtAxis(witness.Position, witness.PositionErrorMetres, witness.Normal, witness.NormalError,
                    witness.UpwardLower, axis, out double low, out double high)) return -1;
                double planeLower = low, planeUpper = high;
                double heightLower = LowerBound(witness.Position.Y, witness.PositionErrorMetres);
                double heightUpper = UpperBound(witness.Position.Y, witness.PositionErrorMetres);
                low = Math.Min(planeLower, heightLower);
                high = Math.Min(planeUpper, heightUpper);
                if (scope is { } tangentScope && tangentScope.Meets(low, high))
                {
                    if (planeLower <= high) RequirePlaneInScope(axis, planeLower, planeUpper, tangentScope.Bounds);
                    if (heightLower <= high) RequireWitnessInScope(witness, tangentScope.Bounds);
                }
                var contribution = new SupportContribution(kind, low, high, witness.Normal, witness.Position, member.ElementId);
                contributions[written++] = new(i, contribution);
                if (!contexts.IsEmpty && (contexts[i].Kind == CertifiedSupportKind.Refused || low < contexts[i].Lower))
                    contexts[i] = contribution;
            }
        }
        return written;
    }

    // Checks every plane that can decide a published polygon contribution, its own or a joined cap, and its
    // disc maximum witness when that can decide it.
    static void RequirePolygonScope(int member, CertifiedSupportKind kind, double high,
        ReadOnlySpan<SupportFootprintMember> members, ReadOnlySpan<ulong> joins, int stride, ReadOnlySpan<double> planes,
        ReadOnlySpan<CertifiedSupportKind> kinds, Vector2 axis, in SupportFootprintBounds scope)
    {
        for (int j = 0; j < members.Length; j++)
        {
            bool term = j == member || members[j].Kind == SupportElementKind.Polygon && kinds[j] == kind &&
                (joins[member * stride + j / 64] & (1UL << (j % 64))) != 0;
            if (term && planes[2 * j] <= high) RequirePlaneInScope(axis, planes[2 * j], planes[2 * j + 1], scope);
        }
        SupportFootprintMember own = members[member];
        if (own.DiscHeightLower > high) return;
        RequireWitnessInScope(own.First, scope);
        if (!(own.DiscHeightLower >= scope.Minimum.Y && own.DiscHeightUpper <= scope.Maximum.Y))
            throw new NotSupportedException("A required footprint witness is outside the admitted scope.");
    }

    static CertifiedSupportKind Classify(double upward, float cosine) => upward > 0
        ? upward >= cosine ? CertifiedSupportKind.Walkable : CertifiedSupportKind.Steep : CertifiedSupportKind.Refused;

    static bool FootprintFinite(in SupportFootprintMember member)
    {
        if (member.Kind is not (SupportElementKind.Polygon or SupportElementKind.Tangent) ||
            !PointFinite(member.PlanePoint, member.PlanePointErrorMetres) || !PointFinite(member.Normal, member.NormalError) ||
            !double.IsFinite(member.UpwardLower) || member.ProposalMask > 3 || member.WitnessCount > 2 ||
            member.HasDiscMaximum && (!double.IsFinite(member.DiscHeightLower) || !double.IsFinite(member.DiscHeightUpper) ||
                member.DiscHeightLower > member.DiscHeightUpper)) return false;
        if (member.Kind == SupportElementKind.Polygon &&
            member.WitnessCount != (member.HasDiscMaximum ? 1 : 0)) return false;
        for (int slot = 0; slot < member.WitnessCount; slot++)
        {
            SupportFootprintWitness witness = slot == 0 ? member.First : member.Second;
            if (witness.Role > SupportWitnessRole.Footprint || !double.IsFinite(witness.UpwardLower) ||
                !PointFinite(witness.Position, witness.PositionErrorMetres) || !PointFinite(witness.Normal, witness.NormalError))
                return false;
        }
        return true;
    }

    static bool PointFinite(Vector3 point, float error) => float.IsFinite(point.X) && float.IsFinite(point.Y) &&
        float.IsFinite(point.Z) && float.IsFinite(error) && error >= 0;
}
