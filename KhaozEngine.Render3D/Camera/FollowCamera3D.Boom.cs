using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Render3D
{
    public sealed partial class FollowCamera3D
    {
        /// <summary>
        /// Optional boom probe. When set, <see cref="Eye"/> asks it how far the boom can extend from
        /// <see cref="Pivot"/> toward the geometric eye, with <see cref="OcclusionRadius"/>, and pulls the eye in
        /// along the boom to that reach less <see cref="OcclusionSkin"/>, floored at
        /// <see cref="MinOcclusionDistance"/>. It shares one path with <see cref="Occlusion"/>, and when both are
        /// set the shorter reach wins. Applied before <see cref="GroundHeight"/> clearance. Null (the default)
        /// leaves the boom to <see cref="Occlusion"/> alone.
        /// </summary>
        public ICameraBoomProbe? BoomProbe;

        long _boomProbeCalls;
        PhysicsBoomProbe? _physicsProbe;

        /// <summary>
        /// Calls this camera has made through <see cref="BoomProbe"/> since it was constructed, cumulative and never
        /// reset, like <see cref="OcclusionSweepCount"/>. Zero while <see cref="BoomProbe"/> is null. A steady one
        /// per rendered frame is the healthy reading.
        /// </summary>
        public long BoomProbeCount => _boomProbeCalls;

        /// <summary>
        /// Shortens the boom from <paramref name="pivot"/> to <paramref name="geometricEye"/> to the shorter reach
        /// of the <see cref="Occlusion"/> sweep and the <see cref="BoomProbe"/>. A reach short of the full boom puts
        /// the eye that reach less <see cref="OcclusionSkin"/> along the boom, floored at
        /// <see cref="MinOcclusionDistance"/> so it never collapses onto the pivot and leaves
        /// <see cref="Forward"/> and <see cref="View"/> with a zero-length look direction.
        /// </summary>
        Vector3 ConstrainBoom(Vector3 pivot, Vector3 geometricEye)
        {
            IPhysicsWorld? world = Occlusion;
            ICameraBoomProbe? probe = BoomProbe;
            if (world is null && probe is null) return geometricEye;

            Vector3 toEye = geometricEye - pivot;
            float full = toEye.Length();
            if (full <= 1e-6f) return geometricEye;

            Vector3 dir = toEye / full;
            float reach = full;
            if (world is not null)
            {
                _occlusionSweeps++;
                reach = MathF.Min(reach, PhysicsProbeFor(world).Reach(pivot, dir, full, OcclusionRadius));
            }
            if (probe is not null)
            {
                _boomProbeCalls++;
                reach = MathF.Min(reach, probe.Reach(pivot, dir, full, OcclusionRadius));
            }
            if (!(reach < full)) return geometricEye;
            return pivot + dir * MathF.Max(MinOcclusionDistance, reach - OcclusionSkin);
        }

        /// <summary>The physics adapter for <paramref name="world"/>, rebuilt only when <see cref="Occlusion"/>
        /// points at a different world, so a frame allocates nothing.</summary>
        PhysicsBoomProbe PhysicsProbeFor(IPhysicsWorld world)
        {
            if (_physicsProbe is null || !ReferenceEquals(_physicsProbe.World, world))
                _physicsProbe = new PhysicsBoomProbe(world);
            return _physicsProbe;
        }
    }
}
