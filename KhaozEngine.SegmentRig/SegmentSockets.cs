using System;
using System.Numerics;

namespace KhaozEngine.SegmentRig;

/// <summary>The two socket rotations a held piece rides, and the hand chain that carries it. This is what the
/// <see cref="WalkPose.RightWrist"/> and <see cref="WalkPose.LeftWrist"/> channels MEAN, written once so a
/// game does not hand-build the product at every draw.</summary>
/// <remarks>
/// Size-free and shape-free, which is the line that decides what is here. A grip ORIENTATION (how far a blade
/// leans out of a fist, which way a plate's face starts) is a property of a MESH, so it is content and stays
/// with the mesh. The two ROTATIONS below are properties of the RIG: they are the axes the pose channels are
/// defined on, and they are the same axes whatever is in the hand.
/// <para>The socket itself is measured FROM A JOINT rather than from the feet, because an arm is two rigid
/// pieces and a held thing has to swing with the fist. That offset is <see cref="BodyRig.HandFromElbow"/>,
/// and <see cref="Held"/> is the whole chain built off it. A socket in body space would leave a weapon
/// hanging in the air while the fist that holds it swung past.</para>
/// <para>ONE offset serves both hands. The arm pieces are authored about their own pivots and are centred on
/// their local x, so the side is carried entirely by which shoulder pivot the chain hangs from.</para>
/// </remarks>
public static class SegmentSockets
{
    /// <summary>The WRIST: a per-frame tip of the held piece about the hand that holds it, applied AFTER the
    /// piece's own orientation and about the body's local x, so it adds to whatever lean that orientation
    /// left the piece at.</summary>
    /// <param name="radians">From <see cref="WalkPose.RightWrist"/>. Positive carries the piece's head the way
    /// the body faces, so a positive wrist rolls a blade forward and a negative one cocks it back over the
    /// fist. Zero is the piece at its resting lean.</param>
    /// <remarks>
    /// The one degree of freedom the rig has nowhere else. Every rotation from the grip up to the shoulder is
    /// about the same axis and they ADD (see <see cref="ChopSwing"/>), so where the fist IS and where the
    /// piece POINTS would otherwise be one number: an arm placed to reach a target points its tool wherever
    /// that placement left it, which on a wind-back is straight through the upper arm. This separates the
    /// two, and it does it at the hand rather than at a joint because a wrist is what a person uses for
    /// exactly this.
    /// <para>THE ORDER IS NOT ARBITRARY. This commutes with a grip that is a rotation about the same axis and
    /// it does NOT commute with one that turns that axis (a quarter roll onto a blade's edge, say): applied
    /// ahead of such a roll it would tip the piece out to the SIDE instead of forward. So <see cref="Held"/>
    /// composes the piece's own orientation first and this over it, which is what a wrist does anyway. The
    /// chain reads piece-outward: how the piece sits in the hand, the wrist that hand is bent to, the socket,
    /// the arm.</para>
    /// </remarks>
    public static Matrix4x4 Wrist(float radians) =>
        radians == 0f ? Matrix4x4.Identity : Matrix4x4.CreateRotationX(radians);

    /// <summary>The OFF hand's TURN: the held piece swung about the BODY'S OWN UP AXIS, through the fist that
    /// holds it. Applied at the very end of the chain, after the arm has placed the hand, which is what makes
    /// the axis the body's rather than the hand's.</summary>
    /// <param name="radians">From <see cref="WalkPose.LeftWrist"/>. Positive carries the piece's face from the
    /// character's LEFT round toward its BACK and negative round toward its FRONT, whatever the arm under it
    /// is doing, so a quarter turn NEGATIVE presents a plate square at whatever the body faces. Zero is the
    /// piece exactly where it was drawn before this axis existed, which is what keeps a hanging plate's whole
    /// geometry unchanged.</param>
    /// <param name="fist">Where that hand is, in the same space the piece is being drawn in: the translation
    /// of <see cref="BodyRig.HandFromElbow"/> carried up the forearm's chain. The turn is about the vertical
    /// THROUGH this point, so the piece spins where it is instead of swinging round the body's centre
    /// line.</param>
    /// <remarks>
    /// A DIFFERENT AXIS FROM <see cref="Wrist"/>, and the difference is the whole reason this exists. A blade
    /// wants the weapon hand's TIP, about the hand's own x, which points its head the way the body faces. A
    /// plate wants its FACE turned, and the face is at the hand's +x while the arm hangs.
    /// <para>THE OBVIOUS AXIS DOES NOT WORK, which is worth writing down because it is the one a reader will
    /// reach for. A roll about the forearm's own length carries the face round the hand's x-z plane, and that
    /// plane is only the world's while the arm HANGS: every joint between the hand and the body is a pitch
    /// about the body's x, so a folded arm has tipped the hand's z most of a quarter turn toward the ceiling,
    /// and a quarter turn of roll on a blocking arm faces the plate at the sky rather than at the attacker.
    /// Measured rather than argued: with the elbow at <see cref="BlockRaise.RaiseElbow"/> the best a roll
    /// about the bone could do was leave the plate 42 degrees off forward, and it could not be brought inside
    /// that without pushing the plate through the chest.</para>
    /// <para>Turning about the BODY's vertical instead makes the face independent of the arm, because a pitch
    /// about x leaves the hand's own x alone: the face ends up at the turn's own angle whatever the shoulder
    /// and the elbow are doing, and it never tips up or down. That is what lets <see cref="BlockRaise"/> fold
    /// the arm into a real block and still present the plate square, and it is the same trick
    /// <see cref="WalkPose.RightArmYaw"/> plays one joint further up.</para>
    /// </remarks>
    public static Matrix4x4 OffTurn(float radians, Vector3 fist) =>
        radians == 0f
            ? Matrix4x4.Identity
            : Matrix4x4.CreateTranslation(-fist) * Matrix4x4.CreateRotationY(radians)
              * Matrix4x4.CreateTranslation(fist);

    /// <summary>Whether a socket offset lies inside a measured box, the sanity check a consumer makes against
    /// the fist its own mesh actually has.</summary>
    /// <param name="socket">The socket offset, from <see cref="BodyRig.HandFromShoulder"/> or
    /// <see cref="BodyRig.HandFromElbow"/>, in whichever frame the bounds were measured in.</param>
    /// <param name="bounds">The box to test against, from the caller's own measurement of a piece.</param>
    /// <remarks>Inclusive on every face, so a socket exactly on a boundary is inside. A box whose min is past
    /// its max contains nothing, which is the right answer for an unmeasured piece.</remarks>
    public static bool IsInside(Vector3 socket, in (Vector3 Min, Vector3 Max) bounds) =>
        socket.X >= bounds.Min.X && socket.X <= bounds.Max.X &&
        socket.Y >= bounds.Min.Y && socket.Y <= bounds.Max.Y &&
        socket.Z >= bounds.Min.Z && socket.Z <= bounds.Max.Z;

    /// <summary>One held piece's transform, the whole five-factor chain in one call: the piece's own
    /// orientation, the weapon hand's tip, the socket down the forearm, that forearm's finished transform,
    /// and finally the off hand's turn.</summary>
    /// <param name="rig">The body's rig, for <see cref="BodyRig.HandFromElbow"/>.</param>
    /// <param name="grip">How the piece sits in the fist, the caller's own content.
    /// <see cref="Matrix4x4.Identity"/> for a piece authored already in the hand's frame.</param>
    /// <param name="forearm">That hand's forearm transform, from
    /// <see cref="HumanoidSkeleton.Compose"/> at <see cref="HumanoidSkeleton.ForearmRight"/> or
    /// <see cref="HumanoidSkeleton.ForearmLeft"/>. The result is in whatever space that transform is in.
    /// </param>
    /// <param name="tip">The weapon hand's wrist, from <see cref="WalkPose.RightWrist"/>.</param>
    /// <param name="turn">The off hand's turn, from <see cref="WalkPose.LeftWrist"/>.</param>
    /// <remarks>
    /// Outward from the piece: its own orientation (how it sits in the fist), then the weapon hand's own tip,
    /// then the socket's offset down the forearm, then that forearm's whole chain, then the body, and finally
    /// the off hand's turn. No hand ever carries both angles: they are two axes for two jobs, not one channel
    /// written twice, so a caller passes the one its hand uses and leaves the other at zero.
    /// <para>THE PIECE GOES FIRST, for the reason on <see cref="Wrist"/>. THE OFF HAND'S TURN GOES LAST,
    /// outside the arm rather than inside it, which is the whole content of <see cref="OffTurn"/>: it is
    /// about the body's up axis through the fist, so what a plate ends up facing does not depend on how far
    /// the elbow is folded.</para>
    /// <para>Both rotations are skipped at zero rather than multiplied by an identity, so an unarmed frame
    /// and a hanging piece compose to the bits they always did.</para>
    /// </remarks>
    public static Matrix4x4 Held(BodyRig rig, in Matrix4x4 grip, in Matrix4x4 forearm, float tip = 0f,
        float turn = 0f)
    {
        ArgumentNullException.ThrowIfNull(rig);
        Matrix4x4 hand = Matrix4x4.CreateTranslation(rig.HandFromElbow) * forearm;
        return grip * Wrist(tip) * hand * OffTurn(turn, hand.Translation);
    }
}
