namespace KhaozEngine.SegmentRig;

/// <summary>Which stroke a body throws in a fight.</summary>
public enum AttackStyle : byte
{
    /// <summary>A bare fist: <see cref="AttackSwing"/>. The default, and what a body with nothing in its
    /// hand throws.</summary>
    Punch = 0,

    /// <summary>A weapon with a length in it: <see cref="SlashSwing"/>.</summary>
    Slash = 1,
}

/// <summary>Which of the two armed and unarmed strokes a body is drawn throwing.</summary>
/// <remarks>
/// The punch is for an EMPTY HAND. A body swinging a length of steel with a jab reads as a body that has
/// forgotten it is armed, so whatever is in the hand picks the stroke.
/// <para>WHAT PICKS THE STYLE IS THE GAME'S, and it is not here. Reading an equipped item, a weapon family
/// or a creature kind is a content question, so a consumer answers it on its own side and hands over the
/// <see cref="AttackStyle"/>. The same goes for the CADENCE: every seconds-taking member in this package
/// takes it as a <c>float</c> parameter, so a game that derives one from its own update rate keeps that
/// arithmetic behind the seam.</para>
/// </remarks>
public static class AttackStyles
{
    /// <summary>The weapon arm at a point in one style's stroke, which is the one call a caller drawing a
    /// body needs: the style picks the curve and the phase runs the same way through either.</summary>
    /// <param name="style">The stroke, which the game selects off whatever is in the hand.</param>
    /// <param name="phase">Where in it, 0 to 1, with 0 and 1 both the impact.</param>
    public static AttackPose PoseAt(AttackStyle style, float phase) =>
        style == AttackStyle.Slash ? SlashSwing.PoseAt(phase) : AttackSwing.PoseAt(phase);
}
