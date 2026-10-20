using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace KhaozEngine.Physics.Bepu;

/// <summary>One writer's or query's reusable triangle pair. No pair is retained per mesh triangle.</summary>
internal sealed class InstalledMeshSweepTriangle
{
    internal InstalledSweepGeometry Scaled { get; } = new(null!, default, default(Triangle));
    internal InstalledSweepGeometry Native { get; } = new(null!, default, default(Triangle));

    internal InstalledMeshSweepTriangle() => Scaled.SweepCompanion = Native;

    internal unsafe bool TryPose(Shapes owner, TypedIndex root, Triangle raw, Vector3 scale,
        RigidPose pose, InstalledPoseOperator map)
    {
        // Borrow only this stack value. Calling the pinned Bepu readers preserves their
        // exact scale/recenter graph without borrowing a mutable installation buffer.
        var source = new Mesh { Triangles = new Buffer<Triangle>(&raw, 1), Scale = scale };
        source.GetLocalChild(0, out Triangle scaled);
        source.GetPosedLocalChild(0, out Triangle native, out RigidPose offset);
        Scaled.SetTriangle(owner, root, scaled, new(Vector3.Zero, Quaternion.Identity));
        Native.SetTriangle(owner, root, native, offset);
        RigidPose nativePose;
        InstalledPoseOperator nativeMap;
        if (pose.Orientation == Quaternion.Identity)
        {
            nativePose = new(pose.Position + offset.Position, Quaternion.Identity);
            nativeMap = map.WithTranslation(nativePose.Position);
            if (!nativeMap.IsResolved) return false;
        }
        else
        {
            Compound.GetWorldPose(offset, pose, out nativePose);
            if (!InstalledPoseOperations.ProveComposition(offset, pose, nativePose) ||
                !InstalledPoseOperator.TryCreate(nativePose, out nativeMap)) return false;
        }
        Scaled.PublishPose(pose, pose, map);
        Native.PublishPose(pose, nativePose, nativeMap);
        return true;
    }
}
