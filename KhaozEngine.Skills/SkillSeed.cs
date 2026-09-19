namespace KhaozEngine.Skills;

/// <summary>One skill a fresh character does not start at level one in.</summary>
/// <remarks>A LEVEL rather than an experience number, because a seed is a design statement ("a character
/// starts able to take ten hits") and the number that buys that level moves with the curve. It is priced
/// through the curve at the moment the book is made, so a curve change reprices every fresh character
/// automatically and <see cref="SkillXpRescale"/> carries the existing ones.</remarks>
/// <param name="Skill">The skill index seeded.</param>
/// <param name="Level">The level it starts at, clamped to the curve's own range.</param>
public readonly record struct SkillSeed(int Skill, int Level);
