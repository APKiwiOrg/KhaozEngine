using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Reads installed sweep geometry while the caller holds its owner's query gate.
/// Registry, index and pose must come from that same live owner and selected candidate.</summary>
internal static class CapsuleSweepGeometry
{
    internal static unsafe bool TryReadIdentityBox(Shapes shapes, TypedIndex shape, RigidPose pose, out Box box)
    {
        box = default;
        if (shapes is null || !shape.Exists || shape.Type != default(Box).TypeId ||
            pose.Orientation != Quaternion.Identity || !Finite(pose.Position)) return false;
        shapes[shape.Type].GetShapeData(shape.Index, out void* data, out int size);
        if (data == null || size < sizeof(Box)) return false;
        Box value = *(Box*)data;
        if (!Positive(value.HalfWidth) || !Positive(value.HalfHeight) || !Positive(value.HalfLength)) return false;
        box = value;
        return true;
    }

    internal static unsafe bool TryCapture(Shapes shapes, TypedIndex root, out InstalledSweepGeometry? geometry)
    {
        geometry = null;
        if (root.Exists && root.Type == default(Mesh).TypeId)
            return InstalledMeshSweepGeometry.TryCapture(shapes, root, out geometry);
        if (CapsuleCurvedGeometry.TryCapture(shapes, root, out geometry)) return true;
        if (root.Exists && root.Type == default(Compound).TypeId)
            return TryCaptureHull(shapes, root, out geometry) || CapsuleCompoundSweep.TryCapture(shapes, root, out geometry);
        if (root.Exists && root.Type == default(ConvexHull).TypeId) return TryCaptureHull(shapes, root, out geometry);
        if (!TryReadIdentityBox(shapes, root, new RigidPose(Vector3.Zero, Quaternion.Identity), out Box box)) return false;
        Vector3 half = new(box.HalfWidth, box.HalfHeight, box.HalfLength);
        if (!InstalledPoseOperator.Within(half, RepresentedGeometryTransforms.MaximumLocalMagnitude)) return false;
        geometry = new(shapes, root, half);
        return true;
    }

    internal static bool HullBuffersValid(in ConvexHull hull) => hull.Points.Allocated &&
        hull.FaceToVertexIndicesStart.Allocated && hull.FaceVertexIndices.Allocated &&
        hull.Points.Length > 0 && hull.Points.Length <= (CapsuleGeneralSweepPolicy.MaximumVertices + Vector<float>.Count - 1) / Vector<float>.Count &&
        hull.FaceToVertexIndicesStart.Length >= 4 && hull.FaceToVertexIndicesStart.Length <= CapsuleGeneralSweepPolicy.MaximumFaces &&
        hull.FaceVertexIndices.Length >= 12 && hull.FaceVertexIndices.Length <= CapsuleGeneralSweepPolicy.MaximumFaceIndices;

    internal static bool TryCaptureHull(Shapes shapes, TypedIndex root, out InstalledSweepGeometry? geometry)
    {
        geometry = null;
        if (shapes is null || !root.Exists) return false;
        TypedIndex shape = root;
        RigidPose local = new(Vector3.Zero, Quaternion.Identity);
        if (root.Type == default(Compound).TypeId)
        {
            ref Compound wrapper = ref shapes.GetShape<Compound>(root.Index);
            if (!wrapper.Children.Allocated || wrapper.Children.Length != 1) return false;
            shape = wrapper.Children[0].ShapeIndex;
            local = wrapper.Children[0].LocalPose;
            if (local.Orientation != Quaternion.Identity ||
                !InstalledPoseOperator.Within(local.Position, RepresentedGeometryTransforms.MaximumLocalMagnitude)) return false;
        }
        if (!shape.Exists || shape.Type != default(ConvexHull).TypeId) return false;
        ref ConvexHull hull = ref shapes.GetShape<ConvexHull>(shape.Index);
        if (!HullBuffersValid(hull)) return false;
        var vertices = new List<Vector3>();
        var ids = new Dictionary<int, int>();
        var faces = new int[hull.FaceToVertexIndicesStart.Length][];
        var indices = new HullVertexIndex[hull.FaceVertexIndices.Length];
        int expectedStart = 0;
        for (int face = 0; face < faces.Length; face++)
        {
            int start = hull.FaceToVertexIndicesStart[face];
            int end = face + 1 == faces.Length ? indices.Length : hull.FaceToVertexIndicesStart[face + 1];
            if (start != expectedStart || end < start + 3 || end > indices.Length || end - start > CapsuleGeneralSweepPolicy.MaximumVertices) return false;
            expectedStart = end;
            faces[face] = new int[end - start];
            for (int i = start; i < end; i++)
            {
                HullVertexIndex source = hull.FaceVertexIndices[i];
                if (source.BundleIndex >= hull.Points.Length || source.InnerIndex >= Vector<float>.Count) return false;
                indices[i] = source;
                int key = source.BundleIndex * Vector<float>.Count + source.InnerIndex;
                if (!ids.TryGetValue(key, out int vertex))
                {
                    if (vertices.Count >= CapsuleGeneralSweepPolicy.MaximumVertices) return false;
                    hull.GetPoint(source, out Vector3 point);
                    if (!InstalledPoseOperator.Within(point, RepresentedGeometryTransforms.MaximumLocalMagnitude)) return false;
                    vertex = vertices.Count;
                    ids.Add(key, vertex);
                    vertices.Add(point);
                }
                faces[face][i - start] = vertex;
            }
        }
        var points = new Vector3[hull.Points.Length * Vector<float>.Count];
        // Bepu support queries include the padded lanes. Every lane must alias an admitted
        // vertex, otherwise the face loops do not describe the installed support solid.
        for (int i = 0; i < points.Length; i++)
        {
            hull.GetPoint(i, out points[i]);
            if (!vertices.Contains(points[i])) return false;
        }
        if (!InstalledPoseOperator.TryCreate(new RigidPose(Vector3.Zero, Quaternion.Identity), out var map)) return false;
        Vector3[] locals = vertices.ToArray();
        var exact = new FeaturePoint[locals.Length];
        for (int i = 0; i < locals.Length; i++) exact[i] = FeaturePoint.Exact(locals[i]);
        var topology = new CapsuleFeaturePolyhedron(shape, local, new RigidPose(Vector3.Zero, Quaternion.Identity),
            0, locals, exact, faces, map);
        // The existing exact validator proves finite planar convex faces, opposite edge
        // pairing, Euler closure and connected cyclic vertex links. No plane epsilon is used.
        if (topology.Validate(topologyOnly: true) != CapsuleFeatureStatus.Complete) return false;
        geometry = new(shapes, root, shape, local, topology, points, indices);
        return true;
    }

    internal static bool TryReadLeaf(Shapes shapes, TypedIndex root, RigidPose parent,
        InstalledSweepGeometry geometry, int leafIndex, out InstalledSweepLeaf leaf)
    {
        leaf = default;
        if (!geometry.Matches(shapes, root)) return false;
        if (geometry.Children is { } children)
        {
            if ((uint)leafIndex >= (uint)children.Length || !geometry.TryPose(parent, out _)) return false;
            geometry = children[leafIndex];
        }
        else if (leafIndex != 0) return false;
        if (!geometry.TryPose(parent, out InstalledPoseOperator map)) return false;
        leaf = new(geometry, geometry.LeafPose, map);
        return true;
    }

    static bool Positive(float value) => float.IsFinite(value) && value > 0f;
    static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
