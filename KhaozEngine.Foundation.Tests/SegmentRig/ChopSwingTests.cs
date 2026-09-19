using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>The chop stroke: where the TOOL HEAD goes, that it stays outside the arm holding it, that the path
/// is a sweep rather than a chop down, that the fall is the fast part, that it wraps without a snap, and that
/// it touches the weapon arm and nothing else.</summary>
/// <remarks>
/// The head's own track is what these assert on rather than the four angles, and that is the point of the
/// file. A held piece rides the forearm through the elbow, the shoulder's two axes and the grip's own tilt,
/// and all but the yaw are rotations about the same axis, so those angles add and the tool ends up pointing
/// the OPPOSITE way round from the obvious reading of any of them on its own. A stroke with a shoulder that
/// swung forward into a tree and a tool that stood up as it did would pass a test that pinned the shoulder.
/// <para>The chain and the tool are <see cref="TestHeldPieces"/>'s, which is a test's own content: a grip
/// orientation is a property of a mesh and a head box is a measuring stick. Both become the package's
/// business only where the socket helpers land, and the two have to be edited together until then.</para>
/// </remarks>
public class ChopSwingTests
{
    // About where a trunk is cut, metres off the ground: chest height on a 1.5 m body.
    const float TrunkHeightMetres = 1f;

    // The TORSO, as a box on the body's own axis. A person's torso stacks sections from the hips at 0.62 up
    // to the collar at 1.145, and the widest of them reaches 0.155 across and 0.16 through, so a box on those
    // two half widths contains every section of it. Conservative on purpose: a clearance floor wants the body
    // it measures against to be at least as big as the body that is drawn.
    const float TorsoBottomMetres = 0.62f;
    const float TorsoTopMetres = 1.145f;
    const float TorsoHalfAcrossMetres = 0.155f;
    const float TorsoHalfThroughMetres = 0.16f;

    // The head, as one sphere on the same axis: a skull box running 1.28 to 1.43 at 0.13 by 0.12, with the
    // jaw tapering into it from 1.20 and the crown closing it at 1.5, so a sphere on the skull's own centre
    // at the radius its furthest corner sits on swallows the lot.
    static readonly Vector3 HeadCentre = new(0f, 1.355f, 0f);
    const float HeadRadiusMetres = 0.22f;

    // How much daylight the tool has to keep off the torso and the head, metres. Small, because the number
    // this pins is a SIGN: the stroke was never tuned to a torso clearance, and what a test can protect is
    // that a later amplitude tweak does not push the pommel through the hip.
    const float BodyClearanceMetres = 0.02f;

    [Fact]
    public void TheToolCrossesTheShoulderLineAtTheImpactAndStandsOutToTheSideAtTheTopOfTheWindBack()
    {
        (Vector3 headAtImpact, Vector3 fistAtImpact) = Tool(ChopSwing.PoseAt(0f));
        (Vector3 headAtTop, Vector3 fistAtTop) = Tool(ChopSwing.PoseAt(ChopSwing.LiftPhase));

        // The body faces engine +z, so the target is at +z, and the weapon side is engine -x.
        Assert.True(headAtImpact.Z > 0f, $"the tool head is at z {headAtImpact.Z} at the impact");
        Assert.True(headAtImpact.Z > fistAtImpact.Z + 0.15f,
            $"the head leads the fist by only {headAtImpact.Z - fistAtImpact.Z} m at the impact");
        // At the target, at about the height a trunk is cut, rather than at the hip or over the head.
        Assert.True(MathF.Abs(headAtImpact.Y - TrunkHeightMetres) < 0.3f,
            $"the head lands at y {headAtImpact.Y}, which is not near the target's {TrunkHeightMetres} m");
        // And the sweep has carried it PAST the weapon shoulder's own line toward the off side, which is what
        // makes the blow read as through the target rather than at it. Past the SHOULDER, which is what the
        // 0.3 rad of yaw buys: the head stops short of the body's centre line rather than crossing it.
        Assert.True(headAtImpact.X > BodyRig.Human.RightShoulder.X + 0.1f,
            $"the head is still at x {headAtImpact.X}, on the weapon side of the shoulder, at the impact");
        Assert.True(headAtImpact.X < 0f,
            $"the head reaches x {headAtImpact.X} at the impact, which is across the body's centre line");

        // The top is OUT: a tree is felled from the side.
        Assert.True(headAtTop.X < BodyRig.Human.RightShoulder.X - 0.2f,
            $"the head is only {BodyRig.Human.RightShoulder.X - headAtTop.X} m outboard of the shoulder at the top");
        Assert.True(headAtTop.Y > 1.3f, $"the tool is cocked at only y {headAtTop.Y} at the top");
        Assert.True(headAtTop.Y > fistAtTop.Y + 0.2f,
            $"the head is only {headAtTop.Y - fistAtTop.Y} m above the fist at the top");
        // The fist is out there with it rather than the arm staying by the ribs.
        Assert.True(fistAtTop.X < BodyRig.Human.RightShoulder.X - 0.3f,
            $"the fist is at x {fistAtTop.X} at the top, which is not away from the body");
    }

    [Fact]
    public void TheChopSweepsAcrossFarMoreThanItFalls()
    {
        // You swing an axe mostly horizontally to take a tree down. So over the chop segment the head's
        // travel across the GROUND has to beat its travel up and down, and beat it by enough that nobody has
        // to squint. Two things this is careful about. Path length rather than end to end, because a stroke
        // that went up and came back down would read as zero net travel and look nothing like this. And the
        // ground track is one length, sqrt(dx^2 + dz^2) per step, rather than the two axes added: adding them
        // sums the legs of a right angle, which reports a diagonal as up to 1.4 times the distance anything
        // actually moved.
        (float horizontal, float vertical, float across, float forward) = ChopTravel();

        Assert.True(horizontal > vertical * 1.5f,
            $"the head travels {horizontal} m across the ground against {vertical} m up and down, which is "
            + "not a sweep");
        // Neither of the two horizontal axes is doing all of it on its own: a tool that only came across, or
        // only came forward, is a different wrong picture.
        Assert.True(across > 0.3f, $"the head only travels {across} m sideways through the chop");
        Assert.True(forward > 0.3f, $"the head only travels {forward} m forward through the chop");
    }

    [Fact]
    public void TheToolStaysOutsideTheArmThroughTheWholeStroke()
    {
        // With three joints on one axis the tool points wherever the arm's placement leaves it, which on a
        // wind-back is straight down the upper arm's own axis. Every sampled point of the haft and the head,
        // at every phase, has to stand off both segments by the arm's own radius PLUS the haft's own half
        // width, or the tool is drawing through the limb holding it. Both halves of that threshold are
        // needed: the tool is a stick rather than a line, so an axis-to-axis rule at the arm's radius alone
        // passes a haft with its near face already inside.
        BodyRig rig = BodyRig.Human;
        float floor = rig.UpperArmRadiusMetres + TestHeldPieces.HaftHalfWidthMetres;
        float worst = float.MaxValue;
        float worstAt = 0f;
        for (int i = 0; i < 64; i++)
        {
            float phase = i / 64f;
            float clearance = Clearance(ChopSwing.PoseAt(phase));
            if (clearance >= worst) continue;
            worst = clearance;
            worstAt = phase;
        }

        Assert.True(worst > floor,
            $"the tool comes within {worst} m of the arm's axis at phase {worstAt}, inside the arm's own "
            + $"{rig.UpperArmRadiusMetres} m plus the haft's {TestHeldPieces.HaftHalfWidthMetres} m");
    }

    [Fact]
    public void TheToolStaysOutsideTheBodyThroughTheWholeStroke()
    {
        // The arm is not the only thing in the way, and the tightest approach in the whole stroke is not the
        // arm's: at the blow the pommel swings past the hip with about 0.06 m of daylight. So the WHOLE tool,
        // the pommel included, is held off the torso and off the head at every phase. An amplitude tuned a
        // little further across the body fails here rather than putting the butt of the haft through
        // somebody's hip on their own screen.
        float worstTorso = float.MaxValue;
        float worstHead = float.MaxValue;
        float torsoAt = 0f;
        float headAt = 0f;
        for (int i = 0; i < 64; i++)
        {
            float phase = i / 64f;
            Matrix4x4 tool = Chain(ChopSwing.PoseAt(phase)).Tool;
            foreach (Vector3 point in TestHeldPieces.ToolPoints(tool, TestHeldPieces.PommelBaseMetres))
            {
                // Surface to surface both ways: the body shapes are the drawn ones and the tool carries its
                // own half width, which the head box's corners do not need and are charged anyway.
                float torso = ToTorso(point) - TestHeldPieces.HaftHalfWidthMetres;
                float head = Vector3.Distance(point, HeadCentre) - HeadRadiusMetres
                    - TestHeldPieces.HaftHalfWidthMetres;
                if (torso < worstTorso)
                {
                    worstTorso = torso;
                    torsoAt = phase;
                }

                if (head >= worstHead) continue;
                worstHead = head;
                headAt = phase;
            }
        }

        Assert.True(worstTorso > BodyClearanceMetres,
            $"the tool comes within {worstTorso} m of the torso at phase {torsoAt}");
        Assert.True(worstHead > BodyClearanceMetres,
            $"the tool comes within {worstHead} m of the head at phase {headAt}");
    }

    [Fact]
    public void TheChopIsFasterThanTheWindBack()
    {
        float lift = PeakRate(ChopSwing.HoldPhase, ChopSwing.LiftPhase);
        float chop = PeakRate(ChopSwing.LiftPhase, 1f);

        Assert.True(chop > lift * 2f, $"the chop peaks at {chop} rad per cycle against the lift's {lift}");
        // The lift owns most of the cadence, which is the other half of a heavy tool.
        Assert.True(ChopSwing.LiftPhase - ChopSwing.HoldPhase > 0.5f);
        Assert.True(1f - ChopSwing.LiftPhase < 0.25f);
    }

    [Fact]
    public void TheToolSitsStillInTheTargetThroughTheHoldAndTheHoldIsWhereTheBlowIs()
    {
        Assert.True(ChopSwing.PoseAt(0f).IsImpact);
        Assert.True(ChopSwing.PoseAt(ChopSwing.HoldPhase * 0.5f).IsImpact);
        Assert.False(ChopSwing.PoseAt(ChopSwing.HoldPhase).IsImpact);
        Assert.False(ChopSwing.PoseAt(ChopSwing.LiftPhase).IsImpact);
        Assert.False(ChopSwing.PoseAt(0.999f).IsImpact);

        // Nothing moves while it is held, on any of the four channels, so the blow has somewhere to land.
        ChopPose held = ChopSwing.PoseAt(ChopSwing.HoldPhase * 0.5f);
        Assert.Equal(ChopSwing.ImpactShoulder, held.Shoulder, 5);
        Assert.Equal(ChopSwing.ImpactElbow, held.Elbow, 5);
        Assert.Equal(ChopSwing.ImpactYaw, held.Yaw, 5);
        Assert.Equal(ChopSwing.ImpactWrist, held.Wrist, 5);
    }

    [Fact]
    public void ThePhaseWrapsOnTheSamePoseSoTheResetIsContinuous()
    {
        // Phase 0 IS the impact and phase 1 is the same blow one cadence on, so the two have to be one pose
        // or every report resetting the phase would snap the arm.
        ChopPose start = ChopSwing.PoseAt(0f);
        ChopPose end = ChopSwing.PoseAt(1f);

        Assert.Equal(start.Shoulder, end.Shoulder, 5);
        Assert.Equal(start.Elbow, end.Elbow, 5);
        Assert.Equal(start.Yaw, end.Yaw, 5);
        Assert.Equal(start.Wrist, end.Wrist, 5);
        Assert.Equal(ChopSwing.ImpactShoulder, start.Shoulder, 5);
        Assert.Equal(ChopSwing.ImpactElbow, start.Elbow, 5);

        // And nowhere in the cycle does any channel jump, the three seams and the wrap included. The
        // threshold is a thousandth of a cadence against a stroke whose fastest thousandth moves the yaw
        // 0.023 rad, so it passes a fast segment and fails a broken seam by an order of magnitude either way.
        ChopPose previous = ChopSwing.PoseAt(0f);
        for (float phase = 0.001f; phase <= 1f; phase += 0.001f)
        {
            ChopPose now = ChopSwing.PoseAt(phase);
            Assert.True(MathF.Abs(now.Shoulder - previous.Shoulder) < 0.05f,
                $"the shoulder jumps at phase {phase}");
            Assert.True(MathF.Abs(now.Elbow - previous.Elbow) < 0.05f, $"the elbow jumps at phase {phase}");
            Assert.True(MathF.Abs(now.Yaw - previous.Yaw) < 0.05f, $"the yaw jumps at phase {phase}");
            Assert.True(MathF.Abs(now.Wrist - previous.Wrist) < 0.05f, $"the wrist jumps at phase {phase}");
            previous = now;
        }
    }

    [Fact]
    public void EveryChannelSweepsOneWayThroughTheLiftAndBackTheOtherThroughTheChop()
    {
        // Four channels, ONE easing parameter, which is what stops the stroke wobbling: each of them has to
        // run one way over the lift and back the other over the chop. Asserted on the channels rather than on
        // the head's own track, because the head's path is an ARC and its sideways extreme falls a little
        // before the yaw's, so a head that turned around at phase 0.63 would be geometry rather than a broken
        // stroke. Which way each one runs is the sign of its own pair of amplitudes.
        Sweeps(ChopSwing.HoldPhase, ChopSwing.LiftPhase, toTop: true);
        Sweeps(ChopSwing.LiftPhase, 1f, toTop: false);
    }

    // Every channel monotone across a segment, in the direction the segment runs.
    static void Sweeps(float from, float to, bool toTop)
    {
        ChopPose previous = ChopSwing.PoseAt(from);
        for (float phase = from + 0.01f; phase <= to; phase += 0.01f)
        {
            ChopPose now = ChopSwing.PoseAt(phase);
            Moves(previous.Shoulder, now.Shoulder, ChopSwing.TopShoulder - ChopSwing.ImpactShoulder, toTop,
                "shoulder", phase);
            Moves(previous.Elbow, now.Elbow, ChopSwing.TopElbow - ChopSwing.ImpactElbow, toTop, "elbow", phase);
            Moves(previous.Yaw, now.Yaw, ChopSwing.TopYaw - ChopSwing.ImpactYaw, toTop, "yaw", phase);
            Moves(previous.Wrist, now.Wrist, ChopSwing.TopWrist - ChopSwing.ImpactWrist, toTop, "wrist", phase);
            previous = now;
        }
    }

    static void Moves(float before, float now, float span, bool toTop, string channel, float phase)
    {
        float step = (now - before) * (toTop ? 1f : -1f) * MathF.Sign(span);
        Assert.True(step >= -1e-4f, $"the {channel} reverses at phase {phase}");
    }

    [Fact]
    public void AnOutOfRangePhaseWraps()
    {
        Assert.Equal(ChopSwing.PoseAt(0.25f), ChopSwing.PoseAt(3.25f));
        Assert.Equal(ChopSwing.PoseAt(0.25f), ChopSwing.PoseAt(-1.75f));
        Assert.Equal(ChopSwing.PoseAt(0f), ChopSwing.PoseAt(float.NaN));
    }

    [Fact]
    public void ComposeReplacesTheWeaponArmAndLeavesTheWalkAlone()
    {
        var walk = new WalkPose(LeftArm: 0.4f, RightArm: -0.4f, LeftLeg: -0.6f, RightLeg: 0.6f,
            LeftElbow: 0.3f, RightElbow: 0.55f, LeftKnee: 0.2f, RightKnee: 0.7f, Bob: -0.02f, Lean: 0.1f);
        ChopPose swing = ChopSwing.PoseAt(ChopSwing.LiftPhase);

        WalkPose none = ChopSwing.Compose(walk, swing, 0f);
        Assert.Equal(walk, none);

        WalkPose full = ChopSwing.Compose(walk, swing, 1f);
        Assert.Equal(swing.Shoulder, full.RightArm, 5);
        Assert.Equal(swing.Elbow, full.RightElbow, 5);
        Assert.Equal(swing.Yaw, full.RightArmYaw, 5);
        Assert.Equal(swing.Wrist, full.RightWrist, 5);
        // Everything the walk owned, untouched: a chopping body still walks with its legs and its off arm.
        Assert.Equal(walk.LeftArm, full.LeftArm);
        Assert.Equal(walk.LeftElbow, full.LeftElbow);
        Assert.Equal(walk.LeftLeg, full.LeftLeg);
        Assert.Equal(walk.RightLeg, full.RightLeg);
        Assert.Equal(walk.LeftKnee, full.LeftKnee);
        Assert.Equal(walk.RightKnee, full.RightKnee);
        Assert.Equal(walk.Bob, full.Bob);
        Assert.Equal(walk.Lean, full.Lean);

        // And the blend is a real interpolation rather than a switch, which is what keeps a stop from
        // freezing an arm mid air. The two channels no other cycle writes blend up from zero, so a stop eases
        // the sweep and the wrist out with the rest of the arm rather than dropping them in one frame.
        WalkPose half = ChopSwing.Compose(walk, swing, 0.5f);
        Assert.Equal((walk.RightArm + swing.Shoulder) * 0.5f, half.RightArm, 5);
        Assert.Equal(swing.Yaw * 0.5f, half.RightArmYaw, 5);
        Assert.Equal(swing.Wrist * 0.5f, half.RightWrist, 5);
        // Out of range clamps rather than extrapolating past the stroke.
        Assert.Equal(full.RightArm, ChopSwing.Compose(walk, swing, 4f).RightArm, 5);
        Assert.Equal(walk, ChopSwing.Compose(walk, swing, -2f));
    }

    // The head's path length over the CHOP segment alone, metres: the ground track and the vertical one as
    // the two lengths that are compared, and the two horizontal axes on their own so a stroke that ran along
    // one of them can be told from a sweep.
    static (float Horizontal, float Vertical, float Across, float Forward) ChopTravel()
    {
        const int steps = 64;
        var travel = Vector3.Zero;
        float horizontal = 0f;
        Vector3 previous = Tool(ChopSwing.PoseAt(ChopSwing.LiftPhase)).Head;
        for (int i = 1; i <= steps; i++)
        {
            float phase = ChopSwing.LiftPhase + ((1f - ChopSwing.LiftPhase) * i / steps);
            Vector3 now = Tool(ChopSwing.PoseAt(phase)).Head;
            Vector3 step = now - previous;
            travel += Vector3.Abs(step);
            horizontal += MathF.Sqrt((step.X * step.X) + (step.Z * step.Z));
            previous = now;
        }
        return (horizontal, travel.Y, travel.X, travel.Z);
    }

    // The fastest any channel moves over a segment, radians per cycle.
    static float PeakRate(float from, float to)
    {
        const float step = 0.002f;
        float peak = 0f;
        for (float phase = from; phase < to - step; phase += step)
        {
            ChopPose a = ChopSwing.PoseAt(phase);
            ChopPose b = ChopSwing.PoseAt(phase + step);
            peak = MathF.Max(peak, MathF.Abs(b.Shoulder - a.Shoulder) / step);
            peak = MathF.Max(peak, MathF.Abs(b.Elbow - a.Elbow) / step);
            peak = MathF.Max(peak, MathF.Abs(b.Yaw - a.Yaw) / step);
            peak = MathF.Max(peak, MathF.Abs(b.Wrist - a.Wrist) / step);
        }
        return peak;
    }

    // How close the tool comes to the arm holding it, metres, at one pose: the smallest distance from any
    // sampled point of the tool to either arm segment's axis.
    //
    // TWO sampling starts, because the two bones are not the same question. Against the UPPER ARM, the bone
    // a haft lies along, the whole tool above the fist is measured. The FOREARM's samples start a fist's half
    // height plus an arm's radius up the haft, and that exclusion is geometry rather than laziness. The
    // forearm's axis ENDS at the top of the fist, a fist's half height above the grip, and the haft leaves
    // that same grip at an angle, so a sample the same height up the haft stands 2 * FistHalf * sin(tilt / 2)
    // from that end and no further: 0.055 m at the tightest phase of this stroke, and under 0.09 m for any
    // tilt at all, against a floor of 0.094 m. Measured down there the rule would fail every stroke that
    // could ever be written rather than the ones that clip. What it asks is whether the tool crosses a BONE,
    // and next to the hand the answer is that the hand is holding it.
    static float Clearance(in ChopPose pose)
    {
        BodyRig rig = BodyRig.Human;
        (Matrix4x4 upper, Matrix4x4 forearm, Matrix4x4 tool) = Chain(pose);
        Vector3 shoulder = rig.RightShoulder;
        Vector3 elbow = Vector3.Transform(rig.ElbowFromShoulder, upper);
        // The forearm PIECE ends at the wrist, the fist's own half height above the grip, so that is where
        // its axis ends too.
        Vector3 wrist = Vector3.Transform(
            rig.HandFromElbow + new Vector3(0f, TestHeldPieces.FistHalfMetres, 0f), forearm);

        float worst = float.MaxValue;
        // The upper arm sees the whole tool above the fist.
        foreach (Vector3 point in TestHeldPieces.ToolPoints(tool, TestHeldPieces.FistHalfMetres))
            worst = MathF.Min(worst, TestHeldPieces.ToSegment(point, shoulder, elbow));
        // The forearm sees it from the point the haft is clear of the hand, which is where its own axis ends.
        foreach (Vector3 point in TestHeldPieces.ToolPoints(
            tool, TestHeldPieces.FistHalfMetres + rig.UpperArmRadiusMetres))
            worst = MathF.Min(worst, TestHeldPieces.ToSegment(point, elbow, wrist));
        return worst;
    }

    // The distance from a point to the torso box, metres. Zero inside it, which a clearance floor above zero
    // reads as the failure it is.
    static float ToTorso(Vector3 point)
    {
        float x = MathF.Max(MathF.Abs(point.X) - TorsoHalfAcrossMetres, 0f);
        float z = MathF.Max(MathF.Abs(point.Z) - TorsoHalfThroughMetres, 0f);
        float y = MathF.Max(MathF.Max(TorsoBottomMetres - point.Y, point.Y - TorsoTopMetres), 0f);
        return MathF.Sqrt((x * x) + (y * y) + (z * z));
    }

    // The three transforms of the weapon arm at a pose, in BODY space, through the chain a draw composes: the
    // shoulder's pitch and yaw out to the shoulder joint, the elbow out to the elbow joint, and then the
    // wrist and the grip's tilt out to the hand.
    static (Matrix4x4 Upper, Matrix4x4 Forearm, Matrix4x4 Tool) Chain(in ChopPose pose)
    {
        BodyRig rig = BodyRig.Human;
        (Matrix4x4 upper, Matrix4x4 forearm) =
            TestHeldPieces.WeaponArm(rig, pose.Shoulder, pose.Elbow, pose.Yaw);
        Matrix4x4 tool = SegmentSockets.Held(rig, TestHeldPieces.ToolGrip, forearm, tip: pose.Wrist);
        return (upper, forearm, tool);
    }

    // The tool's head and the fist that holds it, in BODY space.
    static (Vector3 Head, Vector3 Fist) Tool(in ChopPose pose)
    {
        Matrix4x4 tool = Chain(pose).Tool;
        return (Vector3.Transform(TestHeldPieces.ToolHead, tool), Vector3.Transform(Vector3.Zero, tool));
    }
}
