using System.Collections.Generic;
using System.Numerics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuUtilities;
using BepuUtilities.Memory;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Applies the backend's triangle-boundary smoothing without its final contact-count reduction.</summary>
internal static class CapsuleMeshContacts
{
    // Bepu 2.4.0 skips smoothing above this count. Refuse that case instead of certifying raw edge normals.
    const int MaximumTriangles = 1024;

    public static unsafe bool Prepare(Shapes shapes, CapsuleContactCollector.Pair pair,
        Capsule capsule, Pose pose, float margin)
    {
        shapes[pair.Shape.Type].GetShapeData(pair.Shape.Index, out void* data, out _);
        var mesh = (Mesh*)data;
        // ShapeFactory currently produces unit-scale meshes. Refuse any future unsupported representation.
        if (mesh->Scale != Vector3.One) return false;
        Quaternion inverse = Quaternion.Conjugate(pair.Pose.Orientation);
        Vector3 localCentre = Vector3.Transform(pose.Position - pair.Pose.Position, inverse);
        Quaternion localOrientation = Quaternion.Concatenate(pose.Orientation, inverse);
        capsule.ComputeBounds(localOrientation, out Vector3 min, out Vector3 max);
        var bounds = new BoundingBox
        {
            Min = localCentre + min - new Vector3(margin),
            Max = localCentre + max + new Vector3(margin)
        };
        var indices = pair.MeshTriangles;
        var overlaps = new TriangleIndices(indices);
        mesh->Tree.GetOverlaps(bounds.Min, bounds.Max, ref overlaps);
        if (overlaps.Overflow) return false;
        indices.Sort();
        pair.MeshBounds = bounds;
        return true;
    }

    public static unsafe bool Smooth(Shapes shapes, BufferPool pool, CapsuleContactCollector.Pair pair)
    {
        shapes[pair.Shape.Type].GetShapeData(pair.Shape.Index, out void* data, out _);
        var mesh = (Mesh*)data;
        var indices = pair.MeshTriangles;
        if (indices.Count == 0) return pair.Children.Count == 0;
        pool.Take<Triangle>(indices.Count, out var triangles);
        pool.Take<NonconvexReductionChild>(indices.Count, out var children);
        try
        {
            for (int i = 0; i < indices.Count; i++)
            {
                mesh->GetLocalChild(indices[i], out triangles[i]);
                children[i] = new NonconvexReductionChild { ChildIndexA = indices[i], ChildIndexB = 0 };
            }
            foreach (CapsuleContactCollector.Child child in pair.Children)
            {
                int index = indices.BinarySearch(child.Index);
                if (index < 0) return false;
                children[index].Manifold = child.Manifold;
            }
            Matrix3x3.CreateFromQuaternion(pair.Pose.Orientation, out Matrix3x3 orientation);
            Matrix3x3.Transpose(orientation, out Matrix3x3 inverseOrientation);
            // Source mesh is slot A, so child offsets and normals already have the mesh-relative sense.
            MeshReduction.ReduceManifolds(ref triangles, ref children, 0, indices.Count, false,
                pair.MeshBounds, orientation, inverseOrientation, mesh, pool);
            foreach (CapsuleContactCollector.Child child in pair.Children)
            {
                ConvexContactManifold smoothed = children[indices.BinarySearch(child.Index)].Manifold;
                // For a wedged mesh contact Bepu may rotate the normal while retaining the old depth.
                // That depth is not a certified separation along the new constraint. Refuse the whole
                // query until a geometric reconstruction can supply a bounded distance, never publish it.
                if (smoothed.Count > 0 && smoothed.Normal != child.Manifold.Normal) return false;
            }
            pair.Children.Clear();
            for (int i = 0; i < indices.Count; i++)
                if (children[i].Manifold.Count > 0)
                    pair.Children.Add(new CapsuleContactCollector.Child(indices[i], children[i].Manifold));
            return true;
        }
        finally
        {
            pool.Return(ref children);
            pool.Return(ref triangles);
        }
    }

    struct TriangleIndices(List<int> indices) : IBreakableForEach<int>
    {
        public bool Overflow;

        public bool LoopBody(int index)
        {
            if (indices.Count == MaximumTriangles)
            {
                Overflow = true;
                return false;
            }
            indices.Add(index);
            return true;
        }
    }
}
