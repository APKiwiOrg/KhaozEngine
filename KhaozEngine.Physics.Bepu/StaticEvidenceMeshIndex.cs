using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Trees;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Evidence traverses the existing writer-proved mesh tree. Only the first unresolved vertex is
/// retained, so an off-aperture refusal survives without a second per-triangle index.</summary>
internal sealed class StaticEvidenceMeshIndex(InstalledMeshSweepGeometry mesh)
{
    InstalledPoseOperator _map;
    RigidPose _pose;
    int _firstUnresolved;
    PhysicsEvidenceStatus _status;

    internal void Refresh(RigidPose pose, InstalledPoseOperator map)
    {
        _pose = pose; _map = map; _firstUnresolved = mesh.Count;
        _status = PhysicsEvidenceStatus.Complete;
        for (int triangle = 0; triangle < mesh.Count; triangle++)
            for (int i = 0; i < 3; i++)
                if (!Point(map, mesh.Vertex(triangle * 3 + i), out _))
                { _firstUnresolved = triangle; _status = PhysicsEvidenceStatus.Unresolved; return; }
    }

    internal bool TryCountUpper(Vector3 min, Vector3 max, int limit, out int count) =>
        Traverse(min, max, limit, null, out count);

    bool Traverse(Vector3 min, Vector3 max, int limit, List<int>? triangles, out int count)
    {
        count = 0;
        if (_firstUnresolved == 0) return true;
        Span<int> pending = stackalloc int[CapsuleGeneralSweepPolicy.MaximumPending];
        int next = 0, size = 0;
        while (true)
        {
            if (next < 0)
            {
                int triangle = Tree.Encode(next);
                if (triangle < _firstUnresolved)
                {
                    if (count == limit) return false;
                    count++; triangles?.Add(triangle);
                }
            }
            else
            {
                Node node = mesh.EvidenceNode(next);
                bool a = Touches(node.A, min, max), b = mesh.Count > 1 && Touches(node.B, min, max);
                if (a)
                {
                    if (b)
                    {
                        if (size == pending.Length) return false;
                        pending[size++] = node.B.Index;
                    }
                    next = node.A.Index;
                    continue;
                }
                if (b) { next = node.B.Index; continue; }
            }
            if (size == 0) return true;
            next = pending[--size];
        }
    }

    bool Touches(NodeChild child, Vector3 min, Vector3 max)
    {
        Vector3 lo = new(float.PositiveInfinity), hi = new(float.NegativeInfinity);
        if (_pose.Orientation == Quaternion.Identity)
        {
            Vector3 a = child.Min * mesh.EvidenceScale, b = child.Max * mesh.EvidenceScale;
            GeometryInterval x = GeometryInterval.Enclose(a.X, b.X).Add(GeometryInterval.Exact(_pose.Position.X));
            GeometryInterval y = GeometryInterval.Enclose(a.Y, b.Y).Add(GeometryInterval.Exact(_pose.Position.Y));
            GeometryInterval z = GeometryInterval.Enclose(a.Z, b.Z).Add(GeometryInterval.Exact(_pose.Position.Z));
            if (!x.IsResolved || !y.IsResolved || !z.IsResolved) return true;
            return x.Lower <= max.X && x.Upper >= min.X && y.Lower <= max.Y && y.Upper >= min.Y &&
                z.Lower <= max.Z && z.Upper >= min.Z;
        }
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 local = new((corner & 1) == 0 ? child.Min.X : child.Max.X,
                (corner & 2) == 0 ? child.Min.Y : child.Max.Y, (corner & 4) == 0 ? child.Min.Z : child.Max.Z);
            if (!InstalledSweepBounds.TryPoint(_map, _pose, local * mesh.EvidenceScale, out var a, out var b)) return true;
            lo = Vector3.Min(lo, a); hi = Vector3.Max(hi, b);
        }
        return lo.X <= max.X && hi.X >= min.X && lo.Y <= max.Y && hi.Y >= min.Y && lo.Z <= max.Z && hi.Z >= min.Z;
    }

    internal PhysicsEvidenceStatus Emit(StaticHandle owner, Vector3 min, Vector3 max,
        int limit, ref int count, PhysicsEvidenceVisitor visitor, List<int> triangles)
    {
        triangles.Clear();
        // The preceding upper-bound pass completed under this same lease with at least this much capacity.
        if (!Traverse(min, max, limit - count, triangles, out _))
            throw new InvalidOperationException("The evidence traversal changed within its authenticated interval.");
        triangles.Sort();
        Span<PhysicsEvidencePoint> points = stackalloc PhysicsEvidencePoint[3];
        foreach (int triangle in triangles)
        {
            StaticEvidenceDiagnostics.Triangle();
            for (int i = 0; i < 3; i++)
                if (!Point(_map, mesh.Vertex(triangle * 3 + i), out points[i]))
                    return PhysicsEvidenceStatus.Unresolved;
            if (!BepuPhysicsWorld.EvidenceTouches(points, min, max)) continue;
            if (++count > limit) return PhysicsEvidenceStatus.CapacityExceeded;
            var face = new PhysicsEvidenceFace(owner, 0, triangle, PhysicsEvidenceKind.Triangle, default, 0, 0);
            if (!visitor(face, points)) return PhysicsEvidenceStatus.CapacityExceeded;
        }
        return _status;
    }

    internal PhysicsEvidenceStatus Status => _status;

    internal static bool Point(InstalledPoseOperator map, Vector3 local, out PhysicsEvidencePoint point)
    {
        FeaturePoint exact = map.Point(local, exactZeroProducts: true);
        point = default;
        if (!exact.X.IsExact || !exact.Y.IsExact || !exact.Z.IsExact) return false;
        point = new(exact.X.Value, exact.Y.Value, exact.Z.Value);
        return true;
    }
}
