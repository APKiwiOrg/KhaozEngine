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
    private readonly ContactLoad[] _loads = new ContactLoad[checked(limits.MaxSupports *
        Math.Max(rig.Triangles.Length, rig.RestPositions.Length))];

    private readonly ClothContactVelocity _normalVelocity = new(limits.MaxContactCandidates, limits.MaxNormalVelocityChecks);
    private readonly Vector3[] _positionBias = new Vector3[rig.RestPositions.Length];

    internal ReadOnlySpan<Vector3> PositionBias => _positionBias;
    internal int Candidates { get; private set; }
    internal int Checks { get; private set; }
    internal int VelocityContacts => _normalVelocity.Count;
    internal int VelocityChecks => _normalVelocity.Checks;

    internal void Begin()
    {
        Array.Clear(_loads);
        Array.Clear(_positionBias);
        _normalVelocity.Begin();
        Candidates = 0;
        Checks = 0;
    }

    internal ClothStepStatus Project(Vector3[] opening, Vector3[] positions, in ClothSubstep step) =>
        Visit(opening, positions, step, verify: false);

    internal ClothStepStatus Complete(Vector3[] opening, Vector3[] positions, Vector3[] velocities, in ClothSubstep step)
    {
        ClothStepStatus status = Visit(opening, positions, step, verify: true);
        return status == ClothStepStatus.Accepted ? _normalVelocity.ProjectAndCheck(velocities) : status;
    }

    private ClothStepStatus Visit(Vector3[] opening, Vector3[] positions, in ClothSubstep step, bool verify)
    {
        for (int i = 0; i < step.ClosingSupports.Length; i++)
        {
            ClothSupport first = step.OpeningSupports[i], last = step.ClosingSupports[i];
            int offset = i * Math.Max(rig.Triangles.Length, positions.Length);
            ClothStepStatus status = last.IsPlane
                ? ProjectPlane(opening, positions, first, last, step.Seconds, offset, verify)
                : ProjectCapsule(opening, positions, first, last, step.Seconds, offset, verify);
            if (status != ClothStepStatus.Accepted) return status;
        }
        return ClothStepStatus.Accepted;
    }

    private ClothStepStatus ProjectPlane(Vector3[] opening, Vector3[] positions, in ClothSupport first,
        in ClothSupport last, float seconds, int offset, bool verify)
    {
        Vector3 normal = last.PlaneNormal;
        for (int i = 0; i < positions.Length; i++)
        {
            if (!CountCheck()) return ClothStepStatus.ContactWork;
            float distance = Vector3.Dot(positions[i] - last.Translation, normal);
            var node = new ClothTriangle(i, i, i);
            ref ContactLoad load = ref _loads[offset + i];
            if (verify)
            {
                if (distance < rig.Material.HalfThickness - SolveTolerance) return ClothStepStatus.UnresolvedContact;
                if (distance <= rig.Material.HalfThickness + ContactMargin + VelocityTouchRoundoff)
                {
                    if (!CountCandidate(ref load)) return ClothStepStatus.ContactCapacity;
                    ClothStepStatus status = AddVelocityWitness(node, Vector3.UnitX, normal,
                        positions[i] - distance * normal, first, last, seconds);
                    if (status != ClothStepStatus.Accepted) return status;
                }
                continue;
            }
            if (distance >= rig.Material.HalfThickness + ContactMargin)
            {
                load.Expire();
                continue;
            }
            if (!CountCandidate(ref load)) return ClothStepStatus.ContactCapacity;
            Vector3 supportPoint = positions[i] - distance * normal;
            float openingDistance = Vector3.Dot(opening[i] - first.Translation, first.PlaneNormal);
            var contact = new ContactPoint(node, Vector3.UnitX, 1, normal, openingDistance,
                distance, supportPoint);
            if (!ProjectPoint(opening, positions, first, last, contact, ref load))
                return ClothStepStatus.UnresolvedContact;
        }
        return ClothStepStatus.Accepted;
    }

    private ClothStepStatus ProjectCapsule(Vector3[] opening, Vector3[] positions, in ClothSupport first,
        in ClothSupport last, float seconds, int offset, bool verify)
    {
        ColliderCapsule before = first.WorldCapsule, now = last.WorldCapsule;
        var firstAxis = new Axis(before);
        var lastAxis = new Axis(now);
        float extent = now.Radius + rig.Material.HalfThickness + ContactMargin;
        float reach = verify ? extent + VelocityTouchRoundoff : extent;
        Vector3 min = Vector3.Min(Vector3.Min(before.Start, before.End), Vector3.Min(now.Start, now.End)) - new Vector3(reach);
        Vector3 max = Vector3.Max(Vector3.Max(before.Start, before.End), Vector3.Max(now.Start, now.End)) + new Vector3(reach);
        ReadOnlySpan<ClothTriangle> faces = rig.Triangles;
        for (int i = 0; i < faces.Length; i++)
        {
            ClothTriangle face = faces[i];
            ref ContactLoad load = ref _loads[offset + i];
            if (!Overlaps(face, opening, positions, min, max))
            {
                load.Expire();
                continue;
            }
            if (!CountCandidate(ref load)) return ClothStepStatus.ContactCapacity;
            if (!CountCheck()) return ClothStepStatus.ContactWork;

            var triangle = new ChainContactTriangle(positions[face.A], positions[face.B], positions[face.C]);
            ChainContactTriangle.Contact hit = triangle.Closest(lastAxis, out _);
            var firstTriangle = new ChainContactTriangle(opening[face.A], opening[face.B], opening[face.C]);
            Offset openingResidual = firstTriangle.MaterialResidual(firstAxis, hit);
            float distance = (float)Math.Sqrt(hit.Residual.LengthSquared);
            bool crossed = Offset.Dot(openingResidual, hit.Residual) < 0;
            // Follow the opening exterior when the same material witness crossed the axis.
            // Verification refuses even a separated closing state reached through that axis.
            if (verify)
            {
                if (crossed || distance == 0 || distance < now.Radius + rig.Material.HalfThickness - SolveTolerance)
                    return ClothStepStatus.UnresolvedContact;
                if (distance <= reach)
                {
                    Vector3 direction = (hit.Residual * (1d / distance)).ToVector3();
                    ClothStepStatus status = AddVelocityWitness(face, hit.Weights.ToVector3(), direction,
                        CapsulePoint(now, hit.Fraction, direction), first, last, seconds);
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
                openingDistance, distance - now.Radius, supportPoint);
            if (!ProjectPoint(opening, positions, first, last, contact, ref load))
                return ClothStepStatus.UnresolvedContact;
        }
        return ClothStepStatus.Accepted;
    }

    private ClothStepStatus AddVelocityWitness(ClothTriangle face, Vector3 weights, Vector3 normal, Vector3 supportPoint,
        in ClothSupport first, in ClothSupport last, float seconds)
    {
        float supportSpeed = Vector3.Dot(last.SurfaceDisplacement(first, supportPoint), normal) / seconds;
        return _normalVelocity.Add(face, weights, normal, supportSpeed, rig.InverseMasses);
    }

    private static Vector3 CapsulePoint(in ColliderCapsule capsule, AxisFraction fraction, Vector3 normal) =>
        (float)fraction.StartWeight * capsule.Start + (float)fraction.EndWeight * capsule.End + capsule.Radius * normal;

    private bool ProjectPoint(Vector3[] opening, Vector3[] positions, in ClothSupport first, in ClothSupport last,
        in ContactPoint contact, ref ContactLoad load)
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
                contact.Face.Blend(_positionBias, weights) - last.SurfaceDisplacement(first, contact.SupportPoint);
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

    private bool CountCheck()
    {
        if (Checks == limits.MaxContactChecks) return false;
        Checks++;
        return true;
    }

    private bool CountCandidate(ref ContactLoad load)
    {
        if (load.Counted) return true;
        if (Candidates == limits.MaxContactCandidates) return false;
        load.Counted = true;
        Candidates++;
        return true;
    }

    private static bool Overlaps(ClothTriangle face, Vector3[] first, Vector3[] last, Vector3 min, Vector3 max)
    {
        Vector3 low = Vector3.Min(Vector3.Min(first[face.A], first[face.B]), first[face.C]);
        low = Vector3.Min(low, Vector3.Min(Vector3.Min(last[face.A], last[face.B]), last[face.C]));
        Vector3 high = Vector3.Max(Vector3.Max(first[face.A], first[face.B]), first[face.C]);
        high = Vector3.Max(high, Vector3.Max(Vector3.Max(last[face.A], last[face.B]), last[face.C]));
        return low.X <= max.X && low.Y <= max.Y && low.Z <= max.Z &&
            high.X >= min.X && high.Y >= min.Y && high.Z >= min.Z;
    }

    private readonly record struct ContactPoint(ClothTriangle Face, Vector3 Weights, int Feature, Vector3 Normal,
        float OpeningDistance, float Distance, Vector3 SupportPoint);

    private struct ContactLoad
    {
        internal bool Counted;
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
