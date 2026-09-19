using System;
using System.Numerics;

namespace KhaozEngine.SegmentRig;

/// <summary>Everything one body is drawn at this frame: four root swings, four joint flexions and the
/// numbers that move the whole body. Angles are radians, and a SWING is positive FORWARD (toward engine +z,
/// the way the body faces at yaw 0). <see cref="BodyRig.Limb(Vector3, float)"/> is what turns a swing into a
/// transform, and it is the only place that sign convention is applied.</summary>
/// <param name="LeftArm">The character's left SHOULDER, the arm at engine +x.</param>
/// <param name="RightArm">The character's right shoulder, at engine -x.</param>
/// <param name="LeftLeg">The character's left HIP.</param>
/// <param name="RightLeg">The character's right hip.</param>
/// <param name="LeftElbow">How far the left elbow is bent. A FLEXION rather than a swing: never negative,
/// because an elbow bends one way, and <see cref="BodyRig.Elbow"/> knows which.</param>
/// <param name="RightElbow">The right elbow's flexion.</param>
/// <param name="LeftKnee">How far the left knee is bent. Never negative either, and the hinge runs the other
/// way: <see cref="BodyRig.Knee"/> carries the heel back rather than the hand forward.</param>
/// <param name="RightKnee">The right knee's flexion.</param>
/// <param name="Bob">How far the whole body is lifted or dropped this frame, METRES rather than radians.
/// Negative sinks it, which is the only direction a walk uses: a body is at full height when it stands on a
/// vertical leg, and everything else in the stride is lower.</param>
/// <param name="Lean">How far the whole body tips forward about its own local x, radians, positive tipping
/// the head the way the body faces. Zero at a walk, and what makes a run read as a run.</param>
/// <param name="RightArmYaw">How far the WEAPON shoulder is carried sideways about the body's own up axis,
/// radians, positive swinging the arm OUT to the weapon side (engine -x). The second axis at that one joint,
/// composed outside the swing by <see cref="BodyRig.Limb(Vector3, float, float)"/>, so the swing raises the
/// arm inside a plane this has already turned. Zero everywhere but a stroke: a walk and a breath both hang
/// their arms in the body's own plane.</param>
/// <param name="RightWrist">How far the piece in the WEAPON hand is tipped about that hand, radians, positive
/// carrying its head the way the body faces. It moves the HELD PIECE and never the arm. The off hand has
/// <see cref="LeftWrist"/> below rather than a copy of this one, on a different axis, because a blade wants
/// its head pointed and a plate wants its face aimed. Zero everywhere but a stroke.</param>
/// <param name="TorsoRise">How far the TORSO alone is lifted off the pelvis this frame, METRES, positive
/// lifting it. The legs never see it: the torso, the head, the arms and whatever the hands hold all rise
/// together and the feet stay exactly where they are. That is the whole difference between this and
/// <paramref name="Bob"/>, which moves the body and its feet with it. Zero everywhere but a breath.</param>
/// <param name="LeftArmYaw">How far the OFF shoulder is carried sideways about the body's own up axis,
/// radians, and <see cref="RightArmYaw"/>'s twin on the other side. The sign convention is the SAME one, so
/// positive carries the arm toward engine -x, which on the off side is ACROSS the chest rather than out: see
/// <see cref="BodyRig.Limb(Vector3, float, float)"/>, which is why the two channels name a side instead of
/// one number serving both. Zero everywhere but a guard pose, whose whole reason for wanting it is that a
/// plate hanging at the side faces out to +x and has to be turned toward the attacker to read as a
/// block.</param>
/// <param name="LeftWrist">How far the piece in the OFF hand is TURNED about the body's own up axis, through
/// the fist that holds it, radians. <see cref="RightWrist"/>'s twin on the other side and NOT the same axis:
/// that one tips a piece about the HAND's local x, carrying a blade's head the way the body faces, and this
/// one turns the piece about the BODY's vertical, which is the only axis a plate's face can be aimed on
/// without the arm under it having a say. Positive carries the face from the character's left round toward
/// its back and negative round toward its FRONT, so a quarter turn negative presents a plate square at
/// whatever the body faces. It moves the HELD PIECE and never the arm. Zero everywhere but a guard
/// pose.</param>
/// <param name="TorsoLean">How far the torso alone tips about its own BASE, radians, positive tipping the
/// head the way the body faces. <paramref name="Lean"/>'s twin above the hips: the same rotation about the
/// same height, applied to the upper body only, so the soles do not slide the opposite way from the head.
/// Zero everywhere but a breath.</param>
/// <param name="RootPitch">How far the WHOLE body tips about its own local x at its ROOT, radians, positive
/// carrying the head the way the body faces and the feet the other way. <paramref name="Lean"/>'s big
/// brother and a different pivot: a lean folds a standing body at the hips and leaves the soles where they
/// are, and this one turns the entire rig about the point it stands on, feet included, which is what a body
/// with NO GROUND UNDER IT does. A swimmer lies out flat at about a quarter turn of this, a fall pitches
/// forward into it, and a body on its feet leaves it at zero. <see cref="BodyRig.Body"/> composes it inside
/// the facing, so it tips the way the body faces rather than toward a world axis.</param>
/// <param name="RootRoll">How far the whole body rolls about its own forward axis at the root, radians,
/// positive lifting the character's LEFT side (engine +x). <paramref name="RootPitch"/>'s other axis and the
/// same pivot, in the same sense as <see cref="QuadrupedPose.Roll"/>. What banks a swimmer into a turn or
/// tips a falling body off square. Zero for a body on its feet.</param>
/// <remarks>The eight limb numbers are two per limb because a limb is TWO pieces: a root swing about the
/// shoulder or the hip, and a flexion at the joint half way down it. Then the body's own two, applied once to
/// the whole rig rather than per piece, the weapon arm's own two, which only a stroke ever fills in, and the
/// two root angles a body with no ground contact tips on.
/// <para>Every channel past the first four is DEFAULTED, and each is written by a small number of cycles: a
/// body that is not swinging a tool has no sideways weapon shoulder and no wrist, a body that is not
/// breathing has no torso rise and no torso lean, a body that is not taking a blow on a plate has neither a
/// sideways off shoulder nor a turned off hand, and a body standing on the ground has neither root
/// angle.</para>
/// <para>THE ORDER IS THE CONTRACT. Every one of these is positional, so a new channel is APPENDED and never
/// slotted in beside the one it reads like: <see cref="LeftWrist"/> sits after <see cref="LeftArmYaw"/>
/// rather than beside <see cref="RightWrist"/> for exactly that reason, and <see cref="RootPitch"/> and
/// <see cref="RootRoll"/> sit at the end rather than beside <see cref="Lean"/>, which is the channel they
/// read like. Every positional construction a consumer has already written stays valid because of it, and
/// that is the whole reason the rule exists: a game holds its own poses, and inserting a channel would
/// silently re-point every one of them at the wrong number.</para>
/// <para>THE THREE PAIRS THAT MOVE THE BODY ARE NOT INTERCHANGEABLE. <see cref="Bob"/> and
/// <see cref="Lean"/> carry the WHOLE body about HIP height, feet included, which is what a walk wants
/// because a walk is already lifting its feet. <see cref="TorsoRise"/> and <see cref="TorsoLean"/> carry
/// everything ABOVE the hips and nothing else, which is what a standing body wants: a breath that moved the
/// whole body pushed the soles 9 mm under the floor at the bottom of every exhale and slid them 12 mm across
/// the ground at each end of the lean. <see cref="RootPitch"/> and <see cref="RootRoll"/> carry the whole
/// body about its ROOT, which only makes sense once the soles are not standing on anything.</para></remarks>
public readonly record struct WalkPose(
    float LeftArm, float RightArm, float LeftLeg, float RightLeg,
    float LeftElbow = 0f, float RightElbow = 0f, float LeftKnee = 0f, float RightKnee = 0f,
    float Bob = 0f, float Lean = 0f, float RightArmYaw = 0f, float RightWrist = 0f,
    float TorsoRise = 0f, float TorsoLean = 0f, float LeftArmYaw = 0f, float LeftWrist = 0f,
    float RootPitch = 0f, float RootRoll = 0f)
{
    /// <summary>Every angle zero, no bob, no lean and no torso rise: a body standing still, and what a body
    /// with no cycle of its own draws at.</summary>
    public static WalkPose Rest => default;
}

/// <summary>Where a <see cref="WalkCycle"/> is this frame, shape-free: the phase and the two blends, for a
/// body that turns them into limb angles of its own rather than taking <see cref="WalkCycle.Pose"/>.</summary>
/// <param name="Phase">Where in the cycle the body is, 0 to 1. See <see cref="WalkCycle.Phase"/>.</param>
/// <param name="Weight">How much of the gait is applied, 0 standing to 1 walking. See
/// <see cref="WalkCycle.Weight"/>.</param>
/// <param name="RunWeight">How much of the run is applied, 0 walking to 1 running. See
/// <see cref="WalkCycle.RunWeight"/>.</param>
public readonly record struct GaitSample(float Phase, float Weight, float RunWeight)
{
    /// <summary>A body standing still: no phase, no weight, and what a body with no cycle of its own draws
    /// at.</summary>
    public static GaitSample Still => default;
}

/// <summary>One body's walk, accumulated from the ground it covers rather than from a clock.</summary>
/// <remarks>
/// The bodies are loose rigid pieces with no skin weights, so the cycle is a pure function of DISTANCE
/// TRAVELLED and nothing else. Phase off distance rather than off time is what makes a run look like a run
/// without a second set of numbers: a running body moves twice as fast, so it covers
/// <see cref="StrideMetres"/> in half the time and the legs turn over twice as quickly, for free.
/// <para>Fed the position the body is DRAWN at, which is the presented pose rather than the committed
/// authoritative one, so the swing follows the glide between server answers instead of stepping once per
/// update.</para>
/// <para>The one number that is per BODY is the stride, which the cycle takes from a <see cref="BodyRig"/>:
/// every angle below is a shape and holds at any size, while a stride is a LENGTH. A body at 55 percent of a
/// person's size covers 0.77 m per cycle against 1.4, so crossing the same ground turns its legs over 1.8
/// times as fast, which is most of what a short thing walking looks like.</para>
/// <para>NO CLOCK AND NO TICK COUNT. Everything here is seconds or a normalized phase, so a world that
/// updates a handful of times a second and one that updates every frame drive the same cycle, and neither
/// has to say which it is. <see cref="Advance(float, float, bool, bool)"/> is the path for motion that
/// covers no ground at all.</para>
/// <para>Headlessly testable by construction: no scene, no clock, no renderer.</para>
/// </remarks>
public struct WalkCycle
{
    /// <summary>How far a PERSON walks per full cycle: one metre and a bit, so a body crossing a metre of
    /// ground is most of a stride through and the legs come back to the same place every metre and a half
    /// rather than every metre, which is what stops the walk reading as a march.
    /// <see cref="BodyRig.Human"/> carries this same number, and a cycle that was never handed a rig falls
    /// back to it.</summary>
    public const float StrideMetres = 1.4f;

    /// <summary>How far a HIP swings each way at a full walk, radians. About 34 degrees, which is a stride on
    /// a body of these proportions.</summary>
    public const float SwingRadians = 0.6f;

    /// <summary>How much of the leg's swing an arm takes, as a fraction. Arms swing LESS than legs on a real
    /// walk, and matched amplitudes are most of what makes a cycle read as a marionette: the arm is lighter,
    /// it is counterbalancing rather than carrying, and it hangs from a joint that does not have to clear the
    /// ground.</summary>
    public const float ArmSwingScale = 0.6f;

    /// <summary>How far a knee bends at the peak of a full-speed stride, radians. About 52 degrees, which is
    /// what lifts the foot clear of the ground rather than dragging it through: a rigid leg on a 0.6 rad hip
    /// swing has to skim the floor or hover over it, and the knee is what real walking spends instead.
    /// <para>TURNING THIS DOWN DIGS THE FOOT IN DEEPER, which is the opposite of the obvious guess and the
    /// only reason it is written here. The toe's depth under the knee hinge is <c>-0.28 cos f - 0.135 sin
    /// f</c>, which bottoms out at f = 0.449 rad rather than at zero, so the shipped 0.9 is already PAST the
    /// worst angle and walking back down the curve costs depth: 0.9 to 0.45 takes the deepest the sole
    /// reaches from 2.2 cm under the floor to 3.1. Only a LARGER flex helps, and past about 1.1 the swinging
    /// toe clears the ground entirely and the rigid stance leg becomes the deepest point instead.</para>
    /// </summary>
    public const float KneeFlexRadians = 0.9f;

    /// <summary>The bend an elbow carries the whole time a body is walking, radians. Arms do not hang straight
    /// on anything alive: a constant slight break at the elbow is most of the difference between a person
    /// walking and a mannequin on rails.</summary>
    public const float ElbowBendRadians = 0.3f;

    /// <summary>How much MORE the elbow closes at the front of the arm's swing, radians, on top of
    /// <see cref="ElbowBendRadians"/>. The forward arm folds a little and the trailing one opens out again,
    /// which is the asymmetry that keeps the two arms from reading as one rocking beam.</summary>
    public const float ElbowSwingFlexRadians = 0.25f;

    /// <summary>How far the whole body DROPS between one mid-stance and the next, metres. Twice a stride,
    /// because each leg takes a turn holding the body up. Two centimetres is small on purpose: the eye reads
    /// the rhythm rather than the distance, and a body that visibly pogos is worse than one that does not
    /// bob at all.</summary>
    public const float BobMetres = 0.02f;

    /// <summary>How far a RUNNING body tips forward, radians. About six degrees, which is a lean into the
    /// direction of travel rather than a sprinter's crouch, and it is the one part of the cycle that says
    /// running rather than just faster walking.</summary>
    public const float RunLeanRadians = 0.1f;

    /// <summary>Over this many metres a second the body counts as RUNNING, which is the only thing speed
    /// itself decides (everything else falls out of the distance covered). Sized to sit between a walking
    /// pace and a running one on a human-scaled body, which is about 1.5 m/s against 3.0. A game whose two
    /// speeds straddle some other number reads <see cref="RunWeight"/> off its own state instead, through
    /// <see cref="Advance(float, float, bool, bool)"/>.</summary>
    public const float RunMetresPerSecond = 2.25f;

    /// <summary>How long the swing takes to fade in when a body starts, and out when it stops. Short enough
    /// to read as immediate and long enough that a stop does not freeze a leg mid air.</summary>
    public const float BlendSeconds = 0.15f;

    // Under this many metres a second the body counts as standing. Not zero: a presented pose carries the
    // glide's own float noise, and a body parked in one place would otherwise flicker in and out of walking.
    const float MovingMetresPerSecond = 0.05f;

    Vector3 _last;
    bool _sampled;
    float _phase;
    float _weight;
    float _runWeight;
    float _stride;

    /// <summary>A cycle for one body, turning its legs over at that body's own stride.</summary>
    /// <param name="rig">The body's rig, usually the game's own definition or <see cref="BodyRig.Human"/>.</param>
    public WalkCycle(BodyRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        _stride = rig.StrideMetres;
    }

    /// <summary>How far THIS body walks per cycle, metres. A cycle built with no rig (a <c>default</c>, which
    /// is what a dictionary hands back for a body nobody has classified yet) walks like a person, so the
    /// fallback is a person rather than a divide by zero.</summary>
    public readonly float Stride => _stride > 0f ? _stride : StrideMetres;

    /// <summary>Where in the cycle the body is, 0 to 1. One full cycle is both legs through their whole
    /// swing and back.</summary>
    public readonly float Phase => _phase;

    /// <summary>How much of the swing is applied, 0 standing to 1 walking. This is the blend, and it is what
    /// makes a stop ease out instead of cutting.</summary>
    public readonly float Weight => _weight;

    /// <summary>How much of the run lean is applied, 0 walking to 1 running. Its own blend over the same
    /// <see cref="BlendSeconds"/>, so a body that crosses <see cref="RunMetresPerSecond"/> for one frame (a
    /// correction, a glide seam) tips a few thousandths of a radian rather than snapping upright and back.
    /// </summary>
    public readonly float RunWeight => _runWeight;

    /// <summary>Where the cycle is this frame, as one value a body with a gait of its OWN reads.
    /// <see cref="Pose"/> is the two-legged reading of the same numbers, and a four-legged body takes this
    /// instead and shapes its own (<see cref="QuadrupedGait.PoseAt(QuadrupedRig, in GaitSample)"/>), so
    /// the distance-driven phase and the ease in and out are shared by every body and the limb count is
    /// not.</summary>
    public readonly GaitSample Sample => new(_phase, _weight, _runWeight);

    /// <summary>Advances the cycle by one frame of drawn motion over the GROUND.</summary>
    /// <param name="position">Where the body is being DRAWN this frame, from its presented pose.</param>
    /// <param name="dt">Seconds since the last frame.</param>
    /// <remarks>The first call only samples the position: with nothing to measure against, a body would
    /// otherwise appear to have travelled its whole world coordinate in one frame.
    /// <para>HORIZONTAL distance alone, and the discarded y is the reason the other overload exists: a body
    /// climbing a slope should not turn its legs over for the climb, and a body swimming or falling covers
    /// no ground at all and would stand frozen here.</para></remarks>
    public void Advance(Vector3 position, float dt)
    {
        if (dt < 0f) dt = 0f;
        float travelled = 0f;
        if (_sampled)
        {
            // HORIZONTAL distance alone. A body climbing moves in y as well, and a metre of climb is not a
            // metre of walk.
            float dx = position.X - _last.X;
            float dz = position.Z - _last.Z;
            travelled = MathF.Sqrt(dx * dx + dz * dz);
        }
        _sampled = true;
        _last = position;

        _phase += travelled / Stride;
        _phase -= MathF.Floor(_phase);

        bool moving = dt > 0f && travelled > MovingMetresPerSecond * dt;
        bool running = dt > 0f && travelled > RunMetresPerSecond * dt;
        Blend(dt, moving, running);
    }

    /// <summary>Advances the cycle from an EXPLICIT phase source rather than from ground covered: the path
    /// for motion with no ground contact.</summary>
    /// <param name="phaseDelta">How far round the cycle to turn this frame, as a fraction of a whole cycle.
    /// A swimmer at two strokes a second hands over <c>2 * dt</c>, a body falling with no cycle at all hands
    /// over zero and keeps the legs it had. Negative runs the cycle backward, which is what a backpedal
    /// wants.</param>
    /// <param name="dt">Seconds since the last frame, for the two blends only.</param>
    /// <param name="moving">Whether the body counts as moving this frame, which the caller knows and this
    /// overload cannot work out: a swimmer holding station covers no ground and is still swimming. Drives
    /// <see cref="Weight"/> in and out over <see cref="BlendSeconds"/>, exactly as the ground overload's own
    /// speed test does.</param>
    /// <param name="running">Whether the body counts as running, for <see cref="RunWeight"/> and so for the
    /// run lean. Read off the caller's own state rather than off a speed threshold, because
    /// <see cref="RunMetresPerSecond"/> is a ground speed and there is no ground here.</param>
    /// <remarks>
    /// THE ONE SIDE EFFECT WORTH KNOWING: this FORGETS where the body was, exactly as
    /// <see cref="Teleport"/> does, so the next ground-driven <see cref="Advance(Vector3, float)"/> measures
    /// nothing. Without it a body that swam thirty metres and then waded ashore would hand the ground
    /// overload one frame carrying the whole swim, which reads as a sprint and spins the phase. Alternating
    /// the two overloads is therefore safe by construction and needs no <see cref="Teleport"/> call at the
    /// seam.
    /// <para>The ground overload's behaviour is untouched by this one: the phase, the two blends and the
    /// sampled position are the same fields, so a game that never calls this gets exactly the cycle it
    /// always did.</para>
    /// <para>Why a phase delta rather than a speed: a speed only means anything against a stride, and
    /// nothing with no ground under it has one. A caller that does think in speed converts once, with
    /// <c>metresPerSecond * dt / cycle.Stride</c>, and keeps the per-body scaling <see cref="Stride"/>
    /// already gives it.</para>
    /// </remarks>
    public void Advance(float phaseDelta, float dt, bool moving, bool running = false)
    {
        if (dt < 0f) dt = 0f;
        // The next ground sample measures nothing. See the remark above: the alternative is one frame of
        // travel carrying however far the body swam or fell.
        _sampled = false;

        if (float.IsFinite(phaseDelta))
        {
            _phase += phaseDelta;
            _phase -= MathF.Floor(_phase);
            // The phase is half open, 0 up to but not including 1, and the ground path keeps it that way for
            // free because ground covered is never negative. A delta can be: a tiny negative one floors to -1
            // and the subtraction rounds to exactly 1. Same point on the cycle, so fold it back to 0.
            if (_phase >= 1f) _phase = 0f;
        }

        Blend(dt, moving, running);
    }

    /// <summary>Eases the walk and run weights toward the two flags over <see cref="BlendSeconds"/>. The ONE
    /// copy of the blend rule, shared by both phase sources, so a change to how a body eases in or out of a
    /// gait cannot land on the ground path and miss the no-ground one.</summary>
    void Blend(float dt, bool moving, bool running)
    {
        float step = dt / BlendSeconds;
        _weight = Math.Clamp(moving ? _weight + step : _weight - step, 0f, 1f);
        _runWeight = Math.Clamp(running ? _runWeight + step : _runWeight - step, 0f, 1f);
    }

    /// <summary>Forgets where the body was, so the next <see cref="Advance(Vector3, float)"/> measures
    /// nothing. What a teleport wants: without it the jump reads as a sprint and spins the phase.</summary>
    public void Teleport() => _sampled = false;

    /// <summary>This frame's whole pose: the legs in antiphase with each other, each arm in antiphase with
    /// its OWN side's leg, which is the gait a person actually has (right arm forward with the left leg), the
    /// knees and elbows folding through it, and the body bobbing and leaning over the top. Every number is
    /// zero at rest, so a standing body draws exactly as an unposed one did.</summary>
    /// <remarks>
    /// One angle drives all of it. <c>forward</c> is the right leg's own swing as a fraction, and
    /// <c>opening</c> is its rate of change, which is what says WHICH HALF of the cycle that leg is in:
    /// positive while it is travelling forward (the swing phase, foot in the air) and negative while it is
    /// travelling back (the stance phase, foot on the ground). The other leg reads both negated, being half a
    /// cycle away.
    /// <para>So the knee is a half cosine over the swing half and flat zero through the stance half, peaking
    /// where the leg passes under the body, which is where a real knee is most bent and where the foot most
    /// needs the clearance. Clamped at zero rather than allowed through: a negative flexion is a knee bending
    /// the wrong way, and it is exactly what a plain sine would produce for half of every stride.</para>
    /// <para>The bob rides <c>opening</c> too, at twice the frequency: the body is at full height when a leg
    /// is vertical under it (both are, at the same instant, one passing the other) and at its lowest at
    /// double support, where the legs are split and the hips are further from the floor than the leg is long.
    /// It only ever sinks, because the standing pose is already the tall one.</para>
    /// <para>Nothing here writes <see cref="WalkPose.RootPitch"/> or <see cref="WalkPose.RootRoll"/>: a walk
    /// has ground under it by definition, and a body on its feet does not tip about them.</para>
    /// </remarks>
    public readonly WalkPose Pose
    {
        get
        {
            if (_weight <= 0f) return WalkPose.Rest;
            float theta = _phase * MathF.Tau;
            float forward = MathF.Sin(theta);
            float opening = MathF.Cos(theta);
            float swing = forward * SwingRadians * _weight;
            float arm = swing * ArmSwingScale;
            return new WalkPose(
                LeftArm: arm, RightArm: -arm, LeftLeg: -swing, RightLeg: swing,
                LeftElbow: Elbow(forward), RightElbow: Elbow(-forward),
                LeftKnee: Knee(-opening), RightKnee: Knee(opening),
                Bob: -BobMetres * 0.5f * (1f - MathF.Cos(2f * theta)) * _weight,
                Lean: RunLeanRadians * _runWeight);
        }
    }

    // One arm's elbow, from how far FORWARD that arm is as a fraction of its own amplitude.
    readonly float Elbow(float forward) =>
        (ElbowBendRadians + ElbowSwingFlexRadians * MathF.Max(0f, forward)) * _weight;

    // One knee, from how fast its own leg is opening forward. Zero through the whole stance half.
    readonly float Knee(float opening) => KneeFlexRadians * MathF.Max(0f, opening) * _weight;
}
