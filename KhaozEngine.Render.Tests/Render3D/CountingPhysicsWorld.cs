using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// An <see cref="IPhysicsWorld"/> that counts sweeps and reports a wall the test can move, so a row can
    /// slide an occluder in between frames without building geometry. Everything the camera never calls throws,
    /// so a future camera change that starts querying something else shows up here rather than being silently
    /// absorbed.
    /// </summary>
    internal sealed class CountingPhysicsWorld : IPhysicsWorld
    {
        /// <summary>Sweeps issued against this world, counted on the world's own side so a row can prove the
        /// saving without trusting the camera's own counter.</summary>
        public int SweepCount;

        /// <summary>Distance from the sweep start to the wall's surface, or null for a clear boom. Assigning it
        /// is this fake's "a wall slid in", the move no camera field can see.</summary>
        public float? WallDistance;

        /// <summary>Start position of the most recent sweep, in this world's own space.</summary>
        public Vector3 LastSweepStart;

        /// <summary>The probe stops its own radius short of the surface, which is what a real sphere sweep
        /// reports and what makes <c>OcclusionRadius</c> observable in the returned eye.</summary>
        public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance,
            out SweepHit hit, QueryFilter filter = default)
        {
            SweepCount++;
            LastSweepStart = pose.Position;
            if (WallDistance is { } wall)
            {
                float d = wall - capsule.Radius;
                if (d >= 0f && d <= maxDistance)
                {
                    hit = new SweepHit(d, pose.Position + direction * d, -direction, null);
                    return true;
                }
            }
            hit = default;
            return false;
        }

        public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null)
            => throw new NotSupportedException();
        public void RemoveStatic(StaticHandle handle) => throw new NotSupportedException();
        public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body,
            PhysicsMaterial? material = null) => throw new NotSupportedException();
        public void RemoveDynamic(DynamicBodyHandle handle) => throw new NotSupportedException();
        public Pose GetDynamicPose(DynamicBodyHandle handle) => throw new NotSupportedException();
        public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular)
            => throw new NotSupportedException();
        public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular)
            => throw new NotSupportedException();
        public bool IsAwake(DynamicBodyHandle handle) => throw new NotSupportedException();
        public ConstraintHandle AddConstraint(in ConstraintDescription description) => throw new NotSupportedException();
        public void RemoveConstraint(ConstraintHandle handle) => throw new NotSupportedException();
        public void SetConstraintTarget(ConstraintHandle handle, float target) => throw new NotSupportedException();
        public void Step(float dt) => throw new NotSupportedException();
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit,
            QueryFilter filter = default) => throw new NotSupportedException();
        public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv)
            => throw new NotSupportedException();
        public void Dispose() { }
    }
}
