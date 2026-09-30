using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The <see cref="FollowCamera3D.Occlusion"/> sweep as an <see cref="ICameraBoomProbe"/>, so the physics
    /// world and a custom probe share one boom path. A zero-length capsule (a sphere) is swept against statics
    /// only, mirroring <c>CharacterMovement</c>'s own swept collide-and-slide.
    /// </summary>
    internal sealed class PhysicsBoomProbe(IPhysicsWorld world) : ICameraBoomProbe
    {
        /// <summary>The world this adapter sweeps, so the camera can reuse it while the reference is unchanged.</summary>
        public IPhysicsWorld World { get; } = world;

        public float Reach(Vector3 origin, Vector3 direction, float length, float radius)
        {
            // The sweep START is a query coordinate, so it is expressed in the physics world's own space
            // (IPhysicsWorld.Origin): the camera speaks absolute, and against a rebased world an unreduced start
            // silently stops finding anything. The direction and the returned distance are frame-invariant, so
            // only this one operand converts.
            return World.SweepCapsule(new CapsuleShape(radius, 0f), Pose.At(origin - World.Origin), direction, length,
                out SweepHit hit, QueryFilter.StaticsOnly)
                ? hit.Distance
                : length;
        }
    }
}
