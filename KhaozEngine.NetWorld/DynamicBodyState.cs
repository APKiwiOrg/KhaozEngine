using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Physics;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The replicated state of a server-authoritative dynamic rigid body (a crate, a barrel, a physics prop): the part
/// of its pose beyond the <see cref="ReplicatedPosition"/> that carries the world position. It rides ALONGSIDE
/// <see cref="ReplicatedPosition"/> exactly as <see cref="MovementState"/> does for a player, so the body's position
/// drives area-of-interest (the AoI grid keys off <see cref="ReplicatedPosition"/>) while this component carries the
/// orientation and velocity. Sampled server-side from <see cref="IPhysicsWorld.GetDynamicPose"/> /
/// <see cref="IPhysicsWorld.GetDynamicVelocity"/> after <see cref="IPhysicsWorld.Step"/> (see
/// <see cref="DynamicBodyReplication"/>), and INTERPOLATED on the client with the same fixed-delay buffer as a remote
/// player: registered in <see cref="MoveProtocol.CreateRegistry"/> as type id <see cref="MoveProtocol.DynamicBodyTypeId"/>
/// with an orientation slerp, so <c>ClientReplicationView.RecordInterpolationSample</c>/<c>InterpolateAt</c> smooth the
/// orientation between snapshots automatically (the position glides via <see cref="ReplicatedPosition"/>'s own lerp).
/// The client never simulates the body: it renders the interpolated authoritative pose (no client-side prediction of
/// dynamic bodies in this batch). The linear/angular velocity is carried for a consumer that wants to extrapolate or
/// drive an effect (dust on impact, spin blur); it is not itself blended (velocity does not lerp meaningfully).
/// </summary>
public struct DynamicBodyState : IComponent
{
    /// <summary>The body's world orientation (a unit quaternion). Slerped between snapshots on the client.</summary>
    public Quaternion Orientation;

    /// <summary>Linear velocity (m/s, world space). Carried for extrapolation / effects; not interpolated.</summary>
    public Vector3 LinearVelocity;

    /// <summary>Angular velocity (rad/s, world space). Carried for extrapolation / effects; not interpolated.</summary>
    public Vector3 AngularVelocity;

    /// <summary>Builds the replicated body state from a sampled physics <paramref name="pose"/> and velocity. The
    /// position half of the pose goes into a <see cref="ReplicatedPosition"/> alongside this (see
    /// <see cref="DynamicBodyReplication.Sample"/>); this component carries the orientation + velocity.</summary>
    public static DynamicBodyState From(in Pose pose, Vector3 linear, Vector3 angular) => new()
    {
        Orientation = pose.Orientation,
        LinearVelocity = linear,
        AngularVelocity = angular,
    };
}
