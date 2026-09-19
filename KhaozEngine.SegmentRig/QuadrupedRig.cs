using System.Numerics;

namespace KhaozEngine.SegmentRig;

/// <summary>One four-legged BODY's proportions: where its four legs hang, where each one hinges half way
/// down, where its head turns on its neck, how tall it stands and how far it travels per stride.
/// <see cref="BodyRig"/>'s quadruped sibling, and read by the same kinds of consumer: the composition that
/// draws the pieces, the gait that swings them, and the tests that pin the mesh to the joints.</summary>
/// <remarks>
/// These are the SAME NUMBERS whatever split the mesh into pieces authored them about, in the engine frame.
/// Edit one and edit the other: a pivot that drifts off the mesh tears the leg out of the body the moment it
/// swings, and nothing fails.
/// <para>A leg is TWO pieces, hinged: an upper leg carrying a cannon. The upper piece is authored about the
/// SHOULDER or the HIP, which sits up inside the body where the real joint is, and it is the visible column
/// joined to a BALL centred on that joint, turning in a socket cut into the body: a sphere about the pivot
/// maps onto itself as the leg swings, so the socket never shows its floor and nothing of the body is left
/// behind as a stump. The upper piece carries a smaller ball on its hinge too, with the cannon's top ring
/// inside it, so a deep fold shows a round knee rather than two cut ends. That is what lets the pivot be
/// HIGH: a real animal swings the whole foreleg from the shoulder joint, and a leg swinging from the belly
/// line reads as a pendulum under a box. The cannon is authored about its hinge, the carpus on a foreleg and
/// the hock on a hind, and hangs down -y from there exactly as a humanoid shin does.</para>
/// <para>The BODY is one piece, and the sway is the whole trunk's. Parting it into a chest and a rump so
/// the two could yaw against each other was tried: whatever the cut, a two-degree turn stepped the skin
/// edges a centimetre against each other at the flank, because the skin is no surface of revolution about
/// any axis. So the trunk yaws about its own centre (<see cref="Trunk"/>) with the legs turning under it,
/// which is the sashay a walking animal shows from above without a seam to open.</para>
/// <para>THE TWO HINGES BEND OPPOSITE WAYS, which is the anatomy every quadruped shares and the thing this
/// type writes down once. A foreleg's carpus folds the cannon BACK, the hoof tucking up under the chest,
/// the way a knee carries a heel. A hind leg's hock folds the cannon FORWARD, the hoof coming up under the
/// belly, the way an ankle lifts a toe. <see cref="ForeHinge"/> and <see cref="HindHinge"/> carry that sign,
/// so <see cref="QuadrupedPose"/> holds both flexions as plain non-negative magnitudes.</para>
/// <para>Which side is which follows <see cref="BodyRig"/>: the body faces engine +z at yaw 0 and wears its
/// RIGHT side at engine -x.</para>
/// <para>No instance is declared here. A creature's measurements are game CONTENT, so a game builds its own
/// with an object initializer, exactly as it does for a <see cref="BodyRig"/> other than
/// <see cref="BodyRig.Human"/>.</para>
/// </remarks>
public sealed record QuadrupedRig
{
    /// <summary>The right shoulder, metres from the body's feet: the joint the right foreleg swings about.
    /// Negative x, because the body faces engine +z at yaw 0 and wears its right side to the west.</summary>
    public required Vector3 RightShoulder { get; init; }

    /// <summary>The right hip, on the same side as <see cref="RightShoulder"/> and behind it (negative z).
    /// </summary>
    public required Vector3 RightHip { get; init; }

    /// <summary>The carpus, metres from the SHOULDER of either foreleg: the hinge half way down the leg and
    /// the fore cannon piece's own origin.</summary>
    public required Vector3 ForeHingeFromShoulder { get; init; }

    /// <summary>The hock, metres from the HIP of either hind leg, and the hind cannon piece's own origin. On
    /// most animals it sits a little behind the hip's vertical, because the hind cannon leans: hock back,
    /// hoof forward.</summary>
    public required Vector3 HindHingeFromHip { get; init; }

    /// <summary>How far the bottom of a fore hoof hangs below the carpus at rest, metres. The gait reads
    /// it to know how far a straight leg reaches, and a test reads it to pin the hoof to the floor.</summary>
    public required float ForeSoleFromHinge { get; init; }

    /// <summary>How far the bottom of a hind hoof hangs below the hock at rest, metres.</summary>
    public required float HindSoleFromHinge { get; init; }

    /// <summary>The POLL, metres from the body's feet: where the skull meets the neck, and the point the
    /// head piece nods about. The head is authored about it, so it draws at this translation inside the
    /// body frame the way a head piece draws at the neck base on a two-legged rig.</summary>
    public required Vector3 HeadPivot { get; init; }

    /// <summary>How tall the body stands at rest, metres from the sole to the top of the back. What a breath
    /// scales its rise by, exactly as it does off <see cref="BodyRig.RestHeightMetres"/>.</summary>
    public required float RestHeightMetres { get; init; }

    /// <summary>How far this body walks per full cycle of the gait, metres. The one number the shared
    /// <see cref="WalkCycle"/> reads, through the <see cref="BodyRig"/> a game pairs with this one, which
    /// carries the same value.</summary>
    public required float StrideMetres { get; init; }

    /// <summary>The left shoulder, the mirror of <see cref="RightShoulder"/> in x alone.</summary>
    public Vector3 LeftShoulder => Mirror(RightShoulder);

    /// <summary>The left hip, the mirror of <see cref="RightHip"/> in x alone.</summary>
    public Vector3 LeftHip => Mirror(RightHip);

    /// <summary>The vertical reach of a straight foreleg from its shoulder to the floor, metres, which is the
    /// radius the hoof swings on and the number the gait sizes its swing from.</summary>
    public float ForeLegMetres => -ForeHingeFromShoulder.Y + ForeSoleFromHinge;

    /// <summary>The vertical reach of a straight hind leg from its hip to the floor, metres.</summary>
    public float HindLegMetres => -HindHingeFromHip.Y + HindSoleFromHinge;

    /// <summary>The WHOLE body's transform for one frame: a strike's lunge (<see cref="Strike"/>), then the
    /// run lean about the shoulder height, then the facing, then the position with the gait's own bob folded
    /// into it. The four legs compose inside this, and the barrel and the head compose inside
    /// <see cref="Torso"/>.</summary>
    /// <param name="pose">Where and which way the body draws.</param>
    /// <param name="walk">This frame's biped-shaped pose, read for its run lean only. Its own bob is a
    /// two-legged one and is ignored: the quadruped's comes from its own gait. <c>RootPitch</c> and
    /// <c>RootRoll</c> are NOT read here either. Only the two-legged <see cref="BodyRig"/> turns about its
    /// root, because nothing four-legged swims or falls yet. A game that sets them on a quadruped gets an
    /// upright animal and no error, so add the read here when the first one needs it.</param>
    /// <param name="gait">This frame's four-legged pose, for the bob, the surge and the pitch.</param>
    /// <remarks>Rotation THEN translation, the engine's own model-transform hand. The lean tips about the
    /// shoulder height rather than about a hip, because that is the pivot a body on four legs leans about,
    /// and a pivot on the floor would drive the hind hooves into the ground.</remarks>
    public Matrix4x4 Body(in BodyPose pose, in WalkPose walk, in QuadrupedPose gait)
    {
        float chest = RightShoulder.Y;
        Matrix4x4 body = Matrix4x4.CreateTranslation(0f, -chest, 0f)
            * Matrix4x4.CreateRotationX(walk.Lean)
            * Matrix4x4.CreateTranslation(0f, chest, 0f)
            * Matrix4x4.CreateRotationY(pose.Yaw)
            * Matrix4x4.CreateTranslation(pose.Position + new Vector3(0f, gait.Bob, 0f));
        // Skipped rather than multiplied by an identity when there is no strike, so every other frame composes
        // to the bits it always did.
        return gait.Surge == 0f && gait.Pitch == 0f ? body : Strike(gait.Surge, gait.Pitch) * body;
    }

    /// <summary>A STRIKE's motion of the whole body in its own rest frame: the pitch about the line through
    /// the two shoulder joints, then the surge along the way the body faces. It goes in under
    /// <see cref="Body"/>, so the trunk and all four legs take it together.</summary>
    /// <param name="surge">How far forward, metres, from <see cref="QuadrupedPose.Surge"/>.</param>
    /// <param name="pitch">How far the muzzle end dips, radians, from <see cref="QuadrupedPose.Pitch"/>.</param>
    /// <remarks>BOTH FRAMES, on purpose. The legs hang off <see cref="Body"/> and the trunk off
    /// <see cref="Torso"/> over it, so a motion put here carries every socket and the ball turning in it by the
    /// same transform and no joint can open, whatever the numbers. Tipping the trunk alone would part each
    /// hip socket from its ball by the pitch times the metre between the shoulders and the hips. The price is
    /// that the hooves move with the body too, so a stroke that wants them left where they stood swings the
    /// legs back under it.
    /// <para>About the SHOULDERS because that makes the body's own <see cref="QuadrupedPose.Bob"/> exactly how
    /// far the shoulder joints rise or sink, which is the one height a foreleg solve reads.</para></remarks>
    public Matrix4x4 Strike(float surge, float pitch) =>
        Matrix4x4.CreateTranslation(0f, -RightShoulder.Y, -RightShoulder.Z)
        * Matrix4x4.CreateRotationX(pitch)
        * Matrix4x4.CreateTranslation(0f, RightShoulder.Y, RightShoulder.Z + surge);

    /// <summary>The TORSO's transform for one frame: the whole body with the gait's roll about the spine
    /// and the breath's rise laid on top of it. The body piece and the head compose inside this through
    /// <see cref="Trunk"/>, and the legs inside <see cref="Body"/> through the same yaw, which is what keeps
    /// the hooves on the ground while the ribcage lifts and the back rolls.</summary>
    /// <param name="walk">This frame's pose, for the breath's rise. The breath's LEAN is deliberately not
    /// read: a biped's chest tipping back as it fills is a horizontal barrel see-sawing about its shoulders,
    /// which lifts the rump by centimetres for a rise of millimetres.</param>
    /// <param name="gait">This frame's gait, for the roll.</param>
    /// <param name="body">That same frame's <see cref="Body"/>.</param>
    /// <remarks>The roll is about the body's own forward axis at SHOULDER height, the line the spine runs
    /// along, so the back sways one way and the belly the other and the joint sockets, which sit at about
    /// that height, barely move against the balls the legs turn in.</remarks>
    public Matrix4x4 Torso(in WalkPose walk, in QuadrupedPose gait, in Matrix4x4 body)
    {
        float spine = RightShoulder.Y;
        Matrix4x4 roll = gait.Roll == 0f
            ? Matrix4x4.Identity
            : Matrix4x4.CreateTranslation(0f, -spine, 0f) * Matrix4x4.CreateRotationZ(gait.Roll)
              * Matrix4x4.CreateTranslation(0f, spine, 0f);
        Matrix4x4 rise = walk.TorsoRise == 0f
            ? Matrix4x4.Identity
            : Matrix4x4.CreateTranslation(0f, walk.TorsoRise, 0f);
        return roll * rise * body;
    }

    /// <summary>The TRUNK's yaw about the body's own vertical axis, inside a parent frame: positive swings
    /// the tail end toward the animal's left (+x) and the muzzle end to its right. The body piece and the head
    /// compose through this on <see cref="Torso"/>, and every leg through it on <see cref="Body"/>, so the
    /// hips and shoulders travel with the trunk and the hooves turn under it.</summary>
    /// <param name="yaw">The yaw, radians, from <see cref="QuadrupedPose.TrunkYaw"/>.</param>
    /// <param name="parent">The frame to yaw inside.</param>
    /// <remarks>Negated inside, because a rotation about +y carries a point BEHIND the axis toward -x, and
    /// the channel is written as where the tail goes, which is the end a walk visibly swings.
    /// </remarks>
    public Matrix4x4 Trunk(float yaw, in Matrix4x4 parent) =>
        yaw == 0f ? parent : Matrix4x4.CreateRotationY(-yaw) * parent;

    /// <summary>A fore cannon's transform inside its own upper leg's frame.</summary>
    /// <param name="flexion">How far the carpus is folded, radians, never negative. It carries the hoof BACK
    /// and up under the chest, which is a backward swing and is negated here.</param>
    public Matrix4x4 ForeHinge(float flexion) => BodyRig.Limb(ForeHingeFromShoulder, -flexion);

    /// <summary>A hind cannon's transform inside its own upper leg's frame.</summary>
    /// <param name="flexion">How far the hock is folded, radians, never negative. It carries the hoof
    /// FORWARD and up under the belly, which is a forward swing and passes through as one.</param>
    public Matrix4x4 HindHinge(float flexion) => BodyRig.Limb(HindHingeFromHip, flexion);

    /// <summary>The head's transform inside the trunk's frame: the nod and the sway about the poll, then out
    /// to it.</summary>
    /// <param name="nod">How far the muzzle is dipped, radians, positive being DOWN. The head reaches forward
    /// (+z) of the poll, and a positive rotation about local x carries +z toward -y.</param>
    /// <param name="yaw">How far the muzzle is swung to the animal's left (+x), radians. A rotation about +y
    /// carries a point AHEAD of the axis toward +x, so this one passes through unnegated.</param>
    public Matrix4x4 Head(float nod, float yaw = 0f) =>
        Matrix4x4.CreateRotationX(nod) * Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(HeadPivot);

    static Vector3 Mirror(Vector3 point) => new(-point.X, point.Y, point.Z);
}
