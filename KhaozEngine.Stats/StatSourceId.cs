namespace KhaozEngine.Stats;

/// <summary>
/// A stable identity for one contributor to a <see cref="StatSet"/>: an equipment slot, a buff, an
/// aura, or anything else the game chooses to key its modifier stacks by. The engine assigns no
/// meaning to <see cref="Value"/> beyond equality. It is an opaque handle the game mints and reuses to
/// replace or remove the modifiers it previously added under the same id.
/// </summary>
/// <param name="Value">The opaque, game-chosen identity value.</param>
public readonly record struct StatSourceId(int Value);
