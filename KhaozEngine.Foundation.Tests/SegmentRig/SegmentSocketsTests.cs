using System;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>
/// The SOCKET half: the two rotations a held piece rides, the hand chain that carries it, and the containment
/// check a consumer measures its own fist with.
/// </summary>
/// <remarks>
/// Every piece of geometry here is measured through <see cref="SegmentSockets"/> and
/// <see cref="HumanoidSkeleton.Compose"/> rather than transcribed, which is the whole reason these moved out
/// of the test project: a sign or an order that changed inside the package fails here rather than agreeing
/// with a copy of itself.
/// </remarks>
public class SegmentSocketsTests
{
    const long Seed = 4242L;

    /// <summary>
    /// The WRIST is the identity at zero, so an unarmed frame and a piece at its resting lean compose to the
    /// bits they always did, and a positive angle carries the piece's head the way the body faces.
    /// </summary>
    [Fact]
    public void TheWristIsTheIdentityAtZeroAndTipsThePieceTheWayTheBodyFaces()
    {
        Assert.Equal(Matrix4x4.Identity, SegmentSockets.Wrist(0f));

        // A point up the piece's own length, which is where a blade's tip and a tool's head both are.
        var up = new Vector3(0f, 0.5f, 0f);
        Vector3 upright = Vector3.Transform(up, SegmentSockets.Wrist(0f));
        Vector3 tipped = Vector3.Transform(up, SegmentSockets.Wrist(0.4f));
        Assert.Equal(up, upright);
        Assert.True(tipped.Z > upright.Z + 0.1f, "a positive wrist did not carry the head forward, to +z");
        Assert.True(tipped.Y < upright.Y, "tipping the piece forward should lower its head, not raise it");
        // And it is a rotation about the hand's own x alone, so it never moves the piece sideways.
        Assert.Equal(0f, tipped.X, 6);
    }

    /// <summary>
    /// The OFF hand's turn is the identity at zero, so the axis a guard pose writes cannot move a piece that
    /// is merely hanging. Lifted from Grimhollow's shield carry suite, with the game's own plate orientation
    /// swapped for this project's neutral one.
    /// </summary>
    /// <remarks>Asserted on the MATRIX rather than on a clearance, because that is the only form of the claim
    /// that cannot pass by accident: a turn that moved the piece a millimetre would still clear everything
    /// around it and every other assertion would go on passing.</remarks>
    [Fact]
    public void TheOffHandsTurnIsTheIdentityAtZeroSoAHangingPlateDoesNotMove()
    {
        Assert.Equal(Matrix4x4.Identity, SegmentSockets.OffTurn(0f, Vector3.Zero));
        Assert.Equal(Matrix4x4.Identity, SegmentSockets.OffTurn(0f, new Vector3(0.29f, 0.63f, 0f)));

        foreach (WalkPose walk in Stride())
        {
            Matrix4x4[] at = TestHeldPieces.Standing(walk);
            // The chain as it stood before the off hand had a turn at all, transcribed on purpose: this is
            // the one place a transcription is the assertion rather than a risk.
            Matrix4x4 before = TestHeldPieces.PlateGrip
                * Matrix4x4.CreateTranslation(BodyRig.Human.HandFromElbow) * at[HumanoidSkeleton.ForearmLeft];
            Assert.Equal(before, TestHeldPieces.HeldInOffHand(at));
            Assert.Equal(before, TestHeldPieces.HeldInOffHand(at, 0f));
            // AND THE TURN CAN MOVE IT, so the equality above is a fact about zero rather than about a
            // channel nothing reads.
            Assert.NotEqual(before, TestHeldPieces.HeldInOffHand(at, -MathF.PI / 2f));
        }

        // It turns the piece WHERE IT IS rather than swinging it round the body: the axis is the vertical
        // through the FIST, so nothing moves up or down and nothing changes its distance from the fist, and
        // the face comes round a quarter turn from the character's left to straight ahead.
        Matrix4x4[] rest = TestHeldPieces.Standing(WalkPose.Rest);
        Vector3 fist = (Matrix4x4.CreateTranslation(BodyRig.Human.HandFromElbow)
            * rest[HumanoidSkeleton.ForearmLeft]).Translation;
        Matrix4x4 hanging = TestHeldPieces.HeldInOffHand(rest);
        Matrix4x4 turned = TestHeldPieces.HeldInOffHand(rest, -MathF.PI / 2f);
        Assert.Equal(hanging.Translation.Y, turned.Translation.Y, 5);
        Assert.Equal(Vector3.Distance(fist, hanging.Translation),
            Vector3.Distance(fist, turned.Translation), 5);
        Vector3 face = Vector3.TransformNormal(Vector3.UnitZ, turned);
        Assert.Equal(1f, face.Z, 4);
        Assert.Equal(0f, face.X, 4);
    }

    /// <summary>
    /// <see cref="SegmentSockets.Held"/> IS the five-factor product a consumer would otherwise hand write:
    /// the piece's own orientation, the weapon hand's tip, the socket down the forearm, that forearm's whole
    /// chain, and the off hand's turn last. Written out here at angles that do not commute with each other,
    /// because at zero almost any order agrees. What this pins is the ORDER: the two rotations themselves are
    /// the package's own here, and the wrist and off-turn tests above are what pin those.
    /// </summary>
    [Fact]
    public void AHeldPieceIsExactlyTheHandWrittenFiveFactorProduct()
    {
        BodyRig rig = BodyRig.Human;
        Matrix4x4[] at = TestHeldPieces.Standing(
            new WalkPose(LeftArm: -0.4f, RightArm: 0.7f, LeftLeg: 0.4f, RightLeg: -0.7f,
                LeftElbow: 0.5f, RightElbow: 0.35f));

        foreach ((float tip, float turn) in new[] { (0f, 0f), (0.8f, 0f), (0f, -1.1f), (0.8f, -1.1f) })
        {
            foreach ((Matrix4x4 grip, int piece) in new[]
            {
                (TestHeldPieces.BladeGrip, HumanoidSkeleton.ForearmRight),
                (TestHeldPieces.PlateGrip, HumanoidSkeleton.ForearmLeft),
                (Matrix4x4.Identity, HumanoidSkeleton.ForearmRight),
            })
            {
                Matrix4x4 forearm = at[piece];
                Matrix4x4 hand = Matrix4x4.CreateTranslation(rig.HandFromElbow) * forearm;
                Matrix4x4 written = grip * SegmentSockets.Wrist(tip)
                    * Matrix4x4.CreateTranslation(rig.HandFromElbow) * forearm
                    * SegmentSockets.OffTurn(turn, hand.Translation);
                Assert.Equal(written, SegmentSockets.Held(rig, grip, forearm, tip, turn));
            }
        }

        // The two angles are two axes for two jobs, so the tip and the turn do not undo each other: each one
        // on its own moves the piece, and neither is a no-op at the other's value.
        Matrix4x4 plain = SegmentSockets.Held(rig, TestHeldPieces.BladeGrip, at[HumanoidSkeleton.ForearmRight]);
        Assert.NotEqual(plain,
            SegmentSockets.Held(rig, TestHeldPieces.BladeGrip, at[HumanoidSkeleton.ForearmRight], tip: 0.8f));
        Assert.NotEqual(plain,
            SegmentSockets.Held(rig, TestHeldPieces.BladeGrip, at[HumanoidSkeleton.ForearmRight], turn: -1.1f));
        Assert.Throws<ArgumentNullException>(
            () => SegmentSockets.Held(null!, Matrix4x4.Identity, Matrix4x4.Identity));
    }

    /// <summary>
    /// <see cref="SegmentSockets.IsInside"/>, which is the sanity check a game makes when it wants to know
    /// whether the socket its rig declares actually lands in the fist its mesh has. Inclusive on every face,
    /// and it fails on each of the six independently, which is the half a copied comparison gets wrong.
    /// </summary>
    [Fact]
    public void ASocketIsInsideABoxOnEveryFaceAndOutsideItOnEachOneIndependently()
    {
        (Vector3 Min, Vector3 Max) box = (new Vector3(-0.05f, 0.2f, -0.04f), new Vector3(0.05f, 0.3f, 0.04f));
        var centre = new Vector3(0f, 0.25f, 0f);
        Assert.True(SegmentSockets.IsInside(centre, box));
        Assert.True(SegmentSockets.IsInside(box.Min, box), "a point exactly on the min corner is inside");
        Assert.True(SegmentSockets.IsInside(box.Max, box), "a point exactly on the max corner is inside");

        // Each of the six faces on its own, so a comparison copied from one axis to another and left naming
        // the first axis fails here rather than passing on five out of six.
        foreach (Vector3 outside in new[]
        {
            centre with { X = box.Min.X - 0.001f }, centre with { X = box.Max.X + 0.001f },
            centre with { Y = box.Min.Y - 0.001f }, centre with { Y = box.Max.Y + 0.001f },
            centre with { Z = box.Min.Z - 0.001f }, centre with { Z = box.Max.Z + 0.001f },
        })
            Assert.False(SegmentSockets.IsInside(outside, box), outside + " should be outside " + box);

        // A rig's own socket against a box measured around it, which is the real call: the hand socket is
        // inside a fist-sized box at the point the elbow carries it to, and outside one at the elbow itself.
        Vector3 socket = BodyRig.Human.HandFromElbow;
        var fist = (socket - new Vector3(0.045f), socket + new Vector3(0.045f));
        Assert.True(SegmentSockets.IsInside(socket, fist));
        Assert.False(SegmentSockets.IsInside(Vector3.Zero, fist));
    }

    /// <summary>
    /// A held piece rides the SOCKET through the arm's swing and then the body's facing. Two failures hide
    /// here: a socket applied in world space (indistinguishable from right at yaw 0, so the turned case is
    /// the one that matters) and a piece hung off the BODY instead of the arm, which leaves a weapon hanging
    /// in the air while the fist that holds it swings past.
    /// </summary>
    [Fact]
    public void AHeldPieceRidesTheSwungArmThroughTheBodysFacing()
    {
        BodyRig rig = BodyRig.Human;
        var position = new Vector3(12f, 3f, -8f);
        Vector3 restGrip = rig.RightShoulder + rig.HandFromShoulder;

        Vector3 facingSouth = InWeaponHand(rig, new BodyPose(position, 0f), WalkPose.Rest).Translation;
        Assert.Equal(position.X + restGrip.X, facingSouth.X, 3);
        Assert.Equal(position.Y + restGrip.Y, facingSouth.Y, 3);
        Assert.Equal(position.Z + restGrip.Z, facingSouth.Z, 3);

        // A quarter turn east. The socket rotates with the body: CreateRotationY carries local (x, y, z) to
        // (z, y, -x), so the right hand's local -x lands on world +z. That is the geometry as well as the
        // algebra, and it is the half worth reading twice: facing EAST, a body's right hand is to the SOUTH,
        // and south is world +z.
        Vector3 facingEast =
            InWeaponHand(rig, new BodyPose(position, MathF.PI / 2f), WalkPose.Rest).Translation;
        Assert.Equal(position.X, facingEast.X, 3);
        Assert.Equal(position.Y + restGrip.Y, facingEast.Y, 3);
        Assert.Equal(position.Z - restGrip.X, facingEast.Z, 3);
        Assert.True(facingEast.Z > position.Z,
            "the right hand should swing SOUTH of the body when it faces east");
    }

    /// <summary>A held piece follows the arm that holds it. A piece anchored to the body would sit still
    /// while the arm swung out from under it, which is the whole reason the socket is measured off a joint
    /// rather than off the feet.</summary>
    [Fact]
    public void AHeldPieceFollowsTheSwungWeaponArm()
    {
        BodyRig rig = BodyRig.Human;
        var pose = new BodyPose(new Vector3(4f, 0f, 4f), 0f);
        Vector3 still = InWeaponHand(rig, pose, WalkPose.Rest).Translation;

        // The weapon arm forward, the off arm back, which is what the cycle answers while the weapon-side leg
        // is back.
        var walk = new WalkPose(LeftArm: -0.6f, RightArm: 0.6f, LeftLeg: 0.6f, RightLeg: -0.6f);
        Vector3 swung = InWeaponHand(rig, pose, walk).Translation;

        Assert.True(swung.Z > still.Z + 0.2f,
            "the piece stayed at z " + swung.Z + " while the arm swung forward from " + still.Z);
        Assert.True(swung.Y > still.Y, "the piece should rise with the arm rather than sink through the leg");
        // Sideways it does not move at all: the swing is about the arm's own x, so the hand stays on its side.
        Assert.Equal(still.X, swung.X, 4);
    }

    /// <summary>
    /// THE ELBOW ALONE, which is the whole point of hanging a held piece off the FOREARM. At a straight elbow
    /// the two chains are algebraically identical (<c>HandFromElbow + ElbowFromShoulder</c> is
    /// <c>HandFromShoulder</c> by construction), so no other test here can tell a piece on the forearm from a
    /// piece on the upper arm. This one bends the elbow and nothing else: the shoulder stays unswung, so
    /// anything the grip does is the elbow's doing.
    /// <para>The expected point is computed by hand rather than composed out of the same matrices the socket
    /// uses: the forearm is a rigid segment hinging about the elbow, so a flexion of f swings the grip
    /// <c>reach sin f</c> forward and <c>reach (1 - cos f)</c> up out of where the straight arm left it. A
    /// grip riding the upper arm answers the straight-arm point for every f, which is what this fails on.
    /// </para>
    /// </summary>
    [Fact]
    public void AHeldPieceRidesTheForearmThroughTheElbowAlone()
    {
        BodyRig rig = BodyRig.Human;
        var pose = new BodyPose(new Vector3(4f, 0f, 4f), 0f);
        Vector3 straight = pose.Position + rig.RightShoulder + rig.HandFromShoulder;

        // A LOCKED elbow first, where the two chains have to agree to the last decimal: the socket lives in
        // the forearm's frame and is not allowed to move the resting stance by a millimetre.
        Vector3 locked = InWeaponHand(rig, pose, WalkPose.Rest).Translation;
        Assert.Equal(straight.X, locked.X, 4);
        Assert.Equal(straight.Y, locked.Y, 4);
        Assert.Equal(straight.Z, locked.Z, 4);

        // And now the elbow, on its own. This is the bend the cycle carries the whole time a body walks.
        const float flexion = WalkCycle.ElbowBendRadians;
        Vector3 bent = InWeaponHand(rig, pose,
            new WalkPose(0f, 0f, 0f, 0f, RightElbow: flexion)).Translation;

        float forearm = -rig.HandFromElbow.Y;
        Assert.Equal(straight.X, bent.X, 4);
        Assert.Equal(straight.Y + (forearm * (1f - MathF.Cos(flexion))), bent.Y, 4);
        Assert.Equal(straight.Z + (forearm * MathF.Sin(flexion)), bent.Z, 4);
        // Stated as a direction as well as an equality, because the equalities above all still hold with the
        // hinge negated if the sign convention is read backwards in both places at once: an elbow carries the
        // hand FORWARD, which is engine +z, and it lifts it.
        Assert.True(bent.Z > locked.Z + 0.05f,
            "the grip is at z " + bent.Z + ", which is not forward of the straight arm's " + locked.Z);
        Assert.True(bent.Y > locked.Y, "bending the elbow should raise the grip, not drop it");
    }

    /// <summary>
    /// WHAT THE WEAPON HAND HOLDS STANDS CLEAR OF THE LEGS through the whole breath, sampled over a full
    /// period of <see cref="IdleBreath"/> on the neutral tool and on a long blade.
    /// </summary>
    /// <remarks>
    /// Revived from Grimhollow's breath suite, which measured the game's own sword and shield meshes against
    /// authored leg boxes. Neither of those survives the lift, so this measures the synthetic shapes in
    /// <see cref="TestHeldPieces"/> against the leg's own AXIS, with the clearance floor the rig already
    /// declares (<see cref="BodyRig.UpperArmRadiusMetres"/>, plus the haft's own half width). That is a
    /// weaker floor than a thigh's real half width, so this is the check that a resting arm does not swing
    /// what it holds INTO the leg rather than a pinned millimetre budget. Be plain about what carries it
    /// today: the breath drives no shoulder yaw and no roll, so the arm stays in the shoulder's own plane and
    /// the clearance is dominated by the sideways gap between shoulder and hip, which is a rig constant no
    /// phase can eat. It is a guard against a future breath that sways the arm INWARD, and the 64 samples
    /// only start to matter on the day one does. The off hand's plate stayed behind
    /// with it: there is no synthetic plate shape here to sample, and inventing one would mean inventing the
    /// numbers this is meant to avoid.
    /// </remarks>
    [Fact]
    public void WhatTheWeaponHandHoldsStandsClearOfTheLegsThroughTheWholeBreath()
    {
        BodyRig rig = BodyRig.Human;
        float floor = rig.UpperArmRadiusMetres + TestHeldPieces.HaftHalfWidthMetres;
        float worst = float.MaxValue;
        float worstAt = 0f;
        string worstPiece = "";

        for (int i = 0; i < 64; i++)
        {
            float seconds = IdleBreath.PeriodSeconds * i / 64f;
            WalkPose breath = IdleBreath.Compose(WalkPose.Rest, IdleBreath.PoseAt(seconds, Seed, rig), 1f);
            Matrix4x4[] at = TestHeldPieces.Standing(breath);

            foreach ((string name, Matrix4x4 grip) in new[]
            {
                ("tool", TestHeldPieces.ToolGrip), ("blade", TestHeldPieces.BladeGrip),
            })
            {
                Matrix4x4 held = SegmentSockets.Held(rig, grip, at[HumanoidSkeleton.ForearmRight]);
                foreach (Vector3 point in Sampled(name, held))
                {
                    float clearance = ToLegs(rig, at, point);
                    if (clearance >= worst) continue;
                    worst = clearance;
                    worstAt = seconds / IdleBreath.PeriodSeconds;
                    worstPiece = name;
                }
            }
        }

        Assert.True(worst > floor,
            $"the {worstPiece} comes within {worst} m of a leg axis at phase {worstAt} of the breath, "
            + $"inside the {floor} m floor");
    }

    // The held piece's transform in the weapon hand, through the package's own composition and socket.
    static Matrix4x4 InWeaponHand(BodyRig rig, in BodyPose pose, in WalkPose walk)
    {
        Span<Matrix4x4> at = stackalloc Matrix4x4[HumanoidSkeleton.PieceCount];
        HumanoidSkeleton.Compose(rig, pose, walk, at);
        return SegmentSockets.Held(rig, Matrix4x4.Identity, at[HumanoidSkeleton.ForearmRight]);
    }

    // Every point of one held shape a clearance is measured at, in the space it is composed in. The tool has
    // its own sampler. The blade is a bare length, so it is sampled from the pommel to the point.
    static System.Collections.Generic.IEnumerable<Vector3> Sampled(string name, Matrix4x4 held)
    {
        if (name == "tool")
        {
            foreach (Vector3 point in TestHeldPieces.ToolPoints(held, TestHeldPieces.PommelBaseMetres))
                yield return point;
            yield break;
        }

        const int steps = 16;
        for (int i = 0; i <= steps; i++)
        {
            float along = TestHeldPieces.PommelBaseMetres
                + ((TestHeldPieces.BladePoint.Y - TestHeldPieces.PommelBaseMetres) * i / steps);
            yield return Vector3.Transform(new Vector3(0f, along, 0f), held);
        }
    }

    // How far a point is from the nearer leg, measured to the thigh's and the shin's own axes through the
    // composition it was taken at.
    static float ToLegs(BodyRig rig, Matrix4x4[] at, Vector3 point)
    {
        float worst = float.MaxValue;
        foreach ((int thigh, int shin) in new[]
        {
            (HumanoidSkeleton.ThighLeft, HumanoidSkeleton.ShinLeft),
            (HumanoidSkeleton.ThighRight, HumanoidSkeleton.ShinRight),
        })
        {
            Vector3 hip = Vector3.Transform(Vector3.Zero, at[thigh]);
            Vector3 knee = Vector3.Transform(Vector3.Zero, at[shin]);
            Vector3 sole = TestBodies.Sole(rig, at, shin);
            worst = MathF.Min(worst, TestHeldPieces.ToSegment(point, hip, knee));
            worst = MathF.Min(worst, TestHeldPieces.ToSegment(point, knee, sole));
        }

        return worst;
    }

    // The resting stance and four points around one full stride, which between them cover both arms forward,
    // both arms back and both mid swing.
    static System.Collections.Generic.IEnumerable<WalkPose> Stride()
    {
        yield return WalkPose.Rest;
        for (int quarter = 0; quarter < 4; quarter++) yield return WalkAt(quarter / 4f);
    }

    // One walk pose at a phase, out of the real cycle rather than hand written: the cycle is what decides how
    // far an arm swings and how far an elbow folds, and a transcribed pose would not notice either changing.
    static WalkPose WalkAt(float phase)
    {
        var cycle = new WalkCycle(BodyRig.Human);
        const float dt = 1f / 60f;
        float travelled = 0f;
        cycle.Advance(Vector3.Zero, dt);
        // Long enough for the blend to run all the way in, then on to the wanted phase.
        for (int i = 0; i < 400; i++)
        {
            travelled += BodyRig.Human.StrideMetres * dt;
            cycle.Advance(new Vector3(0f, 0f, travelled), dt);
        }

        for (int i = 0; i < 400 && MathF.Abs(cycle.Phase - phase) > 0.01f; i++)
        {
            travelled += BodyRig.Human.StrideMetres * dt;
            cycle.Advance(new Vector3(0f, 0f, travelled), dt);
        }

        Assert.Equal(1f, cycle.Weight);
        return cycle.Pose;
    }
}
