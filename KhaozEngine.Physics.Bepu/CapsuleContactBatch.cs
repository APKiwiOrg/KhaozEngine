using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Selects source leaves with the requested margin before invoking convex narrow phase.</summary>
internal static class CapsuleContactBatch
{
    public static unsafe bool Submit(Shapes shapes, CapsuleContactCollector.Pair pair, Capsule capsule,
        Pose pose, float margin, CapsuleContactCollector collector,
        ref CollisionBatcher<CapsuleContactCollector.Callbacks> batcher)
    {
        shapes[pair.Shape.Type].GetShapeData(pair.Shape.Index, out void* data, out _);
        if (pair.Shape.Type == default(Mesh).TypeId)
        {
            var mesh = (Mesh*)data;
            foreach (int index in pair.MeshTriangles)
            {
                int leaf = collector.AddLeaf(pair, index);
                if (leaf < 0) return false;
                mesh->GetLocalChild(index, out Triangle triangle);
                Vector3 offset = pose.Position - pair.Pose.Position;
                // Add copies both ephemeral shapes into batch-owned storage until Flush.
                batcher.Add(triangle, capsule, offset, pair.Pose.Orientation, pose.Orientation, margin, leaf);
            }
            return true;
        }
        if (pair.Shape.Type == default(Compound).TypeId)
        {
            var compound = (Compound*)data;
            if (compound->Children.Length > 4096) return false;
            capsule.ComputeBounds(pose.Orientation, out Vector3 queryMin, out Vector3 queryMax);
            queryMin += pose.Position - new Vector3(margin);
            queryMax += pose.Position + new Vector3(margin);
            for (int i = 0; i < compound->Children.Length; i++)
            {
                ref var child = ref compound->Children[i];
                // ShapeFactory flattens compounds to convex leaves. Refuse a future representation
                // that would re-enter Bepu's unexpanded child selection at zero timestep.
                if (shapes[child.ShapeIndex.Type].Compound) return false;
                Compound.GetRotatedChildPose(child.LocalPose, pair.Pose.Orientation, out RigidPose childPose);
                childPose.Position += pair.Pose.Position;
                shapes[child.ShapeIndex.Type].ComputeBounds(child.ShapeIndex.Index, childPose,
                    out Vector3 min, out Vector3 max);
                if (min.X > queryMax.X || min.Y > queryMax.Y || min.Z > queryMax.Z ||
                    max.X < queryMin.X || max.Y < queryMin.Y || max.Z < queryMin.Z) continue;
                if (!SubmitConvex(shapes, child.ShapeIndex, childPose, pair, i, capsule, pose,
                    margin, collector, ref batcher)) return false;
            }
            return true;
        }
        return SubmitConvex(shapes, pair.Shape, pair.Pose, pair, 0, capsule, pose, margin, collector, ref batcher);
    }

    static unsafe bool SubmitConvex(Shapes shapes, TypedIndex shape, RigidPose sourcePose,
        CapsuleContactCollector.Pair owner, int childIndex, Capsule capsule, Pose pose, float margin,
        CapsuleContactCollector collector, ref CollisionBatcher<CapsuleContactCollector.Callbacks> batcher)
    {
        int leaf = collector.AddLeaf(owner, childIndex);
        if (leaf < 0) return false;
        shapes[shape.Type].GetShapeData(shape.Index, out void* data, out _);
        batcher.CacheShapeB(shape.Type, capsule.TypeId, Unsafe.AsPointer(ref capsule),
            Unsafe.SizeOf<Capsule>(), out void* cachedCapsule);
        var continuation = new PairContinuation(leaf);
        Vector3 offset = pose.Position - sourcePose.Position;
        batcher.AddDirectly(shape.Type, capsule.TypeId, data, cachedCapsule, offset,
            sourcePose.Orientation, pose.Orientation, margin, continuation);
        return true;
    }
}
