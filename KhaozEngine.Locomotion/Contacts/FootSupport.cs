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
/// Physics statics are proposed by downward probe capsules and certified from the whole support neighborhood at
/// each probe's contact.</summary>
internal static class FootSupport
{
    /// <summary>The radius of the thin probe capsule that proposes the surface under the axis itself. It is the
    /// neighborhood backend's minimum query radius (<c>BepuPhysicsWorld.SupportNeighborhood.cs</c>), so every axis
    /// proposal stays certifiable.</summary>
    internal const float AxisProbeRadius = 0.01f;

    // The probes' cylindrical segment. Its lower cap holds the probe's lowest point.
    const float ProbeLength = 0.01f;
    // The probes start this far above the band and sweep this far past it, so a surface exactly on either band
    // edge is reached by a sweep rather than landing on the sweep's start or end.
    const float BandMargin = 0.001f;
    const int Capacity = SupportNeighborhoodResult.MaximumElements;
    // The probe passes the back faces of one-sided mesh triangles, as the simulation's contacts do.
    static readonly QueryFilter ProbeFilter = QueryFilter.StaticsOnly with { CullBackFaces = true };

    static readonly CapsuleShape AxisProbe = new(AxisProbeRadius, ProbeLength);

    // The neighborhood spans and the foot probe shape, reused by every Find on this thread, so a warm Find allocates
    // nothing and zero fills nothing. Thread-static storage is never shared between threads. A nested Find on the same
    // thread, which only a physics world calling back into locomotion could start, takes fresh storage instead.
    [ThreadStatic] static ProbeScratch? t_scratch;

    static readonly SupportSample NoSupport =
        new(SupportStatus.None, float.NaN, float.NaN, Vector3.Zero, null, -1, Vector3.Zero);
    static readonly SupportSample RefusedSupport = NoSupport with { Status = SupportStatus.Refused };

    /// <summary>Returns the certified support with the highest midpoint whose interval meets the inclusive reach
    /// band, walkable or steep, carrying its own status, else <see cref="SupportStatus.None"/>. Equal midpoints
    /// prefer walkable, then the witness nearest the axis, then terrain, then the lower static. Every member of a
    /// probe's neighborhood offers its own contribution, and one wholly outside the band is not a candidate. A probe
    /// whose contact lies above the band, a refused neighborhood, or a neighborhood with no member that supports at
    /// or below the band top is a refused proposal at the probe's lowest point. A refused proposal above the selected
    /// support, or with no support at all, returns <see cref="SupportStatus.Refused"/>. A non-null
    /// <paramref name="world"/> must offer <see cref="IPhysicsCapsuleFeatures"/> and <paramref name="lease"/>
    /// must be its current read interval.</summary>
    internal static SupportSample Find(Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        in FootSupportQuery query)
    {
        Validate(query);
        IPhysicsCapsuleFeatures? features = null;
        if (world is not null)
        {
            ArgumentNullException.ThrowIfNull(lease);
            features = world as IPhysicsCapsuleFeatures ?? throw new NotSupportedException(
                "Foot support needs a physics world that offers capsule-feature queries.");
            lease.AssertCurrent();
            IPhysicsWorld source = world is IPhysicsWorldQueryView view ? view.SourceWorld : world;
            if (!ReferenceEquals(lease.SourceWorld, source) || lease.Origin != world.Origin)
                throw new InvalidOperationException("The read lease does not belong to this physics world.");
        }

        var selection = new Selection(query);
        if (groundHeight is not null) selection.Terrain(groundHeight, groundNormal);
        if (world is not null)
        {
            ProbeScratch scratch = t_scratch ??= new ProbeScratch();
            if (scratch.InUse) scratch = new ProbeScratch();
            scratch.InUse = true;
            try
            {
                Probe(world, features!, lease!, query, AxisProbe, scratch, ref selection);
                Probe(world, features!, lease!, query, scratch.FootProbe(query.FootRadius), scratch, ref selection);
            }
            finally
            {
                scratch.InUse = false;
            }
        }
        return selection.Result();
    }

    static void Validate(in FootSupportQuery query)
    {
        if (!float.IsFinite(query.Axis.X) || !float.IsFinite(query.Axis.Y))
            throw new ArgumentOutOfRangeException(nameof(query), "The axis must be finite.");
        if (!float.IsFinite(query.FeetY))
            throw new ArgumentOutOfRangeException(nameof(query), "FeetY must be finite.");
        if (!float.IsFinite(query.FootRadius) || query.FootRadius <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "FootRadius must be positive and finite.");
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

        internal SupportElement[] Elements { get; } = new SupportElement[Capacity];
        internal ulong[] Joins { get; } = new ulong[Capacity * SupportNeighborhoodResult.JoinWordsFor(Capacity)];
        internal SupportContribution[] Contributions { get; } = new SupportContribution[Capacity];
        internal bool InUse { get; set; }

        // Capsule shapes are immutable, so the last foot probe serves every Find with the same radius.
        internal CapsuleShape FootProbe(float radius) =>
            _footProbe is { } probe && probe.Radius == radius ? probe : _footProbe = new CapsuleShape(radius, ProbeLength);
    }

    // Sweeps one probe down the band. The hit leaves the probe exactly in contact, which is the pose whose support
    // neighborhood is certified. A neighborhood that cannot be certified is a refused proposal at its lowest point.
    static void Probe(IPhysicsWorld world, IPhysicsCapsuleFeatures features, IPhysicsQueryLease lease,
        in FootSupportQuery query, CapsuleShape capsule, ProbeScratch scratch, ref Selection selection)
    {
        float radius = capsule.Radius;
        float lowest = query.FeetY + query.ReachUp + BandMargin;
        float centreY = lowest + radius + ProbeLength / 2;
        Pose start = Pose.At(new Vector3(query.Axis.X, centreY, query.Axis.Y));
        if (!world.SweepCapsule(capsule, start, -Vector3.UnitY, query.ReachUp + query.ReachDown + 2 * BandMargin,
                out SweepHit hit, ProbeFilter))
            return;
        // A sweep that starts inside geometry reports no contact normal. Geometry inside the raised start refuses.
        if (hit.Distance == 0 && hit.Normal == Vector3.Zero)
        {
            selection.Refuse(lowest);
            return;
        }
        double contactLowest = (double)lowest - hit.Distance;
        if (hit.Body is not StaticHandle)
        {
            selection.Refuse(contactLowest);
            return;
        }
        Pose contact = Pose.At(new Vector3(query.Axis.X, centreY - hit.Distance, query.Axis.Y));
        SupportNeighborhoodResult result = features.QuerySupportNeighborhood(lease, capsule, contact,
            SupportCertification.ContactBand, scratch.Elements, scratch.Joins, QueryFilter.StaticsOnly);
        int count = SupportCertification.CertifyNeighborhood(features, lease, result, scratch.Elements,
            scratch.Joins, query.Axis, query.CosMaxSlope, scratch.Contributions);
        double bandTop = (double)query.FeetY + query.ReachUp;
        // The sweep stops at the first surface, so a contact above the band hides anything lower.
        if (count < 0 || contactLowest > bandTop)
        {
            selection.Refuse(contactLowest);
            return;
        }
        bool reached = false;
        for (int i = 0; i < count; i++)
        {
            SupportContribution contribution = scratch.Contributions[i];
            // A member wholly above the band is not a candidate. When another member reaches the band, that member
            // is what stopped the probe in the band.
            if (contribution.Kind == CertifiedSupportKind.Refused || contribution.Lower > bandTop) continue;
            reached = true;
            selection.Offer(new Candidate(
                contribution.Kind == CertifiedSupportKind.Walkable ? SupportStatus.Walkable : SupportStatus.Steep,
                contribution.Lower, contribution.Upper, contribution.Normal, scratch.Elements[i].Static,
                contribution.FeatureId, contribution.Witness));
        }
        // A surface that stopped the probe but supports nothing in reach, such as a ledge edge above the band
        // beside the axis, could hide support under the rest of the disc.
        if (!reached) selection.Refuse(contactLowest);
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
        internal void Terrain(Func<float, float, float> groundHeight, Func<float, float, Vector3>? groundNormal)
        {
            float height = groundHeight(_query.Axis.X, _query.Axis.Y);
            Vector3 normal = groundNormal?.Invoke(_query.Axis.X, _query.Axis.Y) ?? Vector3.UnitY;
            SupportStatus status = normal.Y < _query.CosMaxSlope ? SupportStatus.Steep : SupportStatus.Walkable;
            Offer(new Candidate(status, height, height, normal, null, -1,
                new Vector3(_query.Axis.X, height, _query.Axis.Y)));
        }

        // A refusal below the band bottom lies in the sweep margin only, so it cannot hide support in the band.
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
            if (refused && _refusedTop > chosen.Upper) return RefusedSupport;
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
