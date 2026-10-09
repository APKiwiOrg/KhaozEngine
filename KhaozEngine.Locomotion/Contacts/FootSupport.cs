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
/// Physics statics are proposed by downward probe capsules and bound to a face only by certification.</summary>
internal static class FootSupport
{
    /// <summary>The radius of the thin probe capsule that proposes the surface under the axis itself. It is the
    /// feature backend's minimum query radius (<c>BepuPhysicsWorld.CapsuleFeatures.cs</c>), so every axis
    /// proposal stays certifiable.</summary>
    internal const float AxisProbeRadius = 0.01f;

    // The probes' cylindrical segment. Its lower cap holds the probe's lowest point.
    const float ProbeLength = 0.01f;
    const int FaceCapacity = 256;

    static readonly SupportSample NoSupport =
        new(SupportStatus.None, float.NaN, float.NaN, Vector3.Zero, null, -1, Vector3.Zero);
    static readonly SupportSample RefusedSupport = NoSupport with { Status = SupportStatus.Refused };

    /// <summary>Returns the certified support with the highest midpoint within the reach band, walkable or
    /// steep, carrying its own status, else <see cref="SupportStatus.None"/>. Equal midpoints prefer walkable,
    /// then the witness nearest the axis, then terrain, then the lower static. A refused proposal above the
    /// selected support, or with no support at all, returns <see cref="SupportStatus.Refused"/>. A non-null <paramref name="world"/> must
    /// offer <see cref="IPhysicsCapsuleFeatures"/> and <paramref name="lease"/> must be its current read
    /// interval.</summary>
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
            Span<CapsuleIncidentFace> faces = stackalloc CapsuleIncidentFace[FaceCapacity];
            Probe(world, features!, lease!, query, AxisProbeRadius, faces, ref selection);
            Probe(world, features!, lease!, query, query.FootRadius, faces, ref selection);
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
        if (!float.IsFinite(query.FeetY + query.ReachUp + query.FootRadius + ProbeLength) ||
            !float.IsFinite(query.ReachUp + query.ReachDown))
            throw new ArgumentOutOfRangeException(nameof(query), "The probe band must be finite.");
    }

    // Sweeps one probe down the band. The hit leaves the probe exactly in contact, which is the pose the
    // feature query certifies. Anything that cannot be certified is a refused proposal at its lowest point.
    static void Probe(IPhysicsWorld world, IPhysicsCapsuleFeatures features, IPhysicsQueryLease lease,
        in FootSupportQuery query, float radius, Span<CapsuleIncidentFace> faces, ref Selection selection)
    {
        var capsule = new CapsuleShape(radius, ProbeLength);
        float lowest = query.FeetY + query.ReachUp;
        float centreY = lowest + radius + ProbeLength / 2;
        Pose start = Pose.At(new Vector3(query.Axis.X, centreY, query.Axis.Y));
        if (!world.SweepCapsule(capsule, start, -Vector3.UnitY, query.ReachUp + query.ReachDown,
                out SweepHit hit, QueryFilter.StaticsOnly))
            return;
        // A sweep that starts inside geometry reports no contact normal.
        if (hit.Distance == 0 && hit.Normal == Vector3.Zero)
        {
            selection.Refuse(lowest);
            return;
        }
        double contactLowest = (double)lowest - hit.Distance;
        if (hit.Body is not StaticHandle target)
        {
            selection.Refuse(contactLowest);
            return;
        }
        Pose contact = Pose.At(new Vector3(query.Axis.X, centreY - hit.Distance, query.Axis.Y));
        CapsuleFeatureResult result = features.QueryCapsuleFeature(lease, target, capsule, contact,
            SupportCertification.ContactBand, faces, QueryFilter.StaticsOnly);
        SupportContribution contribution = SupportCertification.Certify(features, lease, result,
            faces[..result.Written], query.Axis, query.CosMaxSlope);
        if (contribution.Kind == CertifiedSupportKind.Refused)
        {
            selection.Refuse(contactLowest);
            return;
        }
        selection.Offer(new Candidate(
            contribution.Kind == CertifiedSupportKind.Walkable ? SupportStatus.Walkable : SupportStatus.Steep,
            contribution.Lower, contribution.Upper, contribution.Normal, target, contribution.FeatureId,
            contribution.Witness));
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

        internal void Refuse(double height) => _refusedTop = Math.Max(_refusedTop, height);

        internal void Offer(in Candidate candidate)
        {
            double midpoint = candidate.Midpoint;
            if (!(midpoint >= _bandLower && midpoint <= _bandUpper)) return;
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
