namespace KhaozEngine.Skills;

/// <summary>The one experience number that is not a curve's.</summary>
/// <remarks>
/// A ceiling rather than a curve, which is why it is a separate type from <see cref="SkillXpCurve"/>: it is
/// a hard limit on what a saved character can hold rather than a balance number, <see cref="SkillBook"/>
/// saturates at it, <see cref="SkillBookCodec"/> validates against it, and a curve whose top level would not
/// fit under it is a curve a game should refuse at content load.
/// </remarks>
public static class SkillXpLimits
{
    /// <summary>The experience ceiling, past which a gain is dropped rather than accumulated.</summary>
    public const double MaxXp = 200_000_000d;
}
