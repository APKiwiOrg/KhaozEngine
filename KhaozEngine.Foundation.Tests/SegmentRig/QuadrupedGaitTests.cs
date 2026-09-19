using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>
/// The four-legged walk, which is a pure function of the shared cycle's phase, pinned without a scene. What
/// matters is that it is the gait a grazing animal actually has: four separate footfalls a quarter apart in
/// the lateral sequence, a hoof that stays put on the ground while the body passes over it, a carpus that
/// folds back and a hock that folds forward, and a stop that settles onto four straight legs.
/// </summary>
public class QuadrupedGaitTests
{
    static readonly QuadrupedRig Rig = TestBodies.Grazer;

    static readonly QuadrupedGait.Leg[] Legs =
    [
        QuadrupedGait.Leg.LeftFore, QuadrupedGait.Leg.RightFore,
        QuadrupedGait.Leg.LeftHind, QuadrupedGait.Leg.RightHind,
    ];

    static float Swing(in QuadrupedPose pose, QuadrupedGait.Leg leg) => leg switch
    {
        QuadrupedGait.Leg.LeftFore => pose.LeftForeSwing,
        QuadrupedGait.Leg.RightFore => pose.RightForeSwing,
        QuadrupedGait.Leg.LeftHind => pose.LeftHindSwing,
        _ => pose.RightHindSwing,
    };

    static float Flex(in QuadrupedPose pose, QuadrupedGait.Leg leg) => leg switch
    {
        QuadrupedGait.Leg.LeftFore => pose.LeftForeFlex,
        QuadrupedGait.Leg.RightFore => pose.RightForeFlex,
        QuadrupedGait.Leg.LeftHind => pose.LeftHindFlex,
        _ => pose.RightHindFlex,
    };

    /// <summary>Where one hoof's sole is in the BODY's frame at a pose, through the real chain.</summary>
    static Vector3 Sole(in QuadrupedPose pose, QuadrupedGait.Leg leg) =>
        TestBodies.Hoof(new BodyPose(Vector3.Zero, 0f), pose, leg);

    [Fact]
    public void AStandingBodyAnswersEveryChannelZero()
    {
        Assert.Equal(QuadrupedPose.Rest, QuadrupedGait.PoseAt(Rig, 0.37f, 0f));
        Assert.Equal(QuadrupedPose.Rest, QuadrupedGait.PoseAt(Rig, GaitSample.Still));
        // And a standing body's hooves are on the floor, all four, through the real chain: the pieces stand
        // each hoof a centimetre up, and the rig reproduces exactly that.
        foreach (QuadrupedGait.Leg leg in Legs)
            Assert.Equal(TestBodies.AuthoredFloor, Sole(QuadrupedPose.Rest, leg).Y, 3);
    }

    [Fact]
    public void TheFeetLandOneAtATimeInTheLateralSequence()
    {
        // Walk the phase round once and note the moment each hoof lands: the sample where its stance begins.
        const int samples = 400;
        var landings = new Dictionary<QuadrupedGait.Leg, float>();
        foreach (QuadrupedGait.Leg leg in Legs)
        {
            bool wasStance = QuadrupedGait.IsStance(QuadrupedGait.LocalPhase((samples - 1f) / samples, leg));
            for (int i = 0; i < samples; i++)
            {
                float phase = (float)i / samples;
                bool stance = QuadrupedGait.IsStance(QuadrupedGait.LocalPhase(phase, leg));
                if (stance && !wasStance) landings[leg] = phase;
                wasStance = stance;
            }
        }
        Assert.Equal(4, landings.Count);

        // Left hind, then left fore, then right hind, then right fore, a quarter of a cycle apart each: the
        // lateral sequence every walking quadruped uses, hind foot first and the same side's fore after it.
        QuadrupedGait.Leg[] order = landings.OrderBy(entry => entry.Value).Select(entry => entry.Key).ToArray();
        Assert.Equal(
            [QuadrupedGait.Leg.LeftHind, QuadrupedGait.Leg.LeftFore, QuadrupedGait.Leg.RightHind,
                QuadrupedGait.Leg.RightFore],
            order);
        for (int i = 1; i < order.Length; i++)
            Assert.Equal(0.25f, landings[order[i]] - landings[order[i - 1]], 2);

        // Each hoof is down for the duty factor of the cycle, and at least two are down at every moment.
        for (int i = 0; i < samples; i++)
        {
            float phase = (float)i / samples;
            int down = Legs.Count(leg => QuadrupedGait.IsStance(QuadrupedGait.LocalPhase(phase, leg)));
            Assert.InRange(down, 2, 3);
        }
        foreach (QuadrupedGait.Leg leg in Legs)
        {
            int down = Enumerable.Range(0, samples)
                .Count(i => QuadrupedGait.IsStance(QuadrupedGait.LocalPhase((float)i / samples, leg)));
            Assert.Equal(QuadrupedGait.DutyFactor, (float)down / samples, 2);
        }
    }

    /// <summary>
    /// A HOOF ON THE GROUND DOES NOT MOVE. The body walks over it, so in the body's frame the sole travels
    /// backward at exactly the ground speed, which is the stride per cycle: one full cycle of phase is one
    /// stride of ground. Sampled through the real chain rather than off the angle, because the angle is an
    /// arcsine precisely so that the POSITION is linear.
    /// </summary>
    [Fact]
    public void AStandingHoofTravelsBackwardAtTheGroundSpeedAndNeverForward()
    {
        const int samples = 1000;
        const float step = 1f / samples;
        foreach (QuadrupedGait.Leg leg in Legs)
        {
            int checkedSteps = 0;
            for (int i = 0; i < samples; i++)
            {
                float phase = i * step;
                float local = QuadrupedGait.LocalPhase(phase, leg);
                float localNext = QuadrupedGait.LocalPhase(phase + step, leg);
                // Only strictly inside the stance, clear of both edges, where the hoof is planted.
                if (local < 0.02f || localNext > QuadrupedGait.DutyFactor - 0.02f || localNext < local) continue;
                Vector3 before = Sole(QuadrupedGait.PoseAt(Rig, phase, 1f), leg);
                Vector3 after = Sole(QuadrupedGait.PoseAt(Rig, phase + step, 1f), leg);
                float travelled = (after.Z - before.Z) / step;
                // Backward (negative z) at the stride per cycle, within a few percent: the hinge hangs a
                // little fore or aft of its pivot, which bends the arc a hair off the pure arcsine.
                Assert.InRange(travelled, -Rig.StrideMetres * 1.08f, -Rig.StrideMetres * 0.92f);
                // And it stays on the floor, give or take the rigid-leg trade: some float at the ends of the
                // stance and some sink in the middle. The hind leg swings furthest from the lowest pivot, so
                // its hoof floats the most, a hand and a half at the very end of its stance. A fetlock piece
                // is what would buy that back, not a smaller number here.
                Assert.InRange(before.Y, -0.06f, 0.18f);
                checkedSteps++;
            }
            Assert.True(checkedSteps > samples * 0.5f, "the stance was barely sampled for " + leg);
        }
    }

    [Fact]
    public void ASwingingLegFoldsAtItsHingeAndClearsTheGround()
    {
        const int samples = 400;
        foreach (QuadrupedGait.Leg leg in Legs)
        {
            // Walk this leg's OWN stride, footfall to footfall, so the lift and the landing are its edges.
            float footfall = QuadrupedGait.FootfallPhases[(int)leg];
            float peakFlex = 0f, peakLift = float.MinValue;
            for (int i = 0; i <= samples; i++)
            {
                float local = (float)i / samples;
                QuadrupedPose pose = QuadrupedGait.PoseAt(Rig, footfall + local, 1f);
                float flex = Flex(pose, leg);
                Assert.True(flex >= 0f, "a hinge folded the wrong way on " + leg);
                if (QuadrupedGait.IsStance(local) || local >= 1f)
                {
                    // A planted leg is straight: a folded joint on the ground is a leg standing on air.
                    Assert.Equal(0f, flex, 4);
                    continue;
                }
                peakFlex = MathF.Max(peakFlex, flex);
                peakLift = MathF.Max(peakLift, Sole(pose, leg).Y);
            }
            bool fore = QuadrupedGait.IsFore(leg);
            float amplitude = QuadrupedGait.SwingFor(Rig, fore);
            // The fold peaks at the constant for its hinge, and the front feet lift higher than the hind.
            Assert.Equal(fore ? QuadrupedGait.ForeFlexRadians : QuadrupedGait.HindFlexRadians, peakFlex, 2);
            Assert.InRange(peakLift, fore ? 0.18f : 0.06f, fore ? 0.45f : 0.26f);
            // It leaves the ground trailing and lands reaching: back at the lift, forward at the footfall,
            // give or take the overshoot a continuous rate buys at each end.
            float liftSwing = Swing(QuadrupedGait.PoseAt(Rig, footfall + QuadrupedGait.DutyFactor + 0.002f, 1f), leg);
            float landingSwing = Swing(QuadrupedGait.PoseAt(Rig, footfall + 0.998f, 1f), leg);
            Assert.InRange(liftSwing, -amplitude * 1.1f, -amplitude * 0.9f);
            Assert.InRange(landingSwing, amplitude * 0.9f, amplitude * 1.1f);
        }
    }

    /// <summary>
    /// THE FOLD IS FRONT-LOADED, the way a real animal folds it: the cannon comes up fast off the ground,
    /// peaks well before half way through the swing, and the leg is nearly straight again as it reaches for
    /// the footfall, so the hoof lands on an extended leg rather than a bent one.
    /// </summary>
    [Fact]
    public void TheHingeFoldsEarlyInTheSwingAndStraightensToReach()
    {
        float peakAt = 0f, peak = 0f;
        for (int i = 0; i <= 1000; i++)
        {
            float u = i / 1000f;
            float flex = QuadrupedGait.SwingFlex(u, 1f);
            Assert.InRange(flex, 0f, 1f);
            if (flex > peak) (peak, peakAt) = (flex, u);
        }
        Assert.Equal(1f, peak, 3);
        Assert.Equal(QuadrupedGait.FlexPeak, peakAt, 2);
        Assert.InRange(QuadrupedGait.FlexPeak, 0.3f, 0.45f);
        // Straighter at nine tenths of the swing than at one tenth: the reach is the slow half.
        Assert.True(QuadrupedGait.SwingFlex(0.9f, 1f) < QuadrupedGait.SwingFlex(0.1f, 1f));
        Assert.True(QuadrupedGait.SwingFlex(0.95f, 1f) < 0.25f, "the leg lands bent");
        Assert.Equal(0f, QuadrupedGait.SwingFlex(0f, 1f));
        Assert.Equal(0f, QuadrupedGait.SwingFlex(1f, 1f));
        // And it starts folding gently rather than snapping: the first hundredth of the swing folds less
        // than a twentieth of the peak.
        Assert.True(QuadrupedGait.SwingFlex(0.01f, 1f) < 0.05f, "the knee snaps off the ground");
    }

    /// <summary>
    /// THE CARPUS FOLDS BACK AND THE HOCK FOLDS FORWARD, which is the anatomy this rig writes down once. A
    /// folded fore cannon tucks its hoof BEHIND the hinge, under the chest, and a folded hind cannon carries
    /// its hoof AHEAD of the hock, under the belly. Pinned through the rig's own hinge transforms, because the
    /// gait hands both over as plain non-negative magnitudes and the sign lives nowhere else.
    /// </summary>
    [Fact]
    public void TheCarpusFoldsBackAndTheHockFoldsForward()
    {
        Vector3 hoof = new(0f, -0.4f, 0f);
        Vector3 straightFore = Vector3.Transform(hoof, Rig.ForeHinge(0f));
        Vector3 foldedFore = Vector3.Transform(hoof, Rig.ForeHinge(0.8f));
        Assert.True(foldedFore.Z < straightFore.Z - 0.2f, "the fore hoof did not tuck back under the chest");
        Assert.True(foldedFore.Y > straightFore.Y + 0.1f, "the fore hoof did not come up");

        Vector3 straightHind = Vector3.Transform(hoof, Rig.HindHinge(0f));
        Vector3 foldedHind = Vector3.Transform(hoof, Rig.HindHinge(0.8f));
        Assert.True(foldedHind.Z > straightHind.Z + 0.2f, "the hind hoof did not come forward under the belly");
        Assert.True(foldedHind.Y > straightHind.Y + 0.1f, "the hind hoof did not come up");

        // And a positive swing is forward for every leg, off the shared limb rule.
        Vector3 swung = Vector3.Transform(hoof, BodyRig.Limb(Rig.RightHip, 0.4f));
        Assert.True(swung.Z > Rig.RightHip.Z + 0.1f);
    }

    /// <summary>
    /// THE LEG'S RATE IS CONTINUOUS THROUGH THE LIFT AND THE FOOTFALL. The stance curve turns the leg back
    /// at a rate that is steepest at its ends, and the swing curve leaves and arrives at exactly that rate,
    /// so nothing stops dead: the hoof is still travelling back as it lifts and already travelling back as it
    /// lands. A swing that started and ended at rest was the hitch that read as segmented motion.
    /// </summary>
    [Fact]
    public void TheSwingLeavesAndLandsAtTheStanceRate()
    {
        float amplitude = QuadrupedGait.SwingFor(Rig, fore: true);
        const float h = 1e-3f;
        // Rates in radians per CYCLE fraction on both sides of each transition.
        float stanceEnd = (QuadrupedGait.StanceSwing(1f, amplitude) - QuadrupedGait.StanceSwing(1f - h, amplitude))
                          / (h * QuadrupedGait.DutyFactor);
        float swingStart = (QuadrupedGait.SwingSwing(h, amplitude) - QuadrupedGait.SwingSwing(0f, amplitude))
                           / (h * (1f - QuadrupedGait.DutyFactor));
        float swingEnd = (QuadrupedGait.SwingSwing(1f, amplitude) - QuadrupedGait.SwingSwing(1f - h, amplitude))
                         / (h * (1f - QuadrupedGait.DutyFactor));
        float stanceStart = (QuadrupedGait.StanceSwing(h, amplitude) - QuadrupedGait.StanceSwing(0f, amplitude))
                            / (h * QuadrupedGait.DutyFactor);
        Assert.True(stanceEnd < 0f, "the stance does not travel back");
        // Within a few percent of each other: the stance curve is steepest at its ends, so a finite
        // difference across it reads a little shallower than the tangent the swing was built on.
        Assert.InRange(swingStart / stanceEnd, 0.95f, 1.05f);
        Assert.InRange(swingEnd / stanceStart, 0.95f, 1.05f);
        // And it still gets from one end to the other.
        Assert.Equal(-amplitude, QuadrupedGait.SwingSwing(0f, amplitude), 4);
        Assert.Equal(amplitude, QuadrupedGait.SwingSwing(1f, amplitude), 4);
    }

    /// <summary>
    /// THE TRUNK SWAYS WITH THE LEGS AND NOT ON ITS OWN. It yaws so the left hip comes forward with the left
    /// hind leg, which is the tail end going left. The head sways so the muzzle goes right as the left fore
    /// reaches. The torso rolls once a stride with the left side highest in the middle of the left hind's
    /// swing. All of it scales with the weight and all of it is small, a couple of degrees at most.
    /// </summary>
    [Fact]
    public void TheTrunkYawsWithTheHindLegsAndRollsOntoTheStandingSide()
    {
        float yawHigh = float.MinValue, headHigh = float.MinValue, rollHigh = float.MinValue;
        for (int i = 0; i < 400; i++)
        {
            float phase = i / 400f;
            QuadrupedPose pose = QuadrupedGait.PoseAt(Rig, phase, 1f);
            // Same sign as the left-minus-right hind swing, and the head opposite to the left-minus-right fore.
            float hind = pose.LeftHindSwing - pose.RightHindSwing;
            float fore = pose.LeftForeSwing - pose.RightForeSwing;
            if (MathF.Abs(hind) > 0.05f) Assert.Equal(MathF.Sign(hind), MathF.Sign(pose.TrunkYaw));
            if (MathF.Abs(fore) > 0.05f) Assert.Equal(-MathF.Sign(fore), MathF.Sign(pose.HeadYaw));
            yawHigh = MathF.Max(yawHigh, MathF.Abs(pose.TrunkYaw));
            headHigh = MathF.Max(headHigh, MathF.Abs(pose.HeadYaw));
            rollHigh = MathF.Max(rollHigh, pose.Roll);
        }
        // The two legs of a pair are half a cycle apart, so their swings never oppose at full amplitude at
        // one instant: the yaw reaches most of its constant, never all of it.
        Assert.InRange(yawHigh, QuadrupedGait.TrunkYawRadians * 0.6f, QuadrupedGait.TrunkYawRadians);
        Assert.InRange(headHigh, QuadrupedGait.HeadYawRadians * 0.6f, QuadrupedGait.HeadYawRadians);
        Assert.Equal(QuadrupedGait.RollRadians, rollHigh, 3);
        Assert.InRange(QuadrupedGait.TrunkYawRadians, 0.015f, 0.05f);
        Assert.InRange(QuadrupedGait.HeadYawRadians, 0.015f, 0.06f);
        Assert.InRange(QuadrupedGait.RollRadians, 0.01f, 0.05f);

        // Left side highest half way through the left hind's swing, when the right hind carries it.
        float leftHindMidSwing = QuadrupedGait.FootfallPhases[(int)QuadrupedGait.Leg.LeftHind]
                                 + ((1f + QuadrupedGait.DutyFactor) * 0.5f);
        Assert.Equal(QuadrupedGait.RollRadians, QuadrupedGait.PoseAt(Rig, leftHindMidSwing, 1f).Roll, 4);
        Assert.Equal(-QuadrupedGait.RollRadians, QuadrupedGait.PoseAt(Rig, leftHindMidSwing + 0.5f, 1f).Roll, 4);

        // Half weight is half the sway, and no weight is none.
        QuadrupedPose full = QuadrupedGait.PoseAt(Rig, 0.2f, 1f);
        QuadrupedPose half = QuadrupedGait.PoseAt(Rig, 0.2f, 0.5f);
        Assert.Equal(full.TrunkYaw * 0.5f, half.TrunkYaw, 5);
        Assert.Equal(full.Roll * 0.5f, half.Roll, 5);
        Assert.Equal(full.HeadYaw * 0.5f, half.HeadYaw, 5);
        Assert.Equal(0f, QuadrupedGait.PoseAt(Rig, 0.2f, 0f).Roll);
        Assert.Equal(0f, QuadrupedGait.PoseAt(Rig, 0.2f, 0f).TrunkYaw);
    }

    /// <summary>
    /// THE HOOVES DO NOT FOLLOW THE ROLL OR THE BREATH, AND THEY DO FOLLOW THE YAW. A rolled, breathing
    /// torso leaves every sole where it was, and a yawed trunk carries all four hooves round with the hips
    /// and shoulders rather than leaving them behind under a body that turned without them.
    /// </summary>
    [Fact]
    public void TheHoovesStayPlantedUnderARollingBreathingTorsoAndTurnWithTheTrunk()
    {
        var pose = new BodyPose(new Vector3(3f, 0f, -4f), 0.4f);
        Span<Matrix4x4> still = stackalloc Matrix4x4[TestBodies.QuadrupedPieceCount];
        TestBodies.Quadruped(Rig, pose, WalkPose.Rest, QuadrupedPose.Rest, still);
        Span<Matrix4x4> swayed = stackalloc Matrix4x4[TestBodies.QuadrupedPieceCount];
        TestBodies.Quadruped(Rig, pose, WalkPose.Rest with { TorsoRise = 0.02f },
            QuadrupedPose.Rest with { Roll = 0.03f }, swayed);
        for (int leg = 0; leg < 4; leg++)
        {
            Assert.Equal(still[TestBodies.UpperForeLeft + leg], swayed[TestBodies.UpperForeLeft + leg]);
            Assert.Equal(still[TestBodies.LowerForeLeft + leg], swayed[TestBodies.LowerForeLeft + leg]);
        }
        Assert.NotEqual(still[TestBodies.Trunk], swayed[TestBodies.Trunk]);
        // The body rose by the breath and rolled: its origin is up by the rise (and a hair more, because
        // the roll is about the spine above it), and its x axis tilted.
        Assert.Equal(still[TestBodies.Trunk].Translation.Y + 0.02f, swayed[TestBodies.Trunk].Translation.Y, 3);

        Span<Matrix4x4> yawed = stackalloc Matrix4x4[TestBodies.QuadrupedPieceCount];
        TestBodies.Quadruped(Rig, pose, WalkPose.Rest, QuadrupedPose.Rest with { TrunkYaw = 0.05f }, yawed);
        // Every leg turned with the trunk, and every hoof stayed on the floor while it did.
        for (int leg = 0; leg < 4; leg++)
        {
            Assert.NotEqual(still[TestBodies.UpperForeLeft + leg], yawed[TestBodies.UpperForeLeft + leg]);
            bool fore = leg < 2;
            float soleDrop = fore ? Rig.ForeSoleFromHinge : Rig.HindSoleFromHinge;
            Vector3 sole = Vector3.Transform(
                new Vector3(0f, -soleDrop, 0f), yawed[TestBodies.LowerForeLeft + leg]);
            Assert.Equal(pose.Position.Y + TestBodies.AuthoredFloor, sole.Y, 3);
        }

        // A positive trunk yaw puts the tail end (behind the centre) to the animal's LEFT, +x in the body's
        // frame at yaw zero, and the muzzle end to its right.
        var square = new BodyPose(Vector3.Zero, 0f);
        Span<Matrix4x4> turned = stackalloc Matrix4x4[TestBodies.QuadrupedPieceCount];
        TestBodies.Quadruped(Rig, square, WalkPose.Rest, QuadrupedPose.Rest with { TrunkYaw = 0.1f }, turned);
        Vector3 tail = Vector3.Transform(new Vector3(0f, 1f, -1.1f), turned[TestBodies.Trunk]);
        Vector3 withers = Vector3.Transform(new Vector3(0f, 1.4f, 0.6f), turned[TestBodies.Trunk]);
        Assert.True(tail.X > 0.02f, "the tail did not swing left");
        Assert.True(withers.X < -0.02f, "the shoulders did not swing right");
    }

    [Fact]
    public void TheSwingIsSizedOffTheRigAndCappedForALongLimb()
    {
        float fore = QuadrupedGait.SwingFor(Rig, fore: true);
        float hind = QuadrupedGait.SwingFor(Rig, fore: false);
        // Half the stance travel over the leg: the reach the hoof has to make fore and aft of the pivot.
        float reach = Rig.StrideMetres * QuadrupedGait.DutyFactor * 0.5f;
        Assert.Equal(MathF.Asin(reach / Rig.ForeLegMetres), fore, 4);
        Assert.Equal(MathF.Asin(reach / Rig.HindLegMetres), hind, 4);
        // Around thirty degrees, which is what a grazing animal's limb sweeps at a walk.
        Assert.InRange(fore, 0.45f, 0.6f);
        Assert.InRange(hind, 0.5f, 0.65f);
        Assert.True(fore < QuadrupedGait.MaxSwingRadians && hind < QuadrupedGait.MaxSwingRadians);

        // A stride no leg could cover is capped rather than sweeping like a wiper.
        QuadrupedRig longStride = Rig with { StrideMetres = 4f };
        Assert.Equal(QuadrupedGait.MaxSwingRadians, QuadrupedGait.SwingFor(longStride, fore: true), 4);
    }

    /// <summary>
    /// THE BACK STAYS LEVEL. A walking body sits a few centimetres lower than a standing one, because a
    /// rigid leg off the vertical reaches less far down, and it sits there at a CONSTANT height through the
    /// whole stride: a sink that followed the legs frame by frame rose and fell twice a stride and read as an
    /// animal bouncing along. The constant is the closed-form mean of the shortfall over a stance, which a
    /// numeric average of the same curve has to agree with.
    /// </summary>
    [Fact]
    public void TheBodySinksAConstantFewCentimetresAndDoesNotBounce()
    {
        const int samples = 400;
        float low = float.MaxValue, high = float.MinValue;
        for (int i = 0; i <= samples; i++)
        {
            float bob = QuadrupedGait.PoseAt(Rig, (float)i / samples, 1f).Bob;
            Assert.True(bob <= 0f);
            low = MathF.Min(low, bob);
            high = MathF.Max(high, bob);
        }
        Assert.InRange(low, -0.08f, -0.02f);
        Assert.Equal(low, high, 5);

        // The closed form is the numeric mean over a stance of the same arcsine curve.
        float amplitude = QuadrupedGait.SwingFor(Rig, fore: false);
        float numeric = 0f;
        const int steps = 2000;
        for (int i = 0; i < steps; i++)
        {
            float u = (i + 0.5f) / steps;
            numeric += Rig.HindLegMetres * (1f - MathF.Cos(QuadrupedGait.StanceSwing(u, amplitude)));
        }
        numeric /= steps;
        Assert.Equal(numeric, QuadrupedGait.MeanStanceShortfall(Rig.HindLegMetres, amplitude), 4);
        Assert.Equal(0f, QuadrupedGait.MeanStanceShortfall(Rig.HindLegMetres, 0f));

        // Half weight is half the sink, so a body easing to a stop rises back onto straight legs.
        Assert.Equal(low * 0.5f, QuadrupedGait.PoseAt(Rig, 0.3f, 0.5f).Bob, 5);
    }

    [Fact]
    public void TheHeadNodsTwiceAStrideAndTheRightSideIsTheLeftHalfACycleLater()
    {
        const int samples = 400;
        float nodLow = float.MaxValue, nodHigh = float.MinValue;
        for (int i = 0; i < samples; i++)
        {
            float phase = (float)i / samples;
            QuadrupedPose now = QuadrupedGait.PoseAt(Rig, phase, 1f);
            QuadrupedPose later = QuadrupedGait.PoseAt(Rig, phase + 0.5f, 1f);
            nodLow = MathF.Min(nodLow, now.HeadNod);
            nodHigh = MathF.Max(nodHigh, now.HeadNod);
            // Left and right are the same gait half a cycle apart, on both pairs. Three places rather than
            // four, because a sample that lands a float's width past a stance edge on one side and not the
            // other reads a hair of flexion there.
            Assert.Equal(now.LeftForeSwing, later.RightForeSwing, 3);
            Assert.Equal(now.LeftHindSwing, later.RightHindSwing, 3);
            Assert.Equal(now.LeftForeFlex, later.RightForeFlex, 3);
            Assert.Equal(now.LeftHindFlex, later.RightHindFlex, 3);
            Assert.Equal(now.HeadNod, later.HeadNod, 3);
        }
        Assert.Equal(-QuadrupedGait.HeadNodRadians, nodLow, 3);
        Assert.Equal(QuadrupedGait.HeadNodRadians, nodHigh, 3);
    }

    [Fact]
    public void TheWeightScalesEveryChannelSoAStopSettlesOntoStraightLegs()
    {
        QuadrupedPose full = QuadrupedGait.PoseAt(Rig, 0.31f, 1f);
        QuadrupedPose half = QuadrupedGait.PoseAt(Rig, 0.31f, 0.5f);
        Assert.Equal(full.LeftForeSwing * 0.5f, half.LeftForeSwing, 5);
        Assert.Equal(full.RightHindSwing * 0.5f, half.RightHindSwing, 5);
        Assert.Equal(full.LeftForeFlex * 0.5f, half.LeftForeFlex, 5);
        Assert.Equal(full.RightHindFlex * 0.5f, half.RightHindFlex, 5);
        Assert.Equal(full.Bob * 0.5f, half.Bob, 5);
        Assert.Equal(full.HeadNod * 0.5f, half.HeadNod, 5);
        // The phase wraps, so a cycle that ran past one reads as the same stride.
        QuadrupedPose wrapped = QuadrupedGait.PoseAt(Rig, 1.31f, 1f);
        Assert.Equal(full.LeftForeSwing, wrapped.LeftForeSwing, 4);
        Assert.Equal(full.RightHindFlex, wrapped.RightHindFlex, 4);
        Assert.Equal(full.Bob, wrapped.Bob, 4);
    }

    /// <summary>
    /// The gait rides the SHARED cycle, so a body that walks a stride of ground comes back to the same pose,
    /// and the stride it walks is the one the paired <see cref="BodyRig"/> hands the cycle, which is the
    /// quadruped rig's own. Two numbers for one stride is an animal whose legs turn over at one speed and
    /// whose gait repeats at another.
    /// </summary>
    [Fact]
    public void TheSharedCycleWalksTheQuadrupedRigsStride()
    {
        Assert.Equal(TestBodies.Grazer.StrideMetres, TestBodies.GrazerBody.StrideMetres);
        Assert.Equal(TestBodies.Grazer.RestHeightMetres, TestBodies.GrazerBody.RestHeightMetres, 4);

        var cycle = new WalkCycle(TestBodies.GrazerBody);
        var at = Vector3.Zero;
        const float dt = 1f / 60f;
        cycle.Advance(at, dt);
        for (float t = 0f; t < 1f; t += dt)
        {
            at += new Vector3(0f, 0f, 1.5f * dt);
            cycle.Advance(at, dt);
        }
        Assert.Equal(1f, cycle.Weight, 3);
        QuadrupedPose before = QuadrupedGait.PoseAt(Rig, cycle.Sample);
        float travelled = 0f;
        while (travelled < Rig.StrideMetres - 1e-4f)
        {
            float step = MathF.Min(1.5f * dt, Rig.StrideMetres - travelled);
            at += new Vector3(0f, 0f, step);
            travelled += step;
            cycle.Advance(at, dt);
        }
        QuadrupedPose after = QuadrupedGait.PoseAt(Rig, cycle.Sample);
        Assert.Equal(before.LeftHindSwing, after.LeftHindSwing, 2);
        Assert.Equal(before.RightForeSwing, after.RightForeSwing, 2);
    }
}
