using System;
using System.Numerics;

namespace KhaozEngine.SegmentRig;

/// <summary>Everything a four-legged body is drawn at this frame: four leg swings, four hinge flexions,
/// the bob that moves the whole body and the nod that moves the head. Angles are radians, and a SWING is
/// positive FORWARD (toward engine +z, the way the body faces at yaw 0), the same convention
/// <see cref="WalkPose"/> holds and <see cref="BodyRig.Limb(System.Numerics.Vector3, float)"/> applies.</summary>
/// <param name="LeftForeSwing">The left foreleg's swing about the shoulder.</param>
/// <param name="RightForeSwing">The right foreleg's swing about the shoulder.</param>
/// <param name="LeftHindSwing">The left hind leg's swing about the hip.</param>
/// <param name="RightHindSwing">The right hind leg's swing about the hip.</param>
/// <param name="LeftForeFlex">How far the left carpus is folded. A FLEXION rather than a swing: never
/// negative, because the joint bends one way, and <see cref="QuadrupedRig.ForeHinge"/> knows which.</param>
/// <param name="RightForeFlex">The right carpus.</param>
/// <param name="LeftHindFlex">How far the left hock is folded. Never negative, and the hinge runs the OTHER
/// way from the carpus: <see cref="QuadrupedRig.HindHinge"/> carries the hoof forward rather than back.</param>
/// <param name="RightHindFlex">The right hock.</param>
/// <param name="Bob">How far the whole body is lifted or dropped this frame, METRES. Negative sinks it,
/// which is the only direction a walk uses: a standing body on four straight legs is already the tall one,
/// and a leg swung off the vertical reaches less far down. Constant through a walk, so the back stays
/// level.</param>
/// <param name="HeadNod">How far the muzzle is dipped about the poll, radians, positive being DOWN.</param>
/// <param name="Roll">How far the torso rolls about the spine, radians, positive lifting the animal's LEFT
/// side. The legs never see it. Zero everywhere but a walk.</param>
/// <param name="TrunkYaw">How far the whole trunk and its legs are turned about the body's vertical axis,
/// radians, positive swinging the tail end to the animal's left. The hip on the side of the reaching hind
/// leg comes forward, which puts the tail over to the other side and the shoulders the opposite way.</param>
/// <param name="HeadYaw">How far the head is swung about the poll on top of the trunk's own yaw, radians,
/// positive to the animal's left.</param>
/// <param name="Surge">How far the whole body, legs included, is carried along the way it faces, METRES.
/// Zero everywhere but a strike.</param>
/// <param name="Pitch">How far the whole body, legs included, is tipped about the line through the two
/// shoulder joints, radians, positive dipping the muzzle end. Zero everywhere but a strike.</param>
/// <param name="RootOffset">Local displacement of the whole body after a root fall, in metres. Zero for a
/// body standing on its feet.</param>
/// <param name="RootRoll">Roll of the whole body about the point its pose names, radians, positive lifting
/// the animal's left side. Zero for a body standing on its feet.</param>
/// <param name="LeftForeSplay">Outward splay of the left foreleg at its shoulder, radians.</param>
/// <param name="RightForeSplay">Outward splay of the right foreleg at its shoulder, radians.</param>
/// <param name="LeftHindSplay">Outward splay of the left hind leg at its hip, radians.</param>
/// <param name="RightHindSplay">Outward splay of the right hind leg at its hip, radians.</param>
/// <remarks>THE ORDER IS THE CONTRACT, as it is on <see cref="WalkPose"/>: every one of these is positional,
/// so a new channel is appended and never slotted in beside the one it reads like.</remarks>
public readonly record struct QuadrupedPose(
    float LeftForeSwing, float RightForeSwing, float LeftHindSwing, float RightHindSwing,
    float LeftForeFlex = 0f, float RightForeFlex = 0f, float LeftHindFlex = 0f, float RightHindFlex = 0f,
    float Bob = 0f, float HeadNod = 0f, float Roll = 0f, float TrunkYaw = 0f, float HeadYaw = 0f,
    float Surge = 0f, float Pitch = 0f, Vector3 RootOffset = default, float RootRoll = 0f,
    float LeftForeSplay = 0f, float RightForeSplay = 0f,
    float LeftHindSplay = 0f, float RightHindSplay = 0f)
{
    /// <summary>Every angle zero and no bob: a body standing square on four straight legs, which is the
    /// pose the pieces were authored in.</summary>
    public static QuadrupedPose Rest => default;
}

/// <summary>The four-beat WALK of a grazing animal, sampled off the shared <see cref="WalkCycle"/>'s
/// phase. A pure function of where in the stride the body is, so it is tested without a scene.</summary>
/// <remarks>
/// <para>THE GAIT IS A LATERAL-SEQUENCE WALK, which is what every quadruped uses at walking speed and the
/// thing that separates a walking animal from a rocking toy. The feet land one at a time, a quarter of a
/// cycle apart, in the order left hind, left fore, right hind, right fore
/// (<see cref="FootfallPhases"/>): each hind foot lands, and a quarter cycle later the fore foot on the SAME
/// side follows it. Two or three feet are always on the ground. A diagonal-pair gait with two feet down at
/// once is a trot, and a grazing animal does not trot across a paddock.</para>
/// <para>Each leg spends <see cref="DutyFactor"/> of the cycle on the ground and the rest in the air. On the
/// ground it does not move: the BODY moves over it, so relative to the body the hoof travels backward at
/// exactly the ground speed, which is what <see cref="StanceSwing"/> arranges (an arcsine over a linear
/// travel, so the hoof's horizontal position under the pivot is linear in phase rather than the angle). A
/// hoof that skates is a leg whose angle is a sine of the phase: it matches the ground speed at mid stance
/// and slides everywhere else. Then it swings forward through the air faster than it came back, folding at
/// the carpus or the hock to clear the ground (<see cref="SwingFlex"/>) and unfolding again to reach for the
/// next footfall. The fold is FRONT-LOADED: a real animal folds the cannon nearly flat right after
/// the hoof leaves the ground and straightens the leg late, reaching, so the peak sits at
/// <see cref="FlexPeak"/> of the swing rather than half way.</para>
/// <para>How far a leg swings is SIZED OFF THE RIG rather than written down: the stance travel is the
/// stride times the duty factor, and half of that is what the hoof has to reach fore and aft of the pivot,
/// so <see cref="SwingFor"/> is the arcsine of that reach over the leg's own length. A longer stride or a
/// shorter leg swings further, and a stride no leg could cover is capped at <see cref="MaxSwingRadians"/>
/// and skates a little rather than sweeping like a wiper.</para>
/// <para>The SINK is the price of rigid legs. A straight leg swung off the vertical reaches less far down
/// than one under the body, so a walking body sits lower than a standing one by the shortfall of the legs
/// it stands on, averaged over the stride (<see cref="SinkFor"/>). It is a CONSTANT while walking, blended
/// in with the weight, and deliberately not the frame-by-frame average: that rose and fell twice a stride
/// and read as an animal bouncing along, and a grazing animal's back stays level. What the constant leaves
/// is a hoof that floats a few centimetres at the front and back of its stance and sinks a few at the
/// middle, the same trade the biped makes.</para>
/// <para>The HEAD nods twice a stride, dipping as each foreleg takes the weight, the way a walking
/// ruminant's does. It is small on purpose: the eye reads the rhythm, not the distance.</para>
/// <para>THE TRUNK IS NOT RIGID. It yaws so the hip on the side of the reaching hind leg comes forward,
/// which is a few degrees of tail swinging the other way and the shoulders swinging back
/// (<see cref="TrunkYawRadians"/>), read straight off the difference between the two hind swings so it
/// follows the legs by construction. The whole torso rolls once a stride toward the side that is carrying
/// the hind weight (<see cref="RollRadians"/>), and the head sways on top of that with the forelegs
/// (<see cref="HeadYawRadians"/>). Every one of these is subtle: an animal that visibly waddles is a cartoon.
/// A pelvis and a thorax that counter-rotate, which is what a real spine does, were tried as separate
/// pieces and could not be cut without a seam that opened at the flank, so the trunk moves as one.</para>
/// </remarks>
public static class QuadrupedGait
{
    /// <summary>The fraction of the cycle each hoof spends on the ground. Cattle at a walk are 0.6 to 0.7,
    /// and this sits about there: two feet down through most of the stride and three at the
    /// overlaps.</summary>
    public const float DutyFactor = 0.66f;

    /// <summary>Where in the cycle each hoof LANDS, as a fraction: the lateral sequence, left hind first,
    /// a quarter apart. In the order of <see cref="Leg"/>.</summary>
    public static readonly float[] FootfallPhases = [0.25f, 0.75f, 0f, 0.5f];

    /// <summary>The most a leg swings each way at a walk, radians. About 37 degrees. A grazing animal's limb
    /// sweeps thirty-odd at a walk, so the cap is a guard on the stride sizing rather than a number the body
    /// meets.</summary>
    public const float MaxSwingRadians = 0.65f;

    /// <summary>How far the carpus folds at the peak of a foreleg's swing, radians. About 77 degrees: the
    /// cannon comes up nearly flat under the forearm as the leg starts forward, the hoof a forearm's length
    /// off the ground. A grazing animal lifts its front feet far higher than its hind.
    /// </summary>
    public const float ForeFlexRadians = 1.35f;

    /// <summary>How far the hock folds at the peak of a hind leg's swing, radians. About 40 degrees. The
    /// hind feet clear the ground by less, and a hock that folded as far as the carpus would prance.
    /// </summary>
    public const float HindFlexRadians = 0.7f;

    /// <summary>Where in the swing, 0 at the lift to 1 at the footfall, the hinge is most folded. Under half:
    /// the cannon folds fast off the ground and the leg spends the back half of the swing straightening to
    /// reach for the footfall.</summary>
    public const float FlexPeak = 0.375f;

    /// <summary>How far the muzzle dips at the bottom of each nod, radians. About two degrees, a couple of
    /// centimetres at the nose.</summary>
    public const float HeadNodRadians = 0.035f;

    /// <summary>Where in the cycle the head is lowest, as a fraction after each fore footfall. The head
    /// dips as the landing foreleg takes the weight, a little after it touches down.</summary>
    public const float HeadNodLag = 0.06f;

    /// <summary>How far the trunk yaws each way at full swing, radians. About a degree and three quarters,
    /// which is two centimetres at the tail root and the same the other way at the shoulders.</summary>
    public const float TrunkYawRadians = 0.03f;

    /// <summary>How far the torso rolls each way, radians. About a degree and a half at the back.</summary>
    public const float RollRadians = 0.025f;

    /// <summary>How far the head sways each way on top of the chest, radians. About two degrees.</summary>
    public const float HeadYawRadians = 0.03f;

    /// <summary>Where in the cycle the body's LEFT side is highest: the middle of the left hind leg's swing,
    /// when the right hind is carrying it. The left hind lands at its footfall phase and lifts
    /// <see cref="DutyFactor"/> later, so its swing is centred half way from there to the next footfall.
    /// </summary>
    public static float RollPeakPhase => FootfallPhases[(int)Leg.LeftHind] + ((1f + DutyFactor) * 0.5f);

    /// <summary>The four legs, in the order every array in this type uses.</summary>
    public enum Leg
    {
        /// <summary>The left foreleg.</summary>
        LeftFore = 0,

        /// <summary>The right foreleg.</summary>
        RightFore = 1,

        /// <summary>The left hind leg.</summary>
        LeftHind = 2,

        /// <summary>The right hind leg.</summary>
        RightHind = 3,
    }

    /// <summary>Whether a leg is one of the forelegs, which hinge at a carpus and fold back, rather than one
    /// of the hind legs, which hinge at a hock and fold forward.</summary>
    /// <param name="leg">The leg.</param>
    public static bool IsFore(Leg leg) => leg is Leg.LeftFore or Leg.RightFore;

    /// <summary>How far one pair of legs swings each way on a rig, radians: the arcsine of half the stance
    /// travel over the leg's length, capped at <see cref="MaxSwingRadians"/>.</summary>
    /// <param name="rig">The body.</param>
    /// <param name="fore">True for the forelegs, false for the hind.</param>
    public static float SwingFor(QuadrupedRig rig, bool fore)
    {
        ArgumentNullException.ThrowIfNull(rig);
        float leg = fore ? rig.ForeLegMetres : rig.HindLegMetres;
        float reach = rig.StrideMetres * DutyFactor * 0.5f;
        float sine = Math.Clamp(reach / leg, 0f, MathF.Sin(MaxSwingRadians));
        return MathF.Asin(sine);
    }

    /// <summary>This frame's pose off the shared cycle's sample.</summary>
    /// <param name="rig">The body, for its leg lengths and stride.</param>
    /// <param name="sample">The cycle's phase and blend weight, from <see cref="WalkCycle.Sample"/>.</param>
    public static QuadrupedPose PoseAt(QuadrupedRig rig, in GaitSample sample) =>
        PoseAt(rig, sample.Phase, sample.Weight);

    /// <summary>This frame's pose: every leg at its own point in the stride, the body sunk to the legs it is
    /// standing on, and the head nodding over the forelegs. Every channel scales with the weight, so a body
    /// easing to a stop settles onto four straight legs rather than freezing mid stride.</summary>
    /// <param name="rig">The body, for its leg lengths and stride.</param>
    /// <param name="phase">Where in the cycle the body is, 0 to 1, from <see cref="WalkCycle.Phase"/>.</param>
    /// <param name="weight">How much of the gait is applied, 0 standing to 1 walking, from
    /// <see cref="WalkCycle.Weight"/>.</param>
    public static QuadrupedPose PoseAt(QuadrupedRig rig, float phase, float weight)
    {
        ArgumentNullException.ThrowIfNull(rig);
        weight = Math.Clamp(weight, 0f, 1f);
        if (weight <= 0f) return QuadrupedPose.Rest;
        phase -= MathF.Floor(phase);

        float foreSwing = SwingFor(rig, fore: true);
        float hindSwing = SwingFor(rig, fore: false);
        Span<float> swing = stackalloc float[4];
        Span<float> flex = stackalloc float[4];
        for (int i = 0; i < 4; i++)
        {
            var leg = (Leg)i;
            bool fore = IsFore(leg);
            float local = LocalPhase(phase, leg);
            float amplitude = fore ? foreSwing : hindSwing;
            (swing[i], flex[i]) = LegAt(local, amplitude, fore ? ForeFlexRadians : HindFlexRadians);
        }

        float bob = -SinkFor(rig, foreSwing, hindSwing);
        // Twice a stride, lowest HeadNodLag after each fore footfall: the two fore footfalls are half a cycle
        // apart, so one cosine at double frequency lands its trough on both.
        float nodPhase = phase - FootfallPhases[(int)Leg.LeftFore] - HeadNodLag;
        float nod = HeadNodRadians * MathF.Cos(2f * MathF.Tau * nodPhase);
        // The trunk, off the legs. The left hip comes forward with the left hind, which is the trunk turned
        // so its tail end goes LEFT, so the yaw follows the left-minus-right hind swing straight. The head
        // sways with the forelegs: the left shoulder comes forward with the left fore, which turns the
        // muzzle RIGHT, so the head follows the right-minus-left fore swing. Both as fractions of the full
        // swing, so they stay in step whatever the rig's amplitude is.
        float trunkYaw = TrunkYawRadians * (swing[(int)Leg.LeftHind] - swing[(int)Leg.RightHind]) / (2f * hindSwing);
        float headYaw = HeadYawRadians * (swing[(int)Leg.RightFore] - swing[(int)Leg.LeftFore]) / (2f * foreSwing);
        float roll = RollRadians * MathF.Cos(MathF.Tau * (phase - RollPeakPhase));

        return new QuadrupedPose(
            LeftForeSwing: swing[(int)Leg.LeftFore] * weight,
            RightForeSwing: swing[(int)Leg.RightFore] * weight,
            LeftHindSwing: swing[(int)Leg.LeftHind] * weight,
            RightHindSwing: swing[(int)Leg.RightHind] * weight,
            LeftForeFlex: flex[(int)Leg.LeftFore] * weight,
            RightForeFlex: flex[(int)Leg.RightFore] * weight,
            LeftHindFlex: flex[(int)Leg.LeftHind] * weight,
            RightHindFlex: flex[(int)Leg.RightHind] * weight,
            Bob: bob * weight,
            HeadNod: nod * weight,
            Roll: roll * weight,
            TrunkYaw: trunkYaw * weight,
            HeadYaw: headYaw * weight);
    }

    /// <summary>Where in ITS OWN stride one leg is, 0 to 1, with 0 the moment its hoof lands. The stance
    /// runs from 0 to <see cref="DutyFactor"/> and the swing from there to 1.</summary>
    /// <param name="phase">The body's phase, 0 to 1.</param>
    /// <param name="leg">The leg.</param>
    public static float LocalPhase(float phase, Leg leg)
    {
        float local = phase - FootfallPhases[(int)leg];
        return local - MathF.Floor(local);
    }

    /// <summary>Whether a leg's hoof is on the ground at a point in its own stride.</summary>
    /// <param name="local">The leg's own phase, from <see cref="LocalPhase"/>.</param>
    public static bool IsStance(float local) => local < DutyFactor;

    /// <summary>One leg's swing and flexion at a point in its own stride.</summary>
    /// <param name="local">The leg's own phase, from <see cref="LocalPhase"/>.</param>
    /// <param name="amplitude">How far the leg swings each way, from <see cref="SwingFor"/>.</param>
    /// <param name="flexAmplitude">How far the hinge folds at mid swing.</param>
    public static (float Swing, float Flex) LegAt(float local, float amplitude, float flexAmplitude)
    {
        if (IsStance(local)) return (StanceSwing(local / DutyFactor, amplitude), 0f);
        float u = (local - DutyFactor) / (1f - DutyFactor);
        return (SwingSwing(u, amplitude), SwingFlex(u, flexAmplitude));
    }

    /// <summary>The swing angle through the stance, from +amplitude at the footfall to -amplitude at the
    /// lift, with the hoof's position under the pivot LINEAR in the stance fraction so it holds still on the
    /// ground while the body passes over it.</summary>
    /// <param name="u">How far through the stance, 0 at the footfall to 1 at the lift.</param>
    /// <param name="amplitude">How far the leg swings each way.</param>
    public static float StanceSwing(float u, float amplitude) =>
        MathF.Asin(MathF.Sin(amplitude) * (1f - (2f * u)));

    /// <summary>The swing angle through the air, from -amplitude back up to +amplitude, with the leg's
    /// angular velocity CONTINUOUS through both the lift and the footfall: it leaves the ground still
    /// travelling back at the stance rate, overshoots a little, comes forward, overshoots a little again and
    /// is already travelling back at the stance rate as it lands, which is what lets it land without a skid.
    /// A swing that stopped dead at each end was the hitch that read as segmented motion.</summary>
    /// <param name="u">How far through the swing, 0 at the lift to 1 at the footfall.</param>
    /// <param name="amplitude">How far the leg swings each way.</param>
    /// <remarks>A cubic Hermite from -amplitude to +amplitude whose end tangents are the stance curve's own
    /// rate at its ends (<see cref="StanceEndRate"/>), converted from per-stance to per-swing fractions.
    /// </remarks>
    public static float SwingSwing(float u, float amplitude)
    {
        float m = StanceEndRate(amplitude) * (1f - DutyFactor) / DutyFactor;
        float u2 = u * u, u3 = u2 * u;
        float h00 = (2f * u3) - (3f * u2) + 1f;
        float h10 = u3 - (2f * u2) + u;
        float h01 = (-2f * u3) + (3f * u2);
        float h11 = u3 - u2;
        return (h00 * -amplitude) + (h10 * m) + (h01 * amplitude) + (h11 * m);
    }

    /// <summary>How fast the stance curve is turning the leg at either end of the stance, radians per stance
    /// fraction, negative because the stance travels back. The arcsine curve is steepest at its ends, where
    /// the leg is furthest from vertical and a step of ground is a bigger step of angle.</summary>
    /// <param name="amplitude">How far the leg swings each way.</param>
    public static float StanceEndRate(float amplitude) => -2f * MathF.Tan(amplitude);

    /// <summary>The hinge's flexion through the air: a smooth bump that is zero at the lift and at the
    /// footfall, starts and ends with no snap, and peaks at <see cref="FlexPeak"/>, which is early, where the
    /// hoof passes under the standing leg and most needs the clearance. Zero through the whole stance, where
    /// a folded joint would be a leg standing on air.</summary>
    /// <param name="u">How far through the swing, 0 at the lift to 1 at the footfall.</param>
    /// <param name="flexAmplitude">How far the hinge folds at the peak.</param>
    /// <remarks>A beta-shaped bump, <c>u^p (1 - u)^q</c> normalised to one at its peak, with <c>p</c> fixed
    /// and <c>q</c> chosen so the peak lands on <see cref="FlexPeak"/>. Its rate is zero at both ends, so
    /// the knee starts folding as smoothly as it finishes: a skewed sine folded at an infinite rate off the
    /// ground, and that snap read as a segmented leg.</remarks>
    public static float SwingFlex(float u, float flexAmplitude)
    {
        if (u <= 0f || u >= 1f) return 0f;
        return flexAmplitude * MathF.Pow(u, FlexRise) * MathF.Pow(1f - u, FlexFall) / FlexBumpPeak;
    }

    const float FlexRise = 1.5f;
    static readonly float FlexFall = FlexRise * (1f - FlexPeak) / FlexPeak;
    static readonly float FlexBumpPeak = MathF.Pow(FlexPeak, FlexRise) * MathF.Pow(1f - FlexPeak, FlexFall);

    /// <summary>How far a walking body sits below a standing one, metres, never negative: the mean over a
    /// whole stance of how much less far down a leg reaches than a vertical one would, averaged over the
    /// fore and hind pairs.</summary>
    /// <param name="rig">The body, for its leg lengths.</param>
    /// <param name="foreSwing">The forelegs' swing amplitude, from <see cref="SwingFor"/>.</param>
    /// <param name="hindSwing">The hind legs' swing amplitude.</param>
    /// <remarks>The stance angle is an arcsine of a linear travel (<see cref="StanceSwing"/>), so the cosine
    /// it drops the hoof by averages in closed form: over a stance the mean of <c>cos t</c> is
    /// <c>(cos A + A / sin A) / 2</c>, which is what <see cref="MeanStanceShortfall"/> takes off the leg.
    /// </remarks>
    public static float SinkFor(QuadrupedRig rig, float foreSwing, float hindSwing)
    {
        ArgumentNullException.ThrowIfNull(rig);
        return (MeanStanceShortfall(rig.ForeLegMetres, foreSwing)
                + MeanStanceShortfall(rig.HindLegMetres, hindSwing)) * 0.5f;
    }

    /// <summary>The mean over a stance of how much less far down a rigid leg reaches than a vertical one,
    /// metres, for a leg of one length swinging through one amplitude.</summary>
    /// <param name="legMetres">The leg's length from its pivot to the floor.</param>
    /// <param name="amplitude">How far it swings each way, radians.</param>
    public static float MeanStanceShortfall(float legMetres, float amplitude)
    {
        if (amplitude <= 0f) return 0f;
        float meanCos = (MathF.Cos(amplitude) + (amplitude / MathF.Sin(amplitude))) * 0.5f;
        return legMetres * (1f - meanCos);
    }
}
