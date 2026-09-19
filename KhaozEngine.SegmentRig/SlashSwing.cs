namespace KhaozEngine.SegmentRig;

/// <summary>
/// The swing an ARMED fight draws: a cut on the weapon arm alone, the blade CARRIED at the side between
/// blows and thrown out to the weapon side and across the body into whatever is being hit, edge leading,
/// inside the strike itself.
/// </summary>
/// <remarks>
/// <see cref="AttackSwing"/>'s sibling, deliberately built the same way and deliberately a different shape,
/// which is the pair <see cref="ChopSwing"/> and the punch already are. A punch is a straight jab because a
/// fist is a point: it goes where the arm goes. A blade is a LENGTH, so the stroke that shows it is the one
/// that moves it sideways past the target, and the point covers 0.92 m across the body between the raise and
/// the blow against the fist's own 0.31 over its whole punch.
/// <para>WHICH ONE A BODY THROWS is the GAME's to decide, off whatever is in the hand, and it arrives here
/// as an <see cref="AttackStyle"/>. Nothing else selects it, so a body that picks up a blade changes stroke
/// on the frame the game says it is holding one.</para>
/// <para>THE TIMING IS THE PUNCH'S, exactly: <see cref="AttackTrajectory"/> runs both, so the recovery, the
/// rest and the late fast strike are one set of numbers and a fight reads the same whatever is in the
/// hand.</para>
/// <para>THE CARRY is the reason this stroke has three keys where the punch has two. The arm holds the rest
/// for most of the cadence, so whatever it is doing there is what a fight LOOKS like: held out at the weapon
/// side, that is a blade splayed horizontally for the whole fight. So the rest is the walk's own idle arm
/// with something in the fist (<see cref="RestShoulder"/> and friends), and the going out is folded INSIDE
/// the strike as <see cref="Raise"/>, reached over the first <see cref="RaiseFraction"/> of it. Nothing
/// moves during the hold, so this is still no wind-up: the whole out and across happens in the third of a
/// second before the blow lands.</para>
/// <para>THE ARM AND THE BLADE ARE DIFFERENT ROTATIONS, which is worth reading before touching a number
/// below. A held piece rides the forearm with its own grip orientation ahead of it, and every rotation in
/// that chain but the shoulder's yaw is about the same local x, so they add: the blade points at
/// <c>gripTilt + Wrist - (Shoulder + Elbow)</c> from straight up, measured toward the way the body faces,
/// inside whatever plane the yaw has turned the shoulder to. So the WRIST is what keeps the blade level
/// while the arm swings, and it is large here for that reason rather than because a wrist bends that far: at
/// a grip tilt of 0.95 the sum at the blow is 1.6 rad off vertical, which is the blade held out past the
/// horizontal, at the raise 0.95, and at the carry 0.5, which is the blade standing up and forward out of a
/// hanging fist exactly as a resting body holds it.</para>
/// <para>Headlessly testable by construction, like every other cycle here: no scene, no clock, no
/// renderer.</para>
/// </remarks>
public static class SlashSwing
{
    /// <summary>The weapon shoulder's pitch at the moment the blow lands, radians, positive forward. Lower
    /// than the punch's, because the cut arrives at a body's middle rather than at its face: it puts the fist
    /// at 0.84 m on a 1.5 m body and the blade's point out at 0.99 m in front of the feet.</summary>
    public const float ImpactShoulder = 0.9f;

    /// <summary>The elbow at the blow, radians. Nearly straight, so the cut finishes at full reach, and not
    /// dead straight for <see cref="ChopSwing.ImpactElbow"/>'s reason.</summary>
    public const float ImpactElbow = 0.15f;

    /// <summary>The yaw at the blow, radians, positive being out to the weapon side. NEGATIVE, and that sign
    /// against <see cref="RaiseYaw"/>'s is the whole cut: the arm ends a little past the body's own centre
    /// line, so the blade has crossed in front of the chest rather than stopped beside it.</summary>
    public const float ImpactYaw = -0.25f;

    /// <summary>The wrist at the blow, radians, positive rolling the point the way the body faces. Big, for
    /// the reason in the remarks: it is what holds the blade out past the horizontal on an arm that is only
    /// half raised, so the blade leads the fist through the target instead of trailing behind it.</summary>
    public const float ImpactWrist = 1.7f;

    /// <summary>The weapon shoulder's pitch between blows, radians. Nearly hanging: between blows the blade
    /// is CARRIED, so the arm sits about where a standing body's own idle leaves it and the fighter simply
    /// stands holding the thing.</summary>
    public const float RestShoulder = 0.1f;

    /// <summary>The elbow between blows, radians. A slight bend, the idle's own and no more: the fist hangs
    /// by the hip and the blade takes its resting lean up and forward out of it.</summary>
    public const float RestElbow = 0.35f;

    /// <summary>The yaw between blows, radians, out to the weapon side. Just enough to clear the hip, and
    /// nowhere near <see cref="RaiseYaw"/>: an arm held out at the weapon side for the whole cadence is the
    /// blade splayed horizontally through a fight, which is the regression this number answers.</summary>
    public const float RestYaw = 0.15f;

    /// <summary>The wrist between blows, radians. ZERO, so the blade sits at exactly the lean its own grip
    /// gives it in a resting fist, forward and up, and the carry is the pose a piece is authored around
    /// rather than one held against the grip.</summary>
    public const float RestWrist = 0f;

    /// <summary>How much of the STRIKE is spent reaching <see cref="Raise"/>, the rest of it being the cut
    /// home. Two fifths, so the raise takes about 0.13 s and the cut about 0.19 s at a typical cadence: the
    /// arm comes out and then crosses, and both halves are inside the one third of a second the strike has
    /// always had.</summary>
    public const float RaiseFraction = 0.4f;

    /// <summary>The weapon shoulder's pitch at the raise, radians. Most of the way to the blow's own, because
    /// the arm is up and out by the time the cut starts and the cut itself is across rather than up.</summary>
    public const float RaiseShoulder = 0.75f;

    /// <summary>The elbow at the raise, radians. Still bent, so the blade is cocked ready to be thrown
    /// through the target rather than already at full reach.</summary>
    public const float RaiseElbow = 0.45f;

    /// <summary>The yaw at the raise, radians, out to the weapon side. The far end of the arc, which is what
    /// leaves the whole width of the body for the cut to travel across into
    /// <see cref="ImpactYaw"/>.</summary>
    public const float RaiseYaw = 0.85f;

    /// <summary>The wrist at the raise, radians. Rolled most of the way over, so the blade is already level
    /// when the cut starts and the ARM does the cutting from there.</summary>
    public const float RaiseWrist = 1.2f;

    /// <summary>The arm at the blow, as the four channels.</summary>
    public static AttackPose Impact => new(ImpactShoulder, ImpactElbow, ImpactYaw, ImpactWrist, true);

    /// <summary>The arm between blows, which is where it sits through the middle of every cadence.</summary>
    public static AttackPose Rest => new(RestShoulder, RestElbow, RestYaw, RestWrist, false);

    /// <summary>The arm at the raise, the key the cut is thrown from, reached inside the strike itself.
    /// </summary>
    public static AttackPose Raise => new(RaiseShoulder, RaiseElbow, RaiseYaw, RaiseWrist, false);

    /// <summary>The weapon arm at a point in the stroke.</summary>
    /// <param name="phase">Where in the stroke, 0 to 1, with 0 and 1 both the impact. Values outside are
    /// wrapped, so a caller may hand over a raw accumulator.</param>
    public static AttackPose PoseAt(float phase) =>
        AttackTrajectory.PoseAt(phase, Impact, Rest, Raise, RaiseFraction);

    /// <summary>Lays a cut over a walk pose on the weapon arm alone, <see cref="AttackSwing.Compose"/>'s
    /// contract exactly and its code: the two styles differ in the POSE they reach, never in which channels
    /// they are allowed to write, so the composition is shared rather than copied.</summary>
    /// <param name="walk">The body's own pose this frame, from <c>WalkCycle.Pose</c>.</param>
    /// <param name="swing">The stroke, from <see cref="PoseAt"/>.</param>
    /// <param name="weight">How much of the stroke is applied, 0 to 1. Values outside are clamped.</param>
    public static WalkPose Compose(in WalkPose walk, in AttackPose swing, float weight) =>
        AttackSwing.Compose(walk, swing, weight);
}
