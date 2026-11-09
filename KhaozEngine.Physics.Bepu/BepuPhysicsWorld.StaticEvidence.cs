using System;
using System.Linq;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld : IPhysicsStaticEvidence
{
    public PhysicsEvidenceStatus CaptureStaticEvidence(IPhysicsQueryLease lease, Vector3 minimum, Vector3 maximum,
        int maximumFeatures, PhysicsEvidenceVisitor visitor)
        => CaptureStaticEvidenceCore(lease, minimum, maximum, maximumFeatures, visitor, null);

    PhysicsEvidenceStatus CaptureStaticEvidenceCore(IPhysicsQueryLease lease, Vector3 minimum, Vector3 maximum,
        int maximumFeatures, PhysicsEvidenceVisitor visitor, StaticQueryExclusions? exclusions, bool indexed = true)
    {
        AuthenticateFeatureLease(lease);
        ArgumentNullException.ThrowIfNull(visitor);
        if (!Finite(minimum) || !Finite(maximum) || minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z)
            throw new ArgumentException("The capture bounds must be finite and ordered.", nameof(minimum));
        if (maximumFeatures < 1) throw new ArgumentOutOfRangeException(nameof(maximumFeatures));
        using QueryOperation operation = EnterQuery();
        if (!SweepRecordCountsMatch() || !SelectedSweepBoundsProved(QueryMobility.Statics, null))
            return PhysicsEvidenceStatus.Unavailable;
        if (indexed && _sweepEvidence.IsCurrent(this, _queryGeneration) &&
            _staticEvidence.Count == _handles.Count && _unprovedStaticEvidenceBounds.Count == 0 &&
            CapsuleGeneralSweepTraversal.TryValidate(_sim.BroadPhase.StaticTree, minimum, maximum) &&
            CapsuleGeneralSweepTraversal.TryValidate(_sim.BroadPhase.ActiveTree, minimum, maximum))
            return CaptureIndexedEvidence(lease, minimum, maximum, maximumFeatures, visitor, exclusions, operation.Scratch);
        return CaptureFullEvidence(lease, minimum, maximum, maximumFeatures, visitor, exclusions);
    }

    PhysicsEvidenceStatus CaptureFullEvidence(IPhysicsQueryLease lease, Vector3 minimum, Vector3 maximum,
        int maximumFeatures, PhysicsEvidenceVisitor visitor, StaticQueryExclusions? exclusions)
    {
        int features = 0;
        foreach (var row in _handles.OrderBy(row => row.Key))
        {
            StaticEvidenceDiagnostics.Owner();
            int broad = _sim.Statics[_sim.Statics.HandleToIndex[row.Value.Handle.Value]].BroadPhaseIndex;
            if (!ReadSweepStoredBounds(_sim.BroadPhase.StaticTree, broad, out var min, out var max))
                return PhysicsEvidenceStatus.Unavailable;
            if (!EvidenceOverlap(min, max, minimum, maximum)) continue;
            var reference = new CollidableReference(row.Value.Handle);
            if (!TryGetInstalledSweepGeometry(reference, out var geometry) || geometry is null)
                return PhysicsEvidenceStatus.Unsupported;
            _sim.Statics.GetDescription(row.Value.Handle, out var description);
            var status = EmitEvidence(new(row.Key), geometry, description.Pose, minimum, maximum,
                maximumFeatures, ref features, Visit);
            if (status != PhysicsEvidenceStatus.Complete) return status;
        }
        lease.AssertCurrent();
        return PhysicsEvidenceStatus.Complete;

        bool Visit(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> points) =>
            (exclusions?.ExcludesSweepStatic(face.Static.Value) ?? false) || visitor(face, points);
    }

    static bool EvidenceOverlap(Vector3 a, Vector3 b, Vector3 min, Vector3 max) =>
        a.X <= max.X && b.X >= min.X && a.Y <= max.Y && b.Y >= min.Y && a.Z <= max.Z && b.Z >= min.Z;

    PhysicsEvidenceStatus EmitEvidence(StaticHandle owner, InstalledSweepGeometry geometry, RigidPose root,
        Vector3 minimum, Vector3 maximum, int limit, ref int count, PhysicsEvidenceVisitor visitor)
    {
        if (geometry.Children is { } children)
        {
            foreach (var child in children)
            {
                var status = EmitEvidence(owner, child, root, minimum, maximum, limit, ref count, visitor);
                if (status != PhysicsEvidenceStatus.Complete) return status;
            }
            return PhysicsEvidenceStatus.Complete;
        }
        if (!geometry.TryComposePose(root, out var pose) || !InstalledPoseOperator.TryCreateSupport(pose, out var map))
            return PhysicsEvidenceStatus.Unsupported;
        if (geometry.Curved is { } curved)
        {
            if (++count > limit) return PhysicsEvidenceStatus.CapacityExceeded;
            var kind = curved == SupportCurvedShape.Sphere ? PhysicsEvidenceKind.Sphere :
                curved == SupportCurvedShape.Capsule ? PhysicsEvidenceKind.Capsule : PhysicsEvidenceKind.Cylinder;
            var fact = new PhysicsEvidenceFace(owner, Math.Max(0, geometry.ChildIndex), 0, kind,
                new(pose.Position, pose.Orientation), geometry.Radius, geometry.HalfLength);
            return visitor(fact, []) ? PhysicsEvidenceStatus.Complete : PhysicsEvidenceStatus.CapacityExceeded;
        }
        Span<PhysicsEvidencePoint> vertices = stackalloc PhysicsEvidencePoint[CapsuleFeatureGeometry.MaximumVertices];
        if (geometry.Mesh is { } mesh)
        {
            if (!mesh.ProvePose(pose, map)) return PhysicsEvidenceStatus.Unresolved;
            for (int triangle = 0; triangle < mesh.Count; triangle++)
            {
                StaticEvidenceDiagnostics.Triangle();
                for (int i = 0; i < 3; i++)
                    if (!EvidencePoint(map, mesh.Vertex(triangle * 3 + i), out vertices[i]))
                        return PhysicsEvidenceStatus.Unresolved;
                if (!EvidenceTouches(vertices[..3], minimum, maximum)) continue;
                if (++count > limit) return PhysicsEvidenceStatus.CapacityExceeded;
                var fact = new PhysicsEvidenceFace(owner, 0, triangle, PhysicsEvidenceKind.Triangle, default, 0, 0);
                if (!visitor(fact, vertices[..3])) return PhysicsEvidenceStatus.CapacityExceeded;
            }
            return PhysicsEvidenceStatus.Complete;
        }
        if (geometry.VertexCount > vertices.Length || geometry.FaceCount > CapsuleFeatureGeometry.MaximumFaces)
            return PhysicsEvidenceStatus.CapacityExceeded;
        int leaf = Math.Max(0, geometry.ChildIndex);
        for (int face = 0; face < geometry.FaceCount; face++)
        {
            int size = geometry.InteriorFaceSize(face);
            for (int i = 0; i < size; i++)
                if (!EvidencePoint(map, geometry.Vertex(geometry.FaceVertex(face, i)), out vertices[i]))
                    return PhysicsEvidenceStatus.Unresolved;
            // All faces of an intersecting solid are retained, including its top outside the vertical aperture.
            if (++count > limit) return PhysicsEvidenceStatus.CapacityExceeded;
            var fact = new PhysicsEvidenceFace(owner, leaf, leaf * SupportNeighborhoodPolyhedra.FaceStride + face,
                PhysicsEvidenceKind.SolidPolygon, default, 0, 0);
            if (!visitor(fact, vertices[..size])) return PhysicsEvidenceStatus.CapacityExceeded;
        }
        return PhysicsEvidenceStatus.Complete;
    }

    static bool EvidencePoint(InstalledPoseOperator map, Vector3 local, out PhysicsEvidencePoint point) =>
        StaticEvidenceMeshIndex.Point(map, local, out point);

    internal static bool EvidenceTouches(ReadOnlySpan<PhysicsEvidencePoint> vertices, Vector3 min, Vector3 max)
    {
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0;
        double x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        foreach (var p in vertices)
        { x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X); y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y); z0 = Math.Min(z0, p.Z); z1 = Math.Max(z1, p.Z); }
        return x0 <= max.X && x1 >= min.X && y0 <= max.Y && y1 >= min.Y && z0 <= max.Z && z1 >= min.Z;
    }
}
