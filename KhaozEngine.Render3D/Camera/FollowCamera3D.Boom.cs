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

        /// <summary>
        /// Rate, per second, at which the boom eases back out after an obstruction clears. A pull-in is always
        /// instant, because an eased pull-in would put the eye inside the occluder. Once the probe reports more
        /// room, the boom recovers the metres it is held short by <c>exp(-BoomRecoveryRate * dt)</c> per
        /// <see cref="AdvanceBoom"/> call, frame-rate independent. Zero (the default) follows the probe both ways at
        /// once.
        /// </summary>
        public float BoomRecoveryRate = 0f;

        long _boomProbeCalls;
        PhysicsBoomProbe? _physicsProbe;
        float _heldShortfall;   // metres the boom is held short of its full length, decayed by AdvanceBoom

        /// <summary>
        /// Calls this camera has made through <see cref="BoomProbe"/> since it was constructed, cumulative and never
        /// reset, like <see cref="OcclusionSweepCount"/>. Zero while <see cref="BoomProbe"/> is null. A steady one
        /// per rendered frame is the healthy reading.
        /// </summary>
        public long BoomProbeCount => _boomProbeCalls;

        /// <summary>
        /// Advances the eased boom recovery by <paramref name="dt"/> seconds. Call it once per render frame, for
        /// example via <see cref="FollowCameraController.Update"/>. It decays the held shortfall by
        /// <c>exp(-BoomRecoveryRate * dt)</c>, and drops it outright while <see cref="BoomRecoveryRate"/> is not a
        /// finite positive rate, so turning recovery off never leaves the boom held in.
        /// </summary>
        public void AdvanceBoom(float dt)
        {
            if (!(BoomRecoveryRate > 0f) || !float.IsFinite(BoomRecoveryRate))
            {
                _heldShortfall = 0f;
                return;
            }
            if (!(dt > 0f)) return;   // nothing to advance, and a NaN step must not poison the held shortfall
            _heldShortfall *= MathF.Exp(-BoomRecoveryRate * dt);
            if (_heldShortfall < 1e-4f) _heldShortfall = 0f;
        }

        /// <summary>
        /// Shortens the boom from <paramref name="pivot"/> to <paramref name="geometricEye"/> to the shorter reach
        /// of the <see cref="Occlusion"/> sweep and the <see cref="BoomProbe"/>. A reach short of the full boom puts
        /// the eye that reach less <see cref="OcclusionSkin"/> along the boom, floored at
        /// <see cref="MinOcclusionDistance"/> so it never collapses onto the pivot and leaves
        /// <see cref="Forward"/> and <see cref="View"/> with a zero-length look direction. With
        /// <see cref="BoomRecoveryRate"/> on, a deeper pull-in raises the held shortfall at once, and the boom
        /// stays that far short, even when the probe reports full reach, until <see cref="AdvanceBoom"/> decays it.
        /// </summary>
        Vector3 ConstrainBoom(Vector3 pivot, Vector3 geometricEye)
        {
            IPhysicsWorld? world = Occlusion;
            if (world is null) _physicsProbe = null;   // do not keep a dropped world alive through the adapter
            ICameraBoomProbe? probe = BoomProbe;
            if (world is null && probe is null) return geometricEye;

            Vector3 toEye = geometricEye - pivot;
            float full = toEye.Length();
            if (!(full > 1e-6f)) return geometricEye;

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
            bool shortened = reach < full;
            float length = shortened ? MathF.Max(MinOcclusionDistance, reach - OcclusionSkin) : full;
            if (BoomRecoveryRate > 0f)
            {
                float shortfall = full - length;
                if (shortfall > _heldShortfall) _heldShortfall = shortfall;
                if (_heldShortfall > 0f)
                {
                    length = MathF.Max(MathF.Min(MinOcclusionDistance, full), full - _heldShortfall);
                    shortened = true;
                }
            }
            if (!shortened) return geometricEye;
            return pivot + dir * length;
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
