using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

// Support from a LOW flat prop top for a body grounded at terrain height (KhaozEngine #1253). PropSupportFloor's
// prop sweep is gated on the body standing more than OnPropSkin above the terrain, so a deck lip, curb or doorstep
// lower than that was never support: the cap passed through its walkable lip and the ground snap seated the body at
// the terrain inside the prop. This is the one exception to that gate, and it keeps the flank rule the gate exists
// for: only a near-flat surface counts (a convex flank fails it), and only within LowPropRise of the tick's start
// (a convex prop's near-flat crown sits far higher above its base). Once the body stands above OnPropSkin, the
// ordinary prop sweep follows the surface as before.
public static partial class CharacterMovement
{
    // The highest low prop top this rule seats a terrain-height body on in one tick, measured from its start height.
    // The evidence it covers: a 0.3 m capsule sank through flat lips of 0.025 to 0.1 m above the terrain.
    private const float LowPropRise = 0.1f;

    /// <summary>The support floor raised onto a low near-flat prop top under the footprint, for a grounded body the
    /// ordinary prop sweep skips. Returns <paramref name="groundY"/> unchanged when no such top qualifies.</summary>
    private static float LowPropSupport(IPhysicsWorld world, in CapsuleShape capsule, in Vector3 pos,
        in Vector3 startPos, float halfH, float terrainGroundY, float groundY)
    {
        float probeStart = pos.Y + 2f * halfH;
        float maxProbe = (probeStart - terrainGroundY) + 2f * halfH;
        if (!world.SweepCapsule(capsule, Pose.At(new Vector3(pos.X, probeStart, pos.Z)), -Vector3.UnitY, maxProbe,
                out SweepHit hit) ||
            hit.Normal.Y < LipLandingFlatNormalY || !UnderFootprint(hit.Point, pos, capsule.Radius))
            return groundY;
        float centreY = probeStart - hit.Distance;
        return centreY > groundY && centreY <= startPos.Y + LowPropRise ? centreY : groundY;
    }
}
