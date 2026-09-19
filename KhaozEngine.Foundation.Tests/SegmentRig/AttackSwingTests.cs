using System;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>The bare-handed fighting stroke: where the FIST goes, that the arm rests between blows, that the
/// strike is the last third of a second and the fast part, and that it touches the weapon arm and nothing
/// else.</summary>
/// <remarks>
/// <see cref="ChopSwingTests"/>'s shape, one joint shorter. That file measures the TOOL HEAD because a chop is
/// about where the thing in the hand ends up and three of the four channels rotate it. This one measures the
/// FIST, because a punch is about where the hand arrives and there is nothing in it: this is the stroke a body
/// with an EMPTY hand throws. <see cref="SlashSwingTests"/> is the armed half.
/// <para>THE CADENCE IS A CALLER'S NUMBER. The package has no cadence of its own and no tick count anywhere,
/// so anything measured in seconds here is measured against <see cref="CadenceSeconds"/> below, which is this
/// file's own.</para>
/// </remarks>
public class AttackSwingTests
{
    /// <summary>A cadence to measure seconds against, and a plain number rather than a derived one. A tile
    /// game on a 6 Hz tick swinging every sixteen ticks lands here (16 / 6), which is where this value comes
    /// from, but the package takes seconds and knows nothing about either half of that.</summary>
    const float CadenceSeconds = 16f / 6f;

    [Fact]
    public void TheTwoEndsOfTheStrokeAreTheSamePoseAndBothAreTheBlow()
    {
        // What lets a swing event reset the phase without a snap: the authority seeds phase 0 on the blow, and
        // a stroke that had already run round to 1 is at the same pose it is being seeded to.
        Assert.Equal(AttackSwing.PoseAt(0f), AttackSwing.PoseAt(1f));
        Assert.True(AttackSwing.PoseAt(0f).IsImpact);
        Assert.True(AttackSwing.PoseAt(AttackSwing.ImpactPhase * 0.5f).IsImpact);
        Assert.False(AttackSwing.PoseAt(AttackSwing.ImpactPhase).IsImpact);
        Assert.False(AttackSwing.PoseAt(AttackSwing.RestPhase).IsImpact);
        Assert.False(AttackSwing.PoseAt(AttackSwing.StrikePhase).IsImpact);
        // The blow is where the pose says it is, on all four channels.
        Assert.Equal(AttackSwing.Impact, AttackSwing.PoseAt(0f));
        // A raw accumulator is legal, so a caller never has to wrap one itself.
        Assert.Equal(AttackSwing.PoseAt(0.5f), AttackSwing.PoseAt(2.5f));
        Assert.Equal(AttackSwing.PoseAt(0.75f), AttackSwing.PoseAt(-0.25f));
        Assert.Equal(AttackSwing.PoseAt(0f), AttackSwing.PoseAt(float.NaN));
    }

    [Fact]
    public void TheArmRESTSOnGuardThroughTheMiddleOfEveryCadence()
    {
        // The whole difference from a wound-back stroke: no wind-up. Between the recovery and the strike
        // NOTHING moves, so nothing tells the eye a blow is coming.
        Assert.Equal(AttackSwing.Guard, AttackSwing.PoseAt(0.3f));
        Assert.Equal(AttackSwing.Guard, AttackSwing.PoseAt(0.7f));
        Assert.Equal(AttackSwing.PoseAt(0.3f), AttackSwing.PoseAt(0.7f));
        // Right up to the strike, which is the part a wind-up would eat into.
        Assert.Equal(AttackSwing.Guard.Shoulder, AttackSwing.PoseAt(0.85f).Shoulder, 4);
        Assert.Equal(AttackSwing.Guard.Elbow, AttackSwing.PoseAt(0.85f).Elbow, 4);
        Assert.Equal(AttackSwing.Guard.Yaw, AttackSwing.PoseAt(0.85f).Yaw, 4);
        Assert.Equal(AttackSwing.Guard.Wrist, AttackSwing.PoseAt(0.85f).Wrist, 4);

        // The guard itself: a fist up at the ribs on a folded elbow, held a little out from the body.
        Assert.True(AttackSwing.GuardElbow > 1.5f, "the guard's elbow is barely folded, so it is not a guard");
        Assert.True(AttackSwing.GuardYaw > 0f, "the guard tucks the elbow into the ribs rather than clearing them");
        Vector3 fist = Fist(AttackSwing.Guard);
        Assert.InRange(fist.Y, 0.85f, 1.0f);        // ribs on a 1.5 m body, not the hip and not the chin
        Assert.True(fist.Z > 0f, $"the guard hand is at z {fist.Z}, behind the body rather than in front of it");
    }

    [Fact]
    public void TheStrikeIsTheLastFractionOfTheCadenceAndArrivesAtTheBlow()
    {
        // A third of a second at a typical cadence, which is the number the shape was chosen in.
        float strikeSeconds = (1f - AttackSwing.StrikePhase) * CadenceSeconds;
        Assert.InRange(strikeSeconds, 0.25f, 0.4f);

        // Under way but barely at 0.97, most of the way home at 0.99: an ease-in spends its first half moving
        // hardly at all, which is what keeps the wind-up out of the picture.
        float span = AttackSwing.ImpactShoulder - AttackSwing.GuardShoulder;
        float atLate = (AttackSwing.PoseAt(0.97f).Shoulder - AttackSwing.GuardShoulder) / span;
        Assert.InRange(atLate, 0.3f, 0.6f);
        Assert.True((AttackSwing.PoseAt(0.99f).Shoulder - AttackSwing.GuardShoulder) / span > 0.7f,
            "the strike is still most of a swing from the blow a hundredth of a cadence before it lands");
        Assert.Equal(AttackSwing.ImpactShoulder, AttackSwing.PoseAt(1f).Shoulder, 4);
    }

    [Fact]
    public void TheFistIsThrownFORWARDAndIsFurthestOutAtTheBlow()
    {
        Vector3 atImpact = Fist(AttackSwing.PoseAt(0f));
        Vector3 onGuard = Fist(AttackSwing.PoseAt(0.5f));

        // The body faces engine +z, so a punch is thrown at +z. This is the whole difference from the chop,
        // which goes out to the weapon side at -x and comes back across.
        Assert.True(atImpact.Z > onGuard.Z + 0.25f,
            $"the fist only travels {atImpact.Z - onGuard.Z} m forward between the guard and the blow");
        Assert.True(atImpact.Y > onGuard.Y + 0.15f,
            $"the fist only rises {atImpact.Y - onGuard.Y} m into the blow, so it reads as a shrug");
        // It comes IN a little as it goes out, because the thing being hit is in front of the body rather
        // than off the weapon shoulder. Still on the weapon side of the centre line: a fist that crossed it
        // would be punching past whatever it was aimed at.
        Assert.True(atImpact.X > onGuard.X, $"the fist stays out at x {atImpact.X} through the blow");
        Assert.True(atImpact.X < 0f, $"the fist crosses the body's centre line to x {atImpact.X}");
        Assert.True(AttackSwing.ImpactYaw < 0f, "the blow is thrown out to the weapon side rather than in");

        // And nowhere in the stroke does it get further out than the blow does, which is what makes the blow
        // the moment the eye reads: a stroke whose furthest reach was somewhere in the middle would land its
        // damage on the way back in.
        for (int i = 0; i <= 200; i++)
        {
            float phase = i / 200f;
            Assert.True(Fist(AttackSwing.PoseAt(phase)).Z <= atImpact.Z + 0.001f,
                $"the fist reaches further at phase {phase} than it does at the blow");
        }
    }

    [Fact]
    public void TheBlowArrivesFasterThanTheArmRecovered()
    {
        // PEAK rates rather than mean ones, and the difference matters here: the recovery and the strike
        // cover the same ground over spans of nearly the same length, so their averages say almost nothing.
        // What the eye reads is the arrival, and the easings are what separate them there. The recovery is
        // smoothed at both ends (peak half again its own mean) and the strike is eased IN and nothing else
        // (peak three times its mean), so the fist arrives well over half again as fast as it left.
        float recovery = PeakSpeed(0f, AttackSwing.RestPhase);
        float strike = PeakSpeed(AttackSwing.StrikePhase, 1f);
        Assert.True(strike > recovery * 1.4f,
            $"the strike peaks at {strike} m per unit phase against the recovery's {recovery}, which is not a blow");
    }

    [Fact]
    public void TheStrokeTouchesTheWeaponArmAndNothingElse()
    {
        // A walk pose with something on every channel, so anything the compose overwrites shows up.
        var walk = new WalkPose(
            LeftArm: 0.4f, RightArm: -0.4f, LeftLeg: 0.3f, RightLeg: -0.3f,
            LeftElbow: 0.2f, RightElbow: 0.1f, LeftKnee: 0.15f, RightKnee: 0.05f,
            Bob: 0.02f, Lean: 0.03f, RightArmYaw: 0f, RightWrist: 0f,
            TorsoRise: 0.01f, TorsoLean: 0.04f);
        AttackPose swing = AttackSwing.PoseAt(0f);
        WalkPose composed = AttackSwing.Compose(walk, swing, 1f);

        Assert.Equal(swing.Shoulder, composed.RightArm, 5);
        Assert.Equal(swing.Elbow, composed.RightElbow, 5);
        Assert.Equal(swing.Yaw, composed.RightArmYaw, 5);
        Assert.Equal(swing.Wrist, composed.RightWrist, 5);
        // Everything else is exactly what the walk drew, the breath's two torso channels included.
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

        // A blend of zero is the walk untouched, which is what an eased-out stroke costs a standing body.
        Assert.Equal(walk, AttackSwing.Compose(walk, swing, 0f));
        Assert.Equal(walk, AttackSwing.Compose(walk, swing, -1f));
        Assert.Equal(AttackSwing.Compose(walk, swing, 1f), AttackSwing.Compose(walk, swing, 2f));
    }

    [Fact]
    public void OneSwingKeepsTheArmGoingJustPastTheCadenceItArrivesOn()
    {
        // The seam a whole attack path hangs off: a fight is knowable only from the blows that land in it,
        // so the arm has to outlive one cadence or a steady fight would flicker between strokes. It is also
        // what makes the next strike PREDICTABLE: the arm is still running when the cadence comes round, so
        // it can throw the blow the authority is about to resolve.
        float hold = AttackSwing.HoldSecondsFor(CadenceSeconds);
        Assert.True(hold > CadenceSeconds,
            "a swing does not carry the arm as far as the next one, so a steady fight would stutter");
        Assert.True(hold < CadenceSeconds * 1.5f,
            "a swing carries the arm most of a second stroke, so a fight that ended keeps swinging at nothing");
    }

    /// <summary>
    /// THE CADENCE IS A PARAMETER, which is what keeps the package free of any update rate at all. Both
    /// seconds-taking members scale linearly in whatever the caller hands over, so a game on a quarter-second
    /// tile tick and one running every frame get the same shape at their own rates.
    /// </summary>
    [Fact]
    public void BothSecondsMembersScaleInWhateverCadenceTheCallerHandsOver()
    {
        foreach (float cadence in new[] { 0.6f, 1f, CadenceSeconds, 19f / 6f, 5f })
        {
            // The recovery off a killing blow is the FIRST slice of the hold and nothing after it, so a body
            // that stopped fighting comes down off the blow instead of standing on guard.
            Assert.True(AttackSwing.RecoverySecondsFor(cadence) < AttackSwing.HoldSecondsFor(cadence));
        }

        // Exactly proportional, which is the whole claim: twice the cadence is twice the hold.
        Assert.Equal(2f * AttackSwing.HoldSecondsFor(1f), AttackSwing.HoldSecondsFor(2f), 5);
        Assert.Equal(2f * AttackSwing.RecoverySecondsFor(1f), AttackSwing.RecoverySecondsFor(2f), 5);
        Assert.Equal(0f, AttackSwing.HoldSecondsFor(0f));
    }

    // The fastest the fist moves anywhere in one segment, metres per unit of phase, sampled. See the caller
    // for why the peak rather than the mean.
    static float PeakSpeed(float from, float to)
    {
        const int steps = 128;
        float step = (to - from) / steps;
        float fastest = 0f;
        Vector3 previous = Fist(AttackSwing.PoseAt(from));
        for (int i = 1; i <= steps; i++)
        {
            Vector3 now = Fist(AttackSwing.PoseAt(from + (step * i)));
            fastest = MathF.Max(fastest, Vector3.Distance(previous, now) / step);
            previous = now;
        }
        return fastest;
    }

    // The weapon fist at a pose, in BODY space, through the chain a draw composes: the shoulder's pitch and
    // yaw out to the shoulder joint, then the elbow out to the elbow joint, then the hand socket.
    static Vector3 Fist(in AttackPose pose)
    {
        BodyRig rig = BodyRig.Human;
        Matrix4x4 forearm = TestHeldPieces.WeaponArm(rig, pose.Shoulder, pose.Elbow, pose.Yaw).Forearm;
        return Vector3.Transform(rig.HandFromElbow, forearm);
    }
}
