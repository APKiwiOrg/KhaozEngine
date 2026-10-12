using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>One footprint question in the world's local frame, as <c>StepCore</c> receives it. Support is
/// sought within <c>[FeetY - ReachDown, FeetY + ReachUp]</c> under the vertical axis at <see cref="Axis"/>
/// (X, Z), from a footprint disc of <see cref="FootRadius"/>.</summary>
internal readonly record struct FootSupportQuery(Vector2 Axis, float FeetY, float FootRadius,
    float ReachUp, float ReachDown, float CosMaxSlope);

/// <summary>Finds the certified support height under a body's axis. Analytic terrain is exact at the axis.
/// Physics statics are certified throughout the complete footprint volume, with the two original downward
/// probes retained as proposal guards.</summary>
internal static class FootSupport
{
    /// <summary>The radius of the thin probe capsule that proposes the surface under the axis itself. It is the
    /// neighborhood backend's minimum query radius (<c>BepuPhysicsWorld.SupportNeighborhood.cs</c>), so every axis
    /// proposal stays certifiable.</summary>
    internal const float AxisProbeRadius = 0.01f;

    // The probes' cylindrical segment. Its lower cap holds the probe's lowest point.
    internal const float ProbeLength = 0.01f;
    // The probes start this far above the band and sweep this far past it, so a surface exactly on either band
    // edge is reached by a sweep rather than landing on the sweep's start or end.
    internal const float BandMargin = 0.001f;
    const int Capacity = SupportFootprintResult.MaximumElements;

    static readonly CapsuleShape AxisProbe = new(AxisProbeRadius, ProbeLength);

    // The neighborhood spans and the foot probe shape, reused by every Find on this thread, so a warm Find allocates
    // nothing and zero fills nothing. Thread-static storage is never shared between threads. A nested Find on the same
    // thread, which only a physics world calling back into locomotion could start, takes fresh storage instead.
    [ThreadStatic] static ProbeScratch? t_scratch;

    static readonly SupportSample NoSupport =
        new(SupportStatus.None, float.NaN, float.NaN, Vector3.Zero, null, -1, Vector3.Zero);
    static readonly SupportSample RefusedSupport = NoSupport with { Status = SupportStatus.Refused };

    /// <summary>Samples analytic terrain once at the exact axis and forwards the value fact.</summary>
    internal static SupportSample Find(Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        in FootSupportQuery query)
    {
        Validate(query);
        FootAnalyticGround? analytic = null;
        if (groundHeight is not null)
        {
            SupportQueryDiagnostics.Record(SupportQueryCounter.AnalyticSamples);
            analytic = new(groundHeight(query.Axis.X, query.Axis.Y),
                groundNormal?.Invoke(query.Axis.X, query.Axis.Y) ?? Vector3.UnitY);
        }
        return Find(analytic, world, lease, query);
    }

    /// <summary>Describes both original proposal paths without reading physics or acquiring an interval.</summary>
    internal static SupportFootprintRequest DescribeFootprint(in FootSupportQuery query)
    {
        Validate(query);
        ProbeScratch scratch = t_scratch ??= new ProbeScratch();
        float lowest = query.FeetY + query.ReachUp + BandMargin;
        float distance = query.ReachUp + query.ReachDown + 2 * BandMargin;
        Vector2 axis = query.Axis;
        SupportFootprintProposal Proposal(CapsuleShape probe) => new(probe,
            Pose.At(new(axis.X, lowest + probe.Radius + ProbeLength / 2, axis.Y)), distance);
        return new(query.Axis, query.FootRadius, (double)query.FeetY - query.ReachDown,
            (double)query.FeetY + query.ReachUp, query.CosMaxSlope, SupportCertification.ContactBand,
            Proposal(AxisProbe), Proposal(scratch.FootProbe(query.FootRadius)));
    }

    /// <summary>Returns the certified support with the highest midpoint whose interval meets the inclusive reach
    /// band, walkable or steep, carrying its own status, else <see cref="SupportStatus.None"/>. Equal midpoints
    /// prefer walkable, then the witness nearest the axis, then terrain, then the lower static. A non-null
    /// <paramref name="world"/> must offer <see cref="IPhysicsSupportFootprint"/> and <paramref name="lease"/> must
    /// be its current read interval, which the backend authenticates before either proposal sweep. Every
    /// footprint contribution is offered. A proposal whose certified contact lies above the band, whose start is
    /// inside geometry, or whose contact neighborhood has no member supporting at or below the band top is a
    /// refusal at its lowest point. A refusal not certified at or below the selected support, a refused complete query
    /// or no support with any refusal returns <see cref="SupportStatus.Refused"/>. Nothing falls back to a contact
    /// neighborhood.</summary>
    internal static SupportSample Find(FootAnalyticGround? analytic, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        in FootSupportQuery query) => Find(analytic, world, lease, query, null);

    /// <summary>As <see cref="Find(FootAnalyticGround?, IPhysicsWorld?, IPhysicsQueryLease?, in FootSupportQuery)"/>,
    /// refusing with <see cref="NotSupportedException"/> when a term that can decide a published contribution
    /// in the band lies outside <paramref name="scope"/>.</summary>
    internal static SupportSample Find(FootAnalyticGround? analytic, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        in FootSupportQuery query, SupportFootprintBounds? scope)
    {
        SupportQueryDiagnostics.Record(SupportQueryCounter.Find);
        SupportFootprintRequest request = DescribeFootprint(query);
        var selection = new Selection(query);
        bool refused = false;
        if (world is not null)
        {
            ArgumentNullException.ThrowIfNull(lease);
            IPhysicsSupportFootprint features = world as IPhysicsSupportFootprint ?? throw new NotSupportedException(
                "Foot support needs a physics world that offers complete footprint queries.");
            ProbeScratch scratch = t_scratch!;
            if (scratch.InUse) scratch = new ProbeScratch();
            scratch.InUse = true;
            try
            {
                // A zero slope cosine or an unbounded column has no finite admission volume.
                refused = !SupportFootprintBounds.TryCreate(request, out _);
                if (!refused) Certify(features, lease, request, query, scope, scratch, ref selection, out refused);
            }
            finally { scratch.InUse = false; }
        }
        if (analytic is { } fact) selection.Terrain(fact);
        SupportSample sample = refused ? RefusedSupport : selection.Result();
        SupportQueryDiagnostics.ObserveFind(new(query.Axis, query.FeetY, query.FootRadius,
            query.ReachUp, query.ReachDown, query.CosMaxSlope, (byte)sample.Status, sample.Height, sample.HeightError));
        return sample;
    }

    // Runs the one complete query, applies both proposal guards and offers every certified contribution. A
    // refused query or certificate refuses the whole Find.
    static void Certify(IPhysicsSupportFootprint features, IPhysicsQueryLease lease, in SupportFootprintRequest request,
        in FootSupportQuery query, SupportFootprintBounds? scope, ProbeScratch scratch, ref Selection selection,
        out bool refused)
    {

        SupportFootprintResult result = features.QuerySupportFootprint(lease, request,
            scratch.Members, scratch.Joins, QueryFilter.StaticsOnly);
        FootprintScope? certified = scope is { } bounds ? new(bounds, request.BandLower, request.BandUpper) : null;
        int count;
        using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportCertification))
            count = SupportCertification.CertifyFootprint(features, lease, result, scratch.Members,
                scratch.Joins, query.Axis, query.CosMaxSlope, scratch.Contributions, scratch.Contexts, certified);
        refused = count < 0;
        if (refused) return;
        using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportSelection);
        Guard(result.AxisVerdict, 1, result.Members, scratch, request.BandUpper, ref selection);
        Guard(result.FootVerdict, 2, result.Members, scratch, request.BandUpper, ref selection);
        for (int i = 0; i < count; i++)
        {
            FootprintContribution item = scratch.Contributions[i];
            SupportContribution contribution = item.Contribution;
            if (contribution.Kind == CertifiedSupportKind.Refused) continue;
            selection.Offer(new Candidate(contribution.Kind == CertifiedSupportKind.Walkable
                ? SupportStatus.Walkable : SupportStatus.Steep, contribution.Lower, contribution.Upper,
                contribution.Normal, scratch.Members[item.MemberIndex].Static, contribution.FeatureId,
                contribution.Witness));
        }
    }

    // A proposal stopped above the band, or started inside geometry, hides anything lower. A contact stopped in
    // the band when its certified neighborhood holds a member supporting at or below the band top.
    static void Guard(in SupportProposalVerdict verdict, byte mask, int count, ProbeScratch scratch,
        double bandTop, ref Selection selection)
    {
        if (verdict.Status == SupportProposalStatus.Miss) return;
        if (verdict.Status == SupportProposalStatus.InvalidStart || verdict.LowestLower > bandTop)
        {
            selection.Refuse(verdict.LowestUpper);
            return;
        }
        for (int i = 0; i < count; i++)
            if ((scratch.Members[i].ProposalMask & mask) != 0 &&
                scratch.Contexts[i].Kind != CertifiedSupportKind.Refused && scratch.Contexts[i].Lower <= bandTop)
                return;
        selection.Refuse(verdict.LowestUpper);
    }

    static void Validate(in FootSupportQuery query)
    {
        if (!float.IsFinite(query.Axis.X) || !float.IsFinite(query.Axis.Y))
            throw new ArgumentOutOfRangeException(nameof(query), "The axis must be finite.");
        if (!float.IsFinite(query.FeetY))
            throw new ArgumentOutOfRangeException(nameof(query), "FeetY must be finite.");
        if (!float.IsFinite(query.FootRadius) || query.FootRadius < AxisProbeRadius)
            throw new ArgumentOutOfRangeException(nameof(query), "FootRadius must be at least 0.01 metres and finite.");
        if (!float.IsFinite(query.ReachUp) || query.ReachUp < 0)
            throw new ArgumentOutOfRangeException(nameof(query), "ReachUp must be nonnegative and finite.");
        if (!float.IsFinite(query.ReachDown) || query.ReachDown < 0)
            throw new ArgumentOutOfRangeException(nameof(query), "ReachDown must be nonnegative and finite.");
        if (!float.IsFinite(query.CosMaxSlope) || query.CosMaxSlope < 0 || query.CosMaxSlope > 1)
            throw new ArgumentOutOfRangeException(nameof(query), "CosMaxSlope must lie in [0, 1].");
        if (!float.IsFinite(query.FeetY + query.ReachUp + BandMargin + query.FootRadius + ProbeLength) ||
            !float.IsFinite(query.ReachUp + query.ReachDown + 2 * BandMargin))
            throw new ArgumentOutOfRangeException(nameof(query), "The probe band must be finite.");
    }

    // One probe's neighborhood storage, reused by both probes of a Find. Each query writes the published prefix
    // before certification and selection read it, so nothing left from an earlier query is ever read.
    sealed class ProbeScratch
    {
        CapsuleShape? _footProbe;

        internal SupportFootprintMember[] Members { get; } = new SupportFootprintMember[Capacity];
        internal ulong[] Joins { get; } = new ulong[Capacity * SupportFootprintResult.JoinWordsFor(Capacity)];
        internal FootprintContribution[] Contributions { get; } = new FootprintContribution[2 * Capacity];
        internal SupportContribution[] Contexts { get; } = new SupportContribution[Capacity];
        internal bool InUse { get; set; }

        // Capsule shapes are immutable, so the last foot probe serves every Find with the same radius.
        internal CapsuleShape FootProbe(float radius) =>
            _footProbe is { } probe && probe.Radius == radius ? probe : _footProbe = new CapsuleShape(radius, ProbeLength);
    }

    readonly record struct Candidate(SupportStatus Status, double Lower, double Upper, Vector3 Normal,
        StaticHandle? Static, int FeatureId, Vector3 Witness)
    {
        internal double Midpoint => (Lower + Upper) / 2;
    }

    struct Selection(in FootSupportQuery query)
    {
        readonly FootSupportQuery _query = query;
        readonly double _bandLower = (double)query.FeetY - query.ReachDown;
        readonly double _bandUpper = (double)query.FeetY + query.ReachUp;
        Candidate? _best;
        double _refusedTop = double.NegativeInfinity;

        // Analytic terrain is exact at the axis and needs no certificate.
        internal void Terrain(in FootAnalyticGround ground)
        {
            SupportStatus status = ground.Normal.Y < _query.CosMaxSlope ? SupportStatus.Steep : SupportStatus.Walkable;
            Offer(new Candidate(status, ground.Height, ground.Height, ground.Normal, null, -1,
                new Vector3(_query.Axis.X, ground.Height, _query.Axis.Y)));
        }

        // A refusal is the upper bound of the refused proposal's lowest point. One below the band bottom lies in
        // the sweep margin only, so it cannot hide support in the band.
        internal void Refuse(double height)
        {
            if (height < _bandLower) return;
            _refusedTop = Math.Max(_refusedTop, height);
        }

        internal void Offer(in Candidate candidate)
        {
            // The band is inclusive, so any overlap of the certified interval with it qualifies.
            if (!(candidate.Upper >= _bandLower && candidate.Lower <= _bandUpper)) return;
            if (_best is not Candidate current || Beats(candidate, current)) _best = candidate;
        }

        // Highest midpoint first. Ties prefer walkable, then the witness nearest the axis, then terrain, then the
        // lower static.
        readonly bool Beats(in Candidate challenger, in Candidate holder)
        {
            double a = challenger.Midpoint, b = holder.Midpoint;
            if (a != b) return a > b;
            if (challenger.Status != holder.Status) return challenger.Status == SupportStatus.Walkable;
            double da = AxisDistanceSquared(challenger.Witness), db = AxisDistanceSquared(holder.Witness);
            if (da != db) return da < db;
            if (challenger.Static is not StaticHandle challengerStatic) return holder.Static is not null;
            return holder.Static is StaticHandle holderStatic && challengerStatic.Value < holderStatic.Value;
        }

        readonly double AxisDistanceSquared(Vector3 witness)
        {
            double dx = (double)witness.X - _query.Axis.X, dz = (double)witness.Z - _query.Axis.Y;
            return dx * dx + dz * dz;
        }

        internal readonly SupportSample Result()
        {
            bool refused = _refusedTop != double.NegativeInfinity;
            if (_best is not Candidate chosen) return refused ? RefusedSupport : NoSupport;
            // The refusal holds unless the chosen support is certified at or above it.
            if (refused && _refusedTop > chosen.Lower) return RefusedSupport;
            float height = (float)chosen.Midpoint;
            // The error encloses both bounds from the rounded height, so it covers the midpoint's rounding too.
            double error = Math.Max(Outward(chosen.Upper - height), Outward(height - chosen.Lower));
            return new(chosen.Status, height, RoundUp(error), chosen.Normal, chosen.Static, chosen.FeatureId,
                chosen.Witness);
        }

        static double Outward(double difference) => difference > 0 ? Math.BitIncrement(difference) : 0;

        static float RoundUp(double value)
        {
            float rounded = (float)value;
            return rounded < value ? MathF.BitIncrement(rounded) : rounded;
        }
    }
}
