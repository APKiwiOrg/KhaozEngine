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
    private readonly ContactLoad[] _loads = new ContactLoad[checked(limits.MaxSupports *
        Math.Max(rig.Triangles.Length, rig.RestPositions.Length))];

    internal int Candidates { get; private set; }
    internal int Checks { get; private set; }

    internal void Begin()
    {
        Array.Clear(_loads);
        Candidates = 0;
        Checks = 0;
    }

    internal ClothStepStatus Project(Vector3[] opening, Vector3[] positions, in ClothSubstep step, bool verify)
    {
        for (int i = 0; i < step.ClosingSupports.Length; i++)
        {
            ClothSupport first = step.OpeningSupports[i], last = step.ClosingSupports[i];
            int offset = i * Math.Max(rig.Triangles.Length, positions.Length);
            ClothStepStatus status = last.IsPlane
                ? ProjectPlane(opening, positions, first, last, offset, verify)
                : ProjectCapsule(opening, positions, first, last, offset, verify);
            if (status != ClothStepStatus.Accepted) return status;
        }
        return ClothStepStatus.Accepted;
    }

    private ClothStepStatus ProjectPlane(Vector3[] opening, Vector3[] positions, in ClothSupport first,
        in ClothSupport last, int offset, bool verify)
    {
        Vector3 normal = last.PlaneNormal;
        for (int i = 0; i < positions.Length; i++)
        {
            if (!CountCheck()) return ClothStepStatus.ContactWork;
            float distance = Vector3.Dot(positions[i] - last.Translation, normal);
            if (verify)
            {
                if (distance < rig.Material.HalfThickness - SolveTolerance) return ClothStepStatus.UnresolvedContact;
                continue;
            }
            if (distance >= rig.Material.HalfThickness + ContactMargin) continue;
            ref ContactLoad load = ref _loads[offset + i];
            if (!CountCandidate(ref load)) return ClothStepStatus.ContactCapacity;
            Vector3 supportPoint = positions[i] - distance * normal;
            var contact = new ContactPoint(new ClothTriangle(i, i, i), Vector3.UnitX, normal, distance, supportPoint);
            if (!ProjectPoint(opening, positions, first, last, contact, ref load))
                return ClothStepStatus.UnresolvedContact;
        }
        return ClothStepStatus.Accepted;
    }

    private ClothStepStatus ProjectCapsule(Vector3[] opening, Vector3[] positions, in ClothSupport first,
        in ClothSupport last, int offset, bool verify)
    {
        ColliderCapsule before = first.WorldCapsule, now = last.WorldCapsule;
        var firstAxis = new Axis(before);
        var lastAxis = new Axis(now);
        float extent = now.Radius + rig.Material.HalfThickness + ContactMargin;
        Vector3 min = Vector3.Min(Vector3.Min(before.Start, before.End), Vector3.Min(now.Start, now.End)) - new Vector3(extent);
        Vector3 max = Vector3.Max(Vector3.Max(before.Start, before.End), Vector3.Max(now.Start, now.End)) + new Vector3(extent);
        ReadOnlySpan<ClothTriangle> faces = rig.Triangles;
        for (int i = 0; i < faces.Length; i++)
        {
            ClothTriangle face = faces[i];
            if (!Overlaps(face, opening, positions, min, max)) continue;
            ref ContactLoad load = ref _loads[offset + i];
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
                if (crossed || distance < now.Radius + rig.Material.HalfThickness - SolveTolerance)
                    return ClothStepStatus.UnresolvedContact;
                continue;
            }

            if (!crossed && distance >= extent) continue;
            Vector3 normal;
            if (crossed || distance == 0)
            {
                if (openingResidual.LengthSquared == 0) return ClothStepStatus.UnresolvedContact;
                normal = (openingResidual * (1 / Math.Sqrt(openingResidual.LengthSquared))).ToVector3();
                distance = Vector3.Dot(hit.Residual.ToVector3(), normal);
            }
            else normal = (hit.Residual * (1d / distance)).ToVector3();

            Vector3 supportPoint = (float)hit.Fraction.StartWeight * now.Start + (float)hit.Fraction.EndWeight * now.End +
                now.Radius * normal;
            var contact = new ContactPoint(face, hit.Weights.ToVector3(), normal, distance - now.Radius, supportPoint);
            if (!ProjectPoint(opening, positions, first, last, contact, ref load))
                return ClothStepStatus.UnresolvedContact;
        }
        return ClothStepStatus.Accepted;
    }

    private bool ProjectPoint(Vector3[] opening, Vector3[] positions, in ClothSupport first, in ClothSupport last,
        in ContactPoint contact, ref ContactLoad load)
    {
        var (a, b, c) = contact.Face;
        Vector3 weights = contact.Weights;
        var mass = new Vector3(rig.InverseMasses[a], rig.InverseMasses[b], rig.InverseMasses[c]);
        Vector3 share = weights * mass;
        float inverseMass = Vector3.Dot(weights, share);
        if (inverseMass == 0)
            return contact.Distance >= rig.Material.HalfThickness - SolveTolerance;

        float normalImpulse = (rig.Material.HalfThickness + ContactMargin - contact.Distance) / inverseMass;
        load.Normal += normalImpulse;
        Apply(normalImpulse * contact.Normal);

        if (rig.Material.StaticFriction > 0)
        {
            Vector3 motion = contact.Face.Blend(positions, weights) - contact.Face.Blend(opening, weights) -
                last.SurfaceDisplacement(first, contact.SupportPoint);
            Vector3 tangent = motion - Vector3.Dot(motion, contact.Normal) * contact.Normal;
            Vector3 previous = load.Tangent - Vector3.Dot(load.Tangent, contact.Normal) * contact.Normal;
            Vector3 desired = previous - tangent / inverseMass;
            float desiredLength = desired.Length();
            load.Tangent = desiredLength > rig.Material.StaticFriction * load.Normal
                ? (rig.Material.KineticFriction * load.Normal / desiredLength) * desired
                : desired;
            Apply(load.Tangent - previous);
        }
        return true;

        void Apply(Vector3 correction)
        {
            positions[a] += share.X * correction;
            positions[b] += share.Y * correction;
            positions[c] += share.Z * correction;
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

    private readonly record struct ContactPoint(ClothTriangle Face, Vector3 Weights, Vector3 Normal, float Distance,
        Vector3 SupportPoint);

    private struct ContactLoad
    {
        internal bool Counted;
        internal float Normal;
        internal Vector3 Tangent;
    }
}
