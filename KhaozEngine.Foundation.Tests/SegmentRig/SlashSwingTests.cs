using System;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>The armed fighting stroke: that the blade is CARRIED at the side between blows, that the strike
/// itself throws the arm out to the weapon side and then cuts forward and ACROSS into the blow, that the cut
/// is the fastest part of it, and that it touches the weapon arm and nothing else.</summary>
/// <remarks>
/// <see cref="AttackSwingTests"/>'s twin over the other style, and it measures the BLADE'S POINT rather than
/// the fist for <see cref="ChopSwingTests"/>'s reason: the whole difference between a cut and a jab is what
/// the thing in the hand does, and three of the four channels rotate it. The grip it rides is
/// <see cref="TestHeldPieces.BladeGrip"/>, which is a test's own content until the package owns socket
/// helpers.
/// </remarks>
public class SlashSwingTests
{
    // The blade's point in the piece's own frame, 0.625 m up its own length.
    static readonly Vector3 Point = TestHeldPieces.BladePoint;

    [Fact]
    public void TheTwoEndsOfTheStrokeAreTheSamePoseAndBothAreTheBlow()
    {
        Assert.Equal(SlashSwing.PoseAt(0f), SlashSwing.PoseAt(1f));
        Assert.Equal(SlashSwing.Impact, SlashSwing.PoseAt(0f));
        Assert.True(SlashSwing.PoseAt(0f).IsImpact);
        Assert.True(SlashSwing.PoseAt(AttackSwing.ImpactPhase * 0.5f).IsImpact);
        Assert.False(SlashSwing.PoseAt(AttackSwing.ImpactPhase).IsImpact);
        Assert.False(SlashSwing.PoseAt(AttackSwing.StrikePhase).IsImpact);
        Assert.Equal(SlashSwing.PoseAt(0.5f), SlashSwing.PoseAt(2.5f));
        Assert.Equal(SlashSwing.PoseAt(0f), SlashSwing.PoseAt(float.NaN));
    }

    [Fact]
    public void TheBladeIsCARRIEDAtTheSideBetweenBlows()
    {
        // The rest pose is HELD, exactly as the punch's guard is: the middle of the cadence is a body waiting
        // rather than a body winding up.
        Assert.Equal(SlashSwing.Rest, SlashSwing.PoseAt(0.3f));
        Assert.Equal(SlashSwing.Rest, SlashSwing.PoseAt(0.7f));
        Assert.Equal(SlashSwing.PoseAt(0.3f), SlashSwing.PoseAt(0.7f));
        Assert.Equal(SlashSwing.Rest, SlashSwing.PoseAt(0.85f));

        // And what is held is the WALK'S OWN IDLE ARM with something in the fist: the arm hangs, the yaw is
        // barely off the hip, and the wrist is zero so the blade sits at exactly the lean the resting grip
        // gives it. An arm out at the weapon side here is a blade splayed horizontally for the whole fight,
        // which is what this shape exists to stop.
        Assert.True(SlashSwing.RestShoulder < 0.2f,
            $"the arm rests at a shoulder of {SlashSwing.RestShoulder}, raised rather than hanging");
        Assert.True(SlashSwing.RestYaw < 0.3f,
            $"the arm rests at a yaw of {SlashSwing.RestYaw}, splayed out to the weapon side");
        Assert.Equal(0f, SlashSwing.RestWrist);
        Assert.InRange(SlashSwing.RestElbow, IdleBreath.RestElbowRadians, IdleBreath.RestElbowRadians * 2f);

        // The point is in beside the body rather than out past it: within a hand's width of the shoulder it
        // hangs off, at engine -x, where the carry leaves it.
        Vector3 point = At(SlashSwing.Rest, Point);
        Assert.True(MathF.Abs(point.X - BodyRig.Human.RightShoulder.X) < 0.2f,
            $"the point rests at x {point.X}, out from a shoulder at {BodyRig.Human.RightShoulder.X}");
        // Up and forward out of the fist, which is the resting lean and not a pose being held against it.
        Vector3 fist = At(SlashSwing.Rest, Vector3.Zero);
        Assert.True(point.Y > fist.Y + 0.4f, $"the point rests at y {point.Y}, below a fist at {fist.Y}");
        Assert.True(point.Z > fist.Z, $"the point rests at z {point.Z}, behind a fist at {fist.Z}");
    }

    [Fact]
    public void TheArmGoesOutToTheWeaponSideINSIDETheStrike()
    {
        // The raise is a key of the STRIKE, not a pose the cadence sits in: it is reached inside the last
        // eighth, so the whole out and across happens in the third of a second before the blow. Nothing at all
        // has moved a hundredth of a cadence before the strike opens.
        Assert.Equal(SlashSwing.Rest, SlashSwing.PoseAt(AttackSwing.StrikePhase - 0.01f));
        Assert.True(RaisePhase > AttackSwing.StrikePhase, "the raise is reached before the strike opens");
        Assert.True(RaisePhase < 1f, "the raise is not reached before the blow");
        Assert.Equal(SlashSwing.Raise, SlashSwing.PoseAt(RaisePhase));

        // OUT to the weapon side, which is the positive sense of the yaw, and the point is really out there:
        // 0.6 m past the shoulder it hangs off, at engine -x.
        Assert.True(SlashSwing.RaiseYaw > 0.7f,
            $"the arm raises to a yaw of {SlashSwing.RaiseYaw}, in beside the body rather than out from it");
        Assert.True(SlashSwing.RaiseYaw > SlashSwing.RestYaw, "the raise is no further out than the carry");
        Vector3 point = At(SlashSwing.Raise, Point);
        Assert.True(point.X < BodyRig.Human.RightShoulder.X - 0.5f,
            $"the point raises to x {point.X}, in beside a shoulder at {BodyRig.Human.RightShoulder.X}");

        // And the raise is the FAR END of the arc: nothing anywhere in the stroke is further out than it, so
        // the arm goes out once and then crosses.
        for (int i = 0; i <= 400; i++)
            Assert.True(SlashSwing.PoseAt(i / 400f).Yaw <= SlashSwing.RaiseYaw + 1e-4f,
                $"the arm is at yaw {SlashSwing.PoseAt(i / 400f).Yaw} at phase {i / 400f}, past the raise");
    }

    [Fact]
    public void TheCutCrossesTheBodyAndTheBladeLeadsTheFist()
    {
        Vector3 raise = At(SlashSwing.Raise, Point);
        Vector3 impact = At(SlashSwing.Impact, Point);

        // ACROSS. The point covers most of a metre from the weapon side to the far side of the centre line,
        // which is the whole reason an armed body does not throw the punch: a blade that arrived on the same
        // line it left on would read as a thrust with a blade-shaped stick.
        Assert.True(impact.X > raise.X + 0.5f,
            $"the point only travels {impact.X - raise.X} m across the body into the blow");
        Assert.True(SlashSwing.ImpactYaw < 0f,
            "the arm ends the cut out on the weapon side, so nothing crossed the centre line");
        Assert.True(SlashSwing.ImpactYaw < SlashSwing.RaiseYaw,
            "the arm ends the cut further out than it raised to, so nothing crossed");
        // And FORWARD, into whatever is being hit rather than past it.
        Assert.True(impact.Z > raise.Z + 0.2f,
            $"the point only travels {impact.Z - raise.Z} m forward into the blow");
        // The BLADE LEADS: at the blow the point is out well past the fist that holds it, so the thing that
        // arrives at the target is the blade.
        Vector3 fist = At(SlashSwing.Impact, Vector3.Zero);
        Assert.True(impact.Z > fist.Z + 0.4f,
            $"the point is at z {impact.Z} against a fist at {fist.Z}, so the blade is trailing the hand");

        // The blade never crosses the body on the way round. It is a metre of stone travelling at chest
        // height, and the torso's front face is at 0.16 on this rig.
        for (int i = 0; i <= 400; i++)
        {
            Vector3 at = At(SlashSwing.PoseAt(i / 400f), Point);
            Assert.True(at.Z > 0.2f, $"the point is at z {at.Z} at phase {i / 400f}, back inside the body");
        }
    }

    [Fact]
    public void TheStrikeIsTheLastFractionOfTheCadenceAndArrivesFastest()
    {
        // Still at rest at 0.85 and still short of the raise at 0.9: the whole stroke happens inside the last
        // third of a second, and it spends the first slice of that going out rather than cutting.
        Assert.Equal(SlashSwing.Rest, SlashSwing.PoseAt(0.85f));
        Assert.InRange(SlashSwing.PoseAt(0.9f).Yaw, SlashSwing.RestYaw, SlashSwing.RaiseYaw);

        // PEAK rates rather than mean ones, AttackSwingTests' reason: the segments cover their ground over
        // spans of nearly the same length, and the easings are what separate them at the arrival. The CUT is
        // the fastest thing in the stroke, ahead of both the raise that sets it up and the recovery off the
        // last blow, which is what makes the blow read as a blow.
        float recovery = PeakSpeed(0f, AttackSwing.RestPhase);
        float raise = PeakSpeed(AttackSwing.StrikePhase, RaisePhase);
        float cut = PeakSpeed(RaisePhase, 1f);
        Assert.True(cut > raise * 1.5f,
            $"the cut peaks at {cut} m per unit phase against the raise's {raise}, so the arm is not cutting");
        Assert.True(cut > recovery * 1.4f,
            $"the cut peaks at {cut} m per unit phase against the recovery's {recovery}, which is not a blow");

        // And it is fastest ON ARRIVAL, which is the ease-in: the last quarter of the cut covers far more
        // ground than the first quarter of it.
        float span = 1f - RaisePhase;
        float opening = Travelled(RaisePhase, RaisePhase + (span * 0.25f));
        float arrival = Travelled(1f - (span * 0.25f), 1f);
        Assert.True(arrival > opening * 2f,
            $"the cut covers {opening} m opening and {arrival} m arriving, so it is not eased in");
    }

    [Fact]
    public void TheStrokeTouchesTheWeaponArmAndNothingElse()
    {
        var walk = new WalkPose(
            LeftArm: 0.4f, RightArm: -0.4f, LeftLeg: 0.3f, RightLeg: -0.3f,
            LeftElbow: 0.2f, RightElbow: 0.1f, LeftKnee: 0.15f, RightKnee: 0.05f,
            Bob: 0.02f, Lean: 0.03f, RightArmYaw: 0f, RightWrist: 0f,
            TorsoRise: 0.01f, TorsoLean: 0.04f);
        AttackPose swing = SlashSwing.PoseAt(0f);
        WalkPose composed = SlashSwing.Compose(walk, swing, 1f);

        Assert.Equal(swing.Shoulder, composed.RightArm, 5);
        Assert.Equal(swing.Elbow, composed.RightElbow, 5);
        Assert.Equal(swing.Yaw, composed.RightArmYaw, 5);
        Assert.Equal(swing.Wrist, composed.RightWrist, 5);
        Assert.Equal(walk.LeftArm, composed.LeftArm);
        Assert.Equal(walk.LeftElbow, composed.LeftElbow);
        Assert.Equal(walk.LeftLeg, composed.LeftLeg);
        Assert.Equal(walk.RightLeg, composed.RightLeg);
        Assert.Equal(walk.LeftKnee, composed.LeftKnee);
        Assert.Equal(walk.RightKnee, composed.RightKnee);
        Assert.Equal(walk.Bob, composed.Bob);
        Assert.Equal(walk.Lean, composed.Lean);
        Assert.Equal(walk.TorsoRise, composed.TorsoRise);
        Assert.Equal(walk.TorsoLean, composed.TorsoLean);

        Assert.Equal(walk, SlashSwing.Compose(walk, swing, 0f));
        Assert.Equal(SlashSwing.Compose(walk, swing, 1f), SlashSwing.Compose(walk, swing, 2f));
    }

    [Fact]
    public void TheTwoStylesShareOneClock()
    {
        // The shape is about the timing as much as the pose: both strokes rest and both strike late, so a
        // fight reads the same whatever is in the hand. One phase machine drives them, and this is what says
        // so from the outside.
        for (int i = 0; i <= 100; i++)
        {
            float phase = i / 100f;
            Assert.Equal(AttackSwing.PoseAt(phase).IsImpact, SlashSwing.PoseAt(phase).IsImpact);
            // Up to the strike the two are the SAME curve on one clock. Inside the strike the cut passes
            // through its raise and the punch does not, which is the one place the styles are allowed to
            // differ in more than where they end up.
            if (phase > AttackSwing.StrikePhase) continue;
            float punch = (AttackSwing.PoseAt(phase).Shoulder - AttackSwing.GuardShoulder)
                / (AttackSwing.ImpactShoulder - AttackSwing.GuardShoulder);
            float slash = (SlashSwing.PoseAt(phase).Shoulder - SlashSwing.RestShoulder)
                / (SlashSwing.ImpactShoulder - SlashSwing.RestShoulder);
            Assert.Equal(punch, slash, 5);
        }
    }

    // Where in the cadence the raise is reached, which is the split the trajectory takes as a fraction of
    // the strike alone.
    static float RaisePhase =>
        AttackSwing.StrikePhase + ((1f - AttackSwing.StrikePhase) * SlashSwing.RaiseFraction);

    // How far the point travels over one segment, metres, sampled.
    static float Travelled(float from, float to)
    {
        const int steps = 64;
        float step = (to - from) / steps;
        float total = 0f;
        Vector3 previous = At(SlashSwing.PoseAt(from), Point);
        for (int i = 1; i <= steps; i++)
        {
            Vector3 now = At(SlashSwing.PoseAt(from + (step * i)), Point);
            total += Vector3.Distance(previous, now);
            previous = now;
        }
        return total;
    }

    // The fastest the point moves anywhere in one segment, metres per unit of phase, sampled.
    static float PeakSpeed(float from, float to)
    {
        const int steps = 128;
        float step = (to - from) / steps;
        float fastest = 0f;
        Vector3 previous = At(SlashSwing.PoseAt(from), Point);
        for (int i = 1; i <= steps; i++)
        {
            Vector3 now = At(SlashSwing.PoseAt(from + (step * i)), Point);
            fastest = MathF.Max(fastest, Vector3.Distance(previous, now) / step);
            previous = now;
        }
        return fastest;
    }

    // A point of the BLADE at a pose, in BODY space, through the chain a draw composes: the piece's own grip,
    // then the wrist, then the hand socket down the forearm, then the elbow and the shoulder's two axes. A
    // local of zero is the fist itself.
    static Vector3 At(in AttackPose pose, Vector3 local) => Vector3.Transform(local,
        TestHeldPieces.HeldInWeaponHand(BodyRig.Human, TestHeldPieces.BladeGrip,
            pose.Shoulder, pose.Elbow, pose.Yaw, pose.Wrist));
}
