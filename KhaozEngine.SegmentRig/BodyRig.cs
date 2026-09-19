using System.Numerics;

namespace KhaozEngine.SegmentRig;

/// <summary>One two-legged BODY's proportions: where its limbs hinge, how tall it stands, and how far it
/// travels per stride. The runtime twin of whatever authored the mesh pieces.</summary>
/// <remarks>
/// A second body of a different SIZE needs its own pivots, its own hand socket and its own stride, because
/// every one of those is a length in metres and none of them survives being shared: a 0.8 m body hung off a
/// 1.5 m body's shoulders wears its arms above its own head. <see cref="Scaled"/> is how a game derives one
/// from another without re-authoring a number, and <see cref="Human"/> is the reference proportion every
/// other size is usually taken off.
/// <para>These are the SAME NUMBERS a piece generator authors the limb meshes about, in the engine frame:
/// the human's shoulder sits at (0.215, 1.12, 0) and its hip at (0.075, 0.62, 0), each mirrored in x for the
/// other side. The elbow and the knee are measured from the PARENT joint rather than from the feet, because
/// that is the frame the child piece is authored in. Edit one and edit the other: a pivot that drifts off the
/// mesh tears the arm out of the shoulder the moment it swings, and nothing fails.</para>
/// <para>A limb piece is authored ABOUT ITS OWN PIVOT: its local origin is the joint, and it hangs down -y
/// from there, so a rotation about local x swings it. That is why the pivots live here rather than being
/// baked into the mesh, and why the hand socket (<see cref="HandFromElbow"/>) is measured from the elbow
/// rather than from the feet.</para>
/// <para>A limb is TWO pieces, hinged: an upper arm carrying a forearm, a thigh carrying a shin. So the four
/// joints below are the roots of four two-bone chains, and a child's transform is its own flex, then out to
/// its joint, then its PARENT's whole transform. The two hinges each bend one way only, which is the whole
/// content of <see cref="Elbow"/> and <see cref="Knee"/>: an elbow carries the hand forward, a knee carries
/// the heel back, and that sign lives in those two methods rather than in the cycle that feeds them.</para>
/// <para>WHICH SIDE IS WHICH, derived rather than guessed, because it is the half that inverts silently. A
/// <see cref="BodyPose.Yaw"/> of 0 faces ENGINE +Z, and the frame is right handed with +x east, +y up and +z
/// south. A person facing south has east on their LEFT and west on their right, so the character's RIGHT side
/// is at engine -x and their LEFT at engine +x. The two arm pieces are usually identical geometry, so getting
/// this backwards costs nothing visible until somebody notices the weapon is in the shield hand.</para>
/// </remarks>
public sealed record BodyRig
{
    /// <summary>The PERSON, and the reference proportion every other body is usually derived from. A game
    /// declares its own sizes and shapes with <see cref="Scaled"/> or with an object initializer of its own,
    /// because a creature's proportions are content rather than engine.</summary>
    public static BodyRig Human { get; } = new()
    {
        RightShoulder = new Vector3(-0.215f, 1.12f, 0f),
        RightHip = new Vector3(-0.075f, 0.62f, 0f),
        ElbowFromShoulder = new Vector3(0f, -0.28f, 0f),
        KneeFromHip = new Vector3(0f, -0.30f, 0f),
        HandFromShoulder = new Vector3(0f, -0.485f, 0f),
        HeadFromFeet = new Vector3(0f, 1.20f, 0f),
        UpperArmRadiusMetres = 0.075f,
        RestHeightMetres = 1.5f,
        StrideMetres = 1.4f,
    };

    /// <summary>The weapon shoulder, metres from the body's feet. Negative x: the body faces engine +z at
    /// yaw 0, and a person facing south wears their right side to the west.</summary>
    public required Vector3 RightShoulder { get; init; }

    /// <summary>The right hip, on the same side as <see cref="RightShoulder"/>.</summary>
    public required Vector3 RightHip { get; init; }

    /// <summary>The elbow, metres from the SHOULDER pivot of either arm. The forearm piece is authored about
    /// this point, so it is both the hinge and that piece's own origin. Putting it exactly where the arm's
    /// two sections already meet is what keeps the hand socket at the body-space point it sits at when the
    /// arm hangs (<see cref="HandFromShoulder"/> is that sum).</summary>
    public required Vector3 ElbowFromShoulder { get; init; }

    /// <summary>The knee, metres from the HIP pivot of either leg, and the shin piece's own origin. A little
    /// under half way down from the hip to the sole, 0.32 m off the floor on a person, which is where a knee
    /// is on a body of these proportions: too high and the shin swings like a stilt, too low and the thigh
    /// does.</summary>
    public required Vector3 KneeFromHip { get; init; }

    /// <summary>The grip, metres from the SHOULDER PIVOT of whichever arm holds a piece. The body-space fact,
    /// and the one reach authored here: where a hanging hand is on a body standing still. A draw uses
    /// <see cref="HandFromElbow"/>, which is this less the elbow.</summary>
    public required Vector3 HandFromShoulder { get; init; }

    /// <summary>The NECK BASE, metres from the body's feet: where the head region starts, and the anchor a
    /// head or hair piece rides. Such a piece is authored about this point, so it draws at this translation
    /// inside the torso frame the way a held piece draws at the hand socket inside a forearm's.</summary>
    /// <remarks>A body whose head is drawn at a different scale from the rest of it (which is how a lot of
    /// stylized creatures are built) does not have its skull start here, because a uniformly scaled neck base
    /// is not where an enlarged skull reaches down to. That only matters for a body that actually anchors a
    /// piece on this point.</remarks>
    public required Vector3 HeadFromFeet { get; init; }

    /// <summary>How thick the upper arm is at the shoulder, as a radius in metres, and a CLEARANCE FLOOR
    /// rather than a measurement: what a held piece has to stand off the arm's own axis before it is inside
    /// the arm rather than beside it.</summary>
    /// <remarks>Deliberately a little over the widest half width the limb actually has, because a clearance
    /// is worth more conservative than exact.
    /// <para>Here rather than in the stroke that reads it, because it is a LENGTH and so belongs to the
    /// body: a body at 55 percent has an arm 55 percent as thick, and a clearance measured against a
    /// person's would pass a weapon straight through it.</para></remarks>
    public required float UpperArmRadiusMetres { get; init; }

    /// <summary>How tall the assembled body stands at rest, metres from the sole to the crown. What the
    /// pieces build to, so a mesh that measures anything else has drifted off its own rig.</summary>
    public required float RestHeightMetres { get; init; }

    /// <summary>How far this body walks per full cycle of the gait, metres. It scales with the body because
    /// a stride is a LENGTH: a short body covering a person's 1.4 m stride would slide across the ground on
    /// one pace, and the same body turning its legs over faster for the same ground speed is most of what
    /// reads as small.</summary>
    public required float StrideMetres { get; init; }

    /// <summary>The off shoulder, the mirror of <see cref="RightShoulder"/> in x alone.</summary>
    public Vector3 LeftShoulder => Mirror(RightShoulder);

    /// <summary>The left hip, the mirror of <see cref="RightHip"/> in x alone.</summary>
    public Vector3 LeftHip => Mirror(RightHip);

    /// <summary>The grip, metres from the ELBOW, which is the frame the forearm piece and its fist are
    /// authored in and so the offset a draw actually applies. Derived rather than written down twice: a
    /// hand-copied second number is a second place for the socket to drift off the mesh.</summary>
    public Vector3 HandFromElbow => HandFromShoulder - ElbowFromShoulder;

    /// <summary>This rig at another size: every length multiplied, nothing re-authored. The pivots, the
    /// reach, the height and the stride all scale together, which is the only way they can stay on a mesh
    /// that was scaled by the same factor.</summary>
    /// <param name="scale">The factor, 1 being this rig unchanged.</param>
    public BodyRig Scaled(float scale) => new()
    {
        RightShoulder = RightShoulder * scale,
        RightHip = RightHip * scale,
        ElbowFromShoulder = ElbowFromShoulder * scale,
        KneeFromHip = KneeFromHip * scale,
        HandFromShoulder = HandFromShoulder * scale,
        HeadFromFeet = HeadFromFeet * scale,
        UpperArmRadiusMetres = UpperArmRadiusMetres * scale,
        RestHeightMetres = RestHeightMetres * scale,
        StrideMetres = StrideMetres * scale,
    };

    /// <summary>The WHOLE body's transform for one frame, which the two legs compose inside and which the
    /// upper body reaches through <see cref="Torso(in BodyPose, in WalkPose)"/>: the lean about this body's own hip height, then the
    /// root tilt about the point it stands on, then the facing, then the position with the bob folded into
    /// it.</summary>
    /// <param name="pose">Where and which way the body draws.</param>
    /// <param name="walk">This frame's cycle, for the body's own bob, lean and root tilt.
    /// <see cref="WalkPose.Rest"/> for a body standing still, which reduces this to the yaw and the
    /// position.</param>
    /// <remarks>
    /// Rotation THEN translation, the engine's own model-transform hand and the one <see cref="BodyPose.Yaw"/>
    /// is documented against. The lean goes about the body's own local x AHEAD of the yaw, so it tips the way
    /// the body faces rather than toward a world axis, and the bob runs straight down the world's y after it.
    /// The lean's pivot is HIP height off THIS rig, because that is the joint a body bends at to lean into a
    /// run and because a small body leaning about a person's hip height tips about a point above its own head.
    /// <para>THE ROOT TILT IS A DIFFERENT PIVOT, and that is the whole reason it is a separate pair of
    /// channels. <see cref="WalkPose.RootRoll"/> then <see cref="WalkPose.RootPitch"/> turn the entire rig,
    /// feet included, about the point the pose names, which is what a body with nothing under its soles does:
    /// a swimmer lies out flat, a fall pitches into it. Roll first so it is a roll about the body's own
    /// forward axis rather than about whatever axis a pitch has already swung that into. Both are skipped
    /// entirely at zero rather than multiplied by an identity, so every grounded frame composes to the bits
    /// it always did.</para>
    /// <para>ONE body matrix, here, rather than one in the draw and another wherever an overhead marker is
    /// anchored. The second copy is what lets the furniture drift off the body it names, and it drifts
    /// silently: both look right on a body standing still.</para>
    /// <para>WHAT THIS DOES NOT CARRY is the breath. A breath moves the chest and leaves the feet alone, so
    /// it rides <see cref="Torso(in BodyPose, in WalkPose)"/> instead, and anything anchored on the drawn crown reads that one rather
    /// than this.</para>
    /// </remarks>
    public Matrix4x4 Body(in BodyPose pose, in WalkPose walk)
    {
        float hip = RightHip.Y;
        Matrix4x4 upright = Matrix4x4.CreateTranslation(0f, -hip, 0f)
            * Matrix4x4.CreateRotationX(walk.Lean)
            * Matrix4x4.CreateTranslation(0f, hip, 0f);
        Matrix4x4 rooted = walk.RootPitch == 0f && walk.RootRoll == 0f
            ? upright
            : upright * Matrix4x4.CreateRotationZ(walk.RootRoll) * Matrix4x4.CreateRotationX(walk.RootPitch);
        return rooted
            * Matrix4x4.CreateRotationY(pose.Yaw)
            * Matrix4x4.CreateTranslation(pose.Position + new Vector3(0f, walk.Bob, 0f));
    }

    /// <summary>The UPPER BODY's transform for one frame: the whole body, with the breath's own rise and tip
    /// laid on top of it. The torso, the head, both arms and anything a hand holds compose inside THIS, and
    /// the two legs compose inside <see cref="Body"/> instead, which is what keeps the feet on the ground
    /// while the chest moves.</summary>
    /// <param name="pose">Where and which way the body draws.</param>
    /// <param name="walk">This frame's cycle. <see cref="WalkPose.Rest"/>, or any pose with no torso rise and
    /// no torso lean in it, reduces this to <see cref="Body"/> exactly.</param>
    /// <remarks>
    /// THE PIVOT IS THE BASE OF THE TORSO, which is hip height on this rig: the joint a body actually folds
    /// at, and the same height <see cref="Body"/> leans the whole body about. So the crown travels exactly as
    /// far through a breath as it would if the breath moved everything (18 mm on a person), and the soles do
    /// not travel at all.
    /// <para>The rise is applied along the body's OWN up axis, inside the yaw, so it lifts the chest rather
    /// than the world's y once the body is turned and leaning.</para>
    /// <para>It is still composed unconditionally for a body with no breath in it, because a frame that
    /// skipped it would leave the arms on the pelvis and the torso above it.</para>
    /// </remarks>
    public Matrix4x4 Torso(in BodyPose pose, in WalkPose walk) => Torso(walk, Body(pose, walk));

    /// <summary>The same upper-body frame off a body matrix that has already been built, for a caller that
    /// needs both (a composition does: the legs take the body and everything above the hips takes this).
    /// </summary>
    /// <param name="walk">This frame's cycle, for the torso's own rise and lean.</param>
    /// <param name="body">That same frame's <see cref="Body"/>, for the SAME pose and walk. Passing a body
    /// matrix built from anything else puts the chest on one body and the legs on another.</param>
    public Matrix4x4 Torso(in WalkPose walk, in Matrix4x4 body)
    {
        if (walk.TorsoRise == 0f && walk.TorsoLean == 0f) return body;
        float hip = RightHip.Y;
        return Matrix4x4.CreateTranslation(0f, -hip, 0f)
            * Matrix4x4.CreateRotationX(walk.TorsoLean)
            * Matrix4x4.CreateTranslation(0f, hip + walk.TorsoRise, 0f)
            * body;
    }

    /// <summary>One limb's transform inside its PARENT's local frame: swing about the joint, then out to the
    /// joint. Multiply by the parent's own matrix (the body's for an upper arm or a thigh, the upper arm's
    /// for a forearm) to draw it.</summary>
    /// <param name="pivot">The joint, one of the four above.</param>
    /// <param name="swing">The angle from <see cref="WalkPose"/>, radians, positive being FORWARD.</param>
    /// <remarks>
    /// The negation is the whole content and is worth reading twice. A limb piece hangs along its local -y
    /// from the joint, and <see cref="Matrix4x4.CreateRotationX(float)"/> carries -y toward -z for a positive angle,
    /// which is BEHIND the body. Negating it here is what makes a positive angle in <see cref="WalkPose"/>
    /// mean forward everywhere else, so a cycle can be read and tested without holding a sign in your head.
    /// <para>Static, and the one part of the rig that is: a swing about a pivot is the same rule at any size,
    /// and the size is already in the pivot it is handed.</para>
    /// </remarks>
    public static Matrix4x4 Limb(Vector3 pivot, float swing) =>
        Matrix4x4.CreateRotationX(-swing) * Matrix4x4.CreateTranslation(pivot);

    /// <summary>One limb's transform with a YAW under its swing: the swing about the joint, then a turn about
    /// the body's own up axis, then out to the joint. <see cref="Limb(Vector3, float)"/> is this at a yaw of
    /// zero, which is every limb a walk drives.</summary>
    /// <param name="pivot">The joint.</param>
    /// <param name="swing">The angle from <see cref="WalkPose"/>, radians, positive being FORWARD.</param>
    /// <param name="yaw">How far the limb is carried sideways about the body's up axis, radians, positive
    /// carrying it toward engine -x. That is OUTWARD for the weapon arm (a body faces +z at yaw 0 and wears
    /// its right side at -x, see the note on this type) and across the chest for the off one, which is why
    /// <see cref="WalkPose.RightArmYaw"/> names a side rather than being one number for both arms.</param>
    /// <remarks>
    /// THE ORDER IS THE WHOLE CONTENT. The yaw is composed OUTSIDE the swing, so the swing happens in a plane
    /// the yaw has already turned: an arm yawed out to the side and then swung forward comes up sideways,
    /// which is a sweep, where an arm swung first and then yawed would rotate a finished pose about the body
    /// and read as the whole shoulder turning.
    /// <para>Both angles are negated for the same reason the single-axis overload negates its swing: a limb
    /// hangs along its local -y, and negating here is what lets <see cref="WalkPose"/> carry angles a reader
    /// can hold in their head (positive forward, positive outward) instead of a sign convention.</para>
    /// </remarks>
    public static Matrix4x4 Limb(Vector3 pivot, float swing, float yaw) =>
        yaw == 0f
            ? Limb(pivot, swing)
            : Matrix4x4.CreateRotationX(-swing) * Matrix4x4.CreateRotationY(-yaw)
              * Matrix4x4.CreateTranslation(pivot);

    /// <summary>A forearm's transform inside its own upper arm's frame.</summary>
    /// <param name="flexion">How far the elbow is bent, radians, never negative. An elbow bends ONE way, so
    /// the flexion carries the hand FORWARD and up, which is a positive swing.</param>
    public Matrix4x4 Elbow(float flexion) => Limb(ElbowFromShoulder, flexion);

    /// <summary>A shin's transform inside its own thigh's frame.</summary>
    /// <param name="flexion">How far the knee is bent, radians, never negative. A knee bends the OTHER way
    /// from an elbow, heel toward the buttock, so the flexion is a BACKWARD swing and is negated here. That
    /// negation is the one place the difference between the two hinges is written down, which is why
    /// <see cref="WalkPose"/> can carry both as plain non-negative magnitudes.</param>
    public Matrix4x4 Knee(float flexion) => Limb(KneeFromHip, -flexion);

    static Vector3 Mirror(Vector3 point) => new(-point.X, point.Y, point.Z);
}
