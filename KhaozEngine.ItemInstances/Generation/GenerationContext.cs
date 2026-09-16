namespace KhaozEngine.ItemInstances;

/// <summary>
/// What a caller asks <see cref="ItemGenerator"/> for, spec 9.4 verbatim. It says WHAT to roll and never
/// HOW: there is no seed, no draw index and no source here, because the generator holds its
/// <c>IRandomSource</c> in its constructor and a context carrying one would put gameplay randomness back on
/// the method (contracts 14.4).
/// <para>
/// <b>It does not decide WHICH base drops.</b> That is a loot table, which is Scope A's engine-range
/// content type and is <c>ServerOnly</c>. A loot roll and an affix roll are different questions and the
/// seam between them is deliberate (spec 9.1).
/// </para>
/// </summary>
/// <param name="BaseId">The <c>item</c> row the item is made from. It carries the authored tag list the
/// whole roll is keyed by, so a base this version has no LIVE row for is refused at the door.</param>
/// <param name="ItemLevel">The level the drop rolls at, 1 to 65535, which selects the band and therefore
/// which tiers are live.</param>
/// <param name="ForcedRarityId">A <c>rarity_rule</c> row to seat rather than roll, or 0 to roll one
/// against the base's tags. A forced rarity consumes NO draw, which is why two items are only comparable
/// on their draw count when both were rolled the same way.</param>
/// <param name="ForcedUniqueTemplateId">A <c>unique_template</c> row to seat, or 0 for an ordinary roll. A
/// unique is FORCED by the caller rather than rolled here, because deciding that a unique drops is the
/// loot table's job, and a forced unique draws NOTHING at all.</param>
/// <param name="Quality">Whole percentage points, 0 to 65535. Zero writes no field.</param>
public readonly record struct GenerationContext(
    int BaseId,
    int ItemLevel,
    int ForcedRarityId,
    int ForcedUniqueTemplateId,
    int Quality);
