using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

// A capsule constrains the free triangle witness. A plane has a whole feature
// on each parallel face, so it constrains each vertex on that feature.
internal sealed class ClothContact(ClothRig rig, ClothWorkLimits limits)
{
    private const float ContactMargin = .00025f;
    private const float SolveTolerance = .00025f;
    private const float VelocityTouchRoundoff = .000001f;
    private readonly ContactLoad[] _loads = new ContactLoad[checked((limits.MaxSupports + limits.MaxHulls) *
        Math.Max(rig.Triangles.Length, rig.RestPositions.Length))];

    private readonly ClothContactWorkspace _workspace = new(limits);
    private readonly ClothContactVelocity _normalVelocity = new(limits.MaxContactCandidates, limits.MaxNormalVelocityChecks);
    private readonly ClothSelfContact? _self = limits.EnableSelfContact && rig.Triangles.Length > 1
        ? new ClothSelfContact(rig, limits.MaxContactChecks) : null;
    private readonly Vector3[] _positionBias = new Vector3[rig.RestPositions.Length];

    internal ReadOnlySpan<Vector3> PositionBias => _positionBias;
    internal int Candidates => _workspace.Candidates;
    internal int Checks => _workspace.Checks;
    internal int VelocityContacts => _normalVelocity.Count;
    internal int VelocityChecks => _normalVelocity.Checks;
    internal int SelfContactChecks { get; private set; }
    internal int SweepRefinements => _workspace.SweepRefinements;
    internal uint? PartId => _workspace.PartId;

    internal ClothStepStatus Prepare(in ClothSubstep step, bool closingOnly = false, bool allowCoarse = false) =>
        _workspace.Prepare(step, closingOnly, allowCoarse);

    internal void Begin()
    {
        Array.Clear(_loads);
        Array.Clear(_positionBias);
        _normalVelocity.Begin();
        _workspace.Begin();
        _self?.Begin();
        SelfContactChecks = 0;
    }

    internal ClothStepStatus Project(Vector3[] opening, Vector3[] positions, in ClothSubstep step) =>
        Visit(opening, positions, step, verify: false);

    internal ClothStepStatus Complete(Vector3[] opening, Vector3[] positions, Vector3[] velocities, in ClothSubstep step)
    {
        ClothStepStatus status = Visit(opening, positions, step, verify: true);
        if (status != ClothStepStatus.Accepted) return status;
        status = _normalVelocity.ProjectAndCheck(velocities);
        _workspace.PartId = _normalVelocity.PartId;
        return status;
    }

    internal ClothStepStatus VerifyFit(Vector3[] positions, in ClothSubstep step) =>
        Visit(positions, positions, step, verify: true, collectVelocity: false);

    internal ClothStepStatus VerifyDisplay(Vector3[] opening, Vector3[] positions, in ClothSubstep step) =>
        Visit(opening, positions, step, verify: true, collectVelocity: false);

    private ClothStepStatus Visit(Vector3[] opening, Vector3[] positions, in ClothSubstep step, bool verify,
        bool collectVelocity = true)
    {
        _workspace.PartId = null;
        int startChecks = _workspace.Checks;
        bool broadReady = _self is null || _self.Prepare(opening, positions, _workspace);
        SelfContactChecks += _workspace.Checks - startChecks;
        if (!broadReady) return ClothStepStatus.ContactWork;

        for (int i = 0; i < step.ClosingSupports.Length; i++)
        {
            ClothSupport first = step.OpeningSupports[i], last = step.ClosingSupports[i];
            if (_workspace.Replaced(last)) continue;
            _workspace.PartId = last.Identity == default ? null : last.Identity.PartId;
            int offset = i * Math.Max(rig.Triangles.Length, positions.Length);
            ClothStepStatus status = last.IsPlane
                ? ProjectPlane(opening, positions, first, last, step.Seconds, offset, verify, collectVelocity)
                : ProjectCapsule(opening, positions, first, last, step.Seconds, offset, verify, collectVelocity);
            if (status != ClothStepStatus.Accepted) return status;
        }
        int hullOffset = limits.MaxSupports * Math.Max(rig.Triangles.Length, positions.Length);
        foreach (ref readonly ClothPosedHull hull in _workspace.Hulls)
        {
            _workspace.PartId = hull.Identity.PartId;
            ClothStepStatus status = ProjectHull(opening, positions, hull, step.Seconds, hullOffset, verify, collectVelocity);
            if (status != ClothStepStatus.Accepted) return status;
            hullOffset += Math.Max(rig.Triangles.Length, positions.Length);
        }
        _workspace.PartId = null;
        if (_self is null) return ClothStepStatus.Accepted;
        int priorChecks = _workspace.Checks;
        ClothStepStatus self = _self.Visit(opening, positions, _positionBias, _workspace, _normalVelocity, verify, collectVelocity);
        SelfContactChecks += _workspace.Checks - priorChecks;
        return self;
    }

    private ClothStepStatus ProjectPlane(Vector3[] opening, Vector3[] positions, in ClothSupport first,
        in ClothSupport last, float seconds, int offset, bool verify, bool collectVelocity)
    {
        Vector3 normal = last.PlaneNormal;
        for (int i = 0; i < positions.Length; i++)
        {
            if (!_workspace.Visit()) return ClothStepStatus.ContactWork;
            float distance = Vector3.Dot(positions[i] - last.Translation, normal);
            var node = new ClothTriangle(i, i, i);
            ref ContactLoad load = ref _loads[offset + i];
            if (verify)
            {
                if (distance < rig.Material.HalfThickness - SolveTolerance) return ClothStepStatus.UnresolvedContact;
                if (distance <= rig.Material.HalfThickness + ContactMargin + VelocityTouchRoundoff)
                {
                    if (!_workspace.Candidate()) return ClothStepStatus.ContactCapacity;
                    if (!collectVelocity) continue;
                    Vector3 motion = last.SurfaceDisplacement(first, positions[i] - distance * normal);
                    ClothStepStatus status = AddVelocityWitness(node, Vector3.UnitX, normal, motion, seconds);
                    if (status != ClothStepStatus.Accepted) return status;
                }
                continue;
            }
            if (distance >= rig.Material.HalfThickness + ContactMargin)
            {
                load.Expire();
                continue;
            }
            if (!_workspace.Candidate()) return ClothStepStatus.ContactCapacity;
            Vector3 supportPoint = positions[i] - distance * normal;
            float openingDistance = Vector3.Dot(opening[i] - first.Translation, first.PlaneNormal);
            var contact = new ContactPoint(node, Vector3.UnitX, 1, normal, openingDistance,
                distance, last.SurfaceDisplacement(first, supportPoint));
            if (!ProjectPoint(opening, positions, contact, ref load))
                return ClothStepStatus.UnresolvedContact;
        }
        return ClothStepStatus.Accepted;
    }

    private ClothStepStatus ProjectCapsule(Vector3[] opening, Vector3[] positions, in ClothSupport first,
        in ClothSupport last, float seconds, int offset, bool verify, bool collectVelocity)
    {
        ColliderCapsule before = first.WorldCapsule, now = last.WorldCapsule;
        var firstAxis = new Axis(before);
        var lastAxis = new Axis(now);
        float extent = now.Radius + rig.Material.HalfThickness + ContactMargin;
        float reach = verify ? extent + VelocityTouchRoundoff : extent;
        var supportBounds = new ClothCapsuleSweep(default, default, first, last).Bounds
            .Expand(reach - now.Radius);
        ClothSelfBroadPhase? broadPhase = _self?.BroadPhase;
        if (broadPhase is not null && !broadPhase.CollectSupport(supportBounds, _workspace))
            return ClothStepStatus.ContactWork;
        ReadOnlySpan<int> indices = broadPhase is not null ? broadPhase.SupportFaces : [];
        ReadOnlySpan<ClothTriangle> faces = rig.Triangles;
        int count = broadPhase is not null ? indices.Length : faces.Length;
        for (int candidate = 0; candidate < count; candidate++)
        {
            if (!_workspace.Visit()) return ClothStepStatus.ContactWork;
            int i = broadPhase is not null ? indices[candidate] : candidate;
            ClothTriangle face = faces[i];
            ref ContactLoad load = ref _loads[offset + i];
            var previousFacet = new ClothFacet(face, opening);
            var currentFacet = new ClothFacet(face, positions);
            if (!ClothBounds.Enclose(previousFacet.Bounds, currentFacet.Bounds).Overlaps(supportBounds))
            {
                load.Expire();
                continue;
            }
            if (!_workspace.Candidate()) return ClothStepStatus.ContactCapacity;

            var triangle = new ChainContactTriangle(positions[face.A], positions[face.B], positions[face.C]);
            ChainContactTriangle.Contact hit = triangle.Closest(lastAxis, out _);
            var firstTriangle = new ChainContactTriangle(opening[face.A], opening[face.B], opening[face.C]);
            Offset openingResidual = firstTriangle.MaterialResidual(firstAxis, hit);
            float distance = (float)Math.Sqrt(hit.Residual.LengthSquared);
            bool crossed = Offset.Dot(openingResidual, hit.Residual) < 0;
            // Keep the opening exterior even on a path whose two sampled ends are clear.
            float required = MathF.Max(.0000001f, rig.Material.HalfThickness - SolveTolerance);
            if (distance >= now.Radius + required)
            {
                var pair = new ClothCapsuleSweep(previousFacet, currentFacet, first, last);
                ClothStepStatus swept = _workspace.CheckSweep(pair, required, out Vector3 side);
                if (swept == ClothStepStatus.UnresolvedContact && !verify && side != Vector3.Zero)
                {
                    float target = ClothContactSweep.ClosingTarget(pair, side, rig.Material.HalfThickness + ContactMargin);
                    float surface = currentFacet.MinProjection(side) - pair.Gap(1, side);
                    if (!ProjectSweepPlane(face, positions, side, surface + target)) return swept;
                    load.Expire();
                    continue;
                }
                if (swept != ClothStepStatus.Accepted) return swept;
                crossed = false;
            }

            if (verify)
            {
                if (crossed || distance == 0 || distance < now.Radius + required)
                    return ClothStepStatus.UnresolvedContact;
                if (collectVelocity && distance <= reach)
                {
                    Vector3 direction = (hit.Residual * (1d / distance)).ToVector3();
                    Vector3 motion = last.CapsuleDisplacement(first, CapsulePoint(now, hit.Fraction, direction), hit.Fraction, direction);
                    ClothStepStatus status = AddVelocityWitness(face, hit.Weights.ToVector3(), direction, motion, seconds);
                    if (status != ClothStepStatus.Accepted) return status;
                }
                continue;
            }

            if (!crossed && distance >= extent)
            {
                load.Expire();
                continue;
            }
            Vector3 normal;
            if (crossed || distance == 0)
            {
                if (openingResidual.LengthSquared == 0) return ClothStepStatus.UnresolvedContact;
                normal = (openingResidual * (1 / Math.Sqrt(openingResidual.LengthSquared))).ToVector3();
                distance = Vector3.Dot(hit.Residual.ToVector3(), normal);
            }
            else normal = (hit.Residual * (1d / distance)).ToVector3();

            Vector3 supportPoint = CapsulePoint(now, hit.Fraction, normal);
            Vector3 weights = hit.Weights.ToVector3();
            int triangleFeature = (weights.X > 0 ? 1 : 0) | (weights.Y > 0 ? 2 : 0) | (weights.Z > 0 ? 4 : 0);
            int axisFeature = hit.Fraction.EndWeight == 0 ? 1 : hit.Fraction.StartWeight == 0 ? 2 : 3;
            float openingDistance = (float)Math.Sqrt(openingResidual.LengthSquared) - before.Radius;
            var contact = new ContactPoint(face, weights, triangleFeature | (axisFeature << 3), normal,
                openingDistance, distance - now.Radius, last.CapsuleDisplacement(first, supportPoint, hit.Fraction, normal));
            if (!ProjectPoint(opening, positions, contact, ref load))
                return ClothStepStatus.UnresolvedContact;
        }
        return ClothStepStatus.Accepted;
    }

    private ClothStepStatus ProjectHull(Vector3[] opening, Vector3[] positions, in ClothPosedHull hull,
        float seconds, int offset, bool verify, bool collectVelocity)
    {
        float extent = rig.Material.HalfThickness + ContactMargin + VelocityTouchRoundoff;
        float required = MathF.Max(.0000001f, rig.Material.HalfThickness - SolveTolerance);
        ClothBounds bounds = hull.SweptBounds.Expand(extent);
        ClothSelfBroadPhase? broadPhase = _self?.BroadPhase;
        if (broadPhase is not null && !broadPhase.CollectSupport(bounds, _workspace))
            return ClothStepStatus.ContactWork;
        ReadOnlySpan<int> indices = broadPhase is not null ? broadPhase.SupportFaces : [];
        ReadOnlySpan<ClothTriangle> faces = rig.Triangles;
        int count = broadPhase is not null ? indices.Length : faces.Length;
        for (int candidate = 0; candidate < count; candidate++)
        {
            if (!_workspace.Visit()) return ClothStepStatus.ContactWork;
            int i = broadPhase is not null ? indices[candidate] : candidate;
            ClothTriangle face = faces[i];
            ref ContactLoad load = ref _loads[offset + i];
            var before = new ClothFacet(face, opening);
            var after = new ClothFacet(face, positions);
            if (!ClothBounds.Enclose(before.Bounds, after.Bounds).Overlaps(bounds))
            {
                load.Expire();
                continue;
            }
            if (!_workspace.Candidate()) return ClothStepStatus.ContactCapacity;

            var pair = new ClothHullSweep(before, after, hull);
            ClothHullSweep.HullContact hit = pair.Witness(1);
            ClothSweepSample prior = pair.Sample(0);
            if (hit.Distance >= required || verify)
            {
                ClothStepStatus swept = _workspace.CheckSweep(pair, required, out Vector3 side);
                if (swept != ClothStepStatus.Accepted)
                {
                    if (verify || swept != ClothStepStatus.UnresolvedContact || side == Vector3.Zero ||
                        !ProjectToOpeningExterior(face, positions, after, pair, side))
                        return swept;
                    load.Expire();
                    continue;
                }
            }
            else if (hit.Distance <= 0 || Vector3.Dot(prior.Normal, hit.Normal) <= 0)
            {
                if (prior.Normal == Vector3.Zero || !ProjectToOpeningExterior(face, positions, after, pair, prior.Normal))
                    return ClothStepStatus.UnresolvedContact;
                load.Expire();
                continue;
            }

            if (hit.Distance > extent)
            {
                load.Expire();
                continue;
            }

            Vector3 motion = hull.SurfaceDisplacement(hit.LocalPoint);
            if (verify)
            {
                if (!collectVelocity) continue;
                ClothStepStatus status = AddVelocityWitness(face, hit.Weights, hit.Normal, motion, seconds);
                if (status != ClothStepStatus.Accepted) return status;
                continue;
            }

            float openingDistance = Vector3.Dot(face.Blend(opening, hit.Weights) - hull.Opening.World(hit.LocalPoint), hit.Normal);
            var contact = new ContactPoint(face, hit.Weights, hit.Feature, hit.Normal, openingDistance, hit.Distance, motion);
            if (hit.Distance < rig.Material.HalfThickness + ContactMargin &&
                !ProjectPoint(opening, positions, contact, ref load))
                return ClothStepStatus.UnresolvedContact;
        }
        return ClothStepStatus.Accepted;
    }

    private bool ProjectToOpeningExterior<T>(ClothTriangle face, Vector3[] positions, in ClothFacet after,
        in T pair, Vector3 side) where T : struct, IClothSweepPair
    {
        float target = ClothContactSweep.ClosingTarget(pair, side, rig.Material.HalfThickness + ContactMargin);
        float surface = after.MinProjection(side) - pair.Gap(1, side);
        return ProjectSweepPlane(face, positions, side, surface + target);
    }

    private ClothStepStatus AddVelocityWitness(ClothTriangle face, Vector3 weights, Vector3 normal,
        Vector3 supportDisplacement, float seconds)
    {
        float supportSpeed = Vector3.Dot(supportDisplacement, normal) / seconds;
        return _normalVelocity.Add(face, weights, normal, supportSpeed, rig.InverseMasses, _workspace.PartId);
    }

    private static Vector3 CapsulePoint(in ColliderCapsule capsule, AxisFraction fraction, Vector3 normal) =>
        (float)fraction.StartWeight * capsule.Start + (float)fraction.EndWeight * capsule.End + capsule.Radius * normal;

    private bool ProjectPoint(Vector3[] opening, Vector3[] positions, in ContactPoint contact, ref ContactLoad load)
    {
        var (a, b, c) = contact.Face;
        Vector3 weights = contact.Weights;
        var mass = new Vector3(rig.InverseMasses[a], rig.InverseMasses[b], rig.InverseMasses[c]);
        Vector3 share = weights * mass;
        float inverseMass = Vector3.Dot(weights, share);
        if (inverseMass == 0)
        {
            load.Expire();
            return contact.Distance >= rig.Material.HalfThickness - SolveTolerance;
        }

        load.Use(contact.Feature, contact.Normal);
        float envelope = rig.Material.HalfThickness + ContactMargin;
        float correction = envelope - contact.Distance;
        // Resolve closing travel physically. Clearance already missing at the opening is
        // positioning bias, so it supplies neither velocity nor a friction budget.
        float physical = MathF.Max(0, MathF.Min(envelope, contact.OpeningDistance) - contact.Distance);
        load.Normal += physical / inverseMass;
        Apply(positions, (correction / inverseMass) * contact.Normal);
        Apply(_positionBias, ((correction - physical) / inverseMass) * contact.Normal);

        if (rig.Material.StaticFriction > 0)
        {
            Vector3 motion = contact.Face.Blend(positions, weights) - contact.Face.Blend(opening, weights) -
                contact.Face.Blend(_positionBias, weights) - contact.SupportDisplacement;
            Vector3 tangent = motion - Vector3.Dot(motion, contact.Normal) * contact.Normal;
            Vector3 previous = load.Tangent - Vector3.Dot(load.Tangent, contact.Normal) * contact.Normal;
            Vector3 desired = previous - tangent / inverseMass;
            float desiredLength = desired.Length();
            load.Tangent = desiredLength > rig.Material.StaticFriction * load.Normal
                ? (rig.Material.KineticFriction * load.Normal / desiredLength) * desired
                : desired;
            Apply(positions, load.Tangent - previous);
        }
        return true;

        void Apply(Vector3[] target, Vector3 displacement)
        {
            target[a] += share.X * displacement;
            target[b] += share.Y * displacement;
            target[c] += share.Z * displacement;
        }
    }

    private bool ProjectSweepPlane(ClothTriangle face, Vector3[] positions, Vector3 normal, float minimum)
    {
        return ProjectNode(face.A) && ProjectNode(face.B) && ProjectNode(face.C);

        bool ProjectNode(int node)
        {
            float correction = minimum - Vector3.Dot(positions[node], normal);
            if (correction <= 0) return true;
            if (rig.InverseMasses[node] == 0) return false;
            Vector3 change = correction * normal;
            positions[node] += change;
            _positionBias[node] += change;
            return true;
        }
    }

    private readonly record struct ContactPoint(ClothTriangle Face, Vector3 Weights, int Feature, Vector3 Normal,
        float OpeningDistance, float Distance, Vector3 SupportDisplacement);

    private struct ContactLoad
    {
        internal float Normal;
        internal Vector3 Tangent;
        private int _feature;
        private Vector3 _direction;

        internal void Use(int feature, Vector3 direction)
        {
            if (_feature != feature || Vector3.Dot(_direction, direction) <= 0)
                Expire();
            _feature = feature;
            _direction = direction;
        }

        internal void Expire()
        {
            _feature = 0;
            Normal = 0;
            Tangent = Vector3.Zero;
        }
    }
}
