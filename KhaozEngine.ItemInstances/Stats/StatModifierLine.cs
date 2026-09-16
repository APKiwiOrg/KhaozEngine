namespace KhaozEngine.ItemInstances;

/// <summary>
/// The three ways a modifier combines, contracts 13.2, and exactly three. The numbering is written out
/// rather than left to declaration order because a stat line row stores it (spec 8.4), so inserting a kind
/// in the middle would restate every authored line.
/// </summary>
public enum StatCombineKind : byte
{
    /// <summary>Summed in the stat's scaled units, and a negative value is ordinary.</summary>
    Flat = 1,

    /// <summary>An ADDITIVE basis point pool. Every one is summed, then applied once.</summary>
    Increased = 2,

    /// <summary>A MULTIPLICATIVE basis point factor. Each one applies as its own step.</summary>
    More = 3,
}

/// <summary>
/// One modifier line aimed at one stat, spec 11.2. It is the unit the evaluator folds and the unit
/// <c>InstanceStatLines</c> produces from a payload plus its content.
/// <para>
/// <b><see cref="TagScopeStart"/> and <see cref="TagScopeLength"/> index ONE shared tag array the evaluator
/// owns</b>, not a per line array. A source with eight lines hands over one tag span and eight structs, so
/// adding a worn item allocates twice rather than nine times, and a fold reads the scope as a slice with no
/// indirection. The positions a caller hands in are relative to the tag span it passes to
/// <c>AddSource</c>, and the evaluator rebases them onto its own array.
/// </para>
/// <para>
/// <b>Every number here is an integer and there is no float on this path</b> (contracts 13.4).
/// <see cref="Value"/> is in the stat's scaled units for <see cref="StatCombineKind.Flat"/> and in BASIS
/// POINTS, where 10,000 is 100 percent, for the other two.
/// </para>
/// </summary>
/// <param name="StatId">The <c>stat</c> definition this line moves.</param>
/// <param name="Combine">How the value combines with the rest of the stat's lines.</param>
/// <param name="Value">Scaled units for a flat line, basis points for the other two.</param>
/// <param name="TagScopeStart">Where this line's required tags start in the source's tag span.</param>
/// <param name="TagScopeLength">How many tags the scope requires. Zero applies always.</param>
/// <param name="ConditionId">
/// The condition gating the line, or <see cref="IStatConditionRegistry.Unconditional"/>. Ids up to
/// <see cref="IStatConditionRegistry.EngineBandMaximum"/> are the engine's own band and never reach a
/// registry.
/// </param>
public readonly record struct StatModifierLine(
    int StatId,
    StatCombineKind Combine,
    int Value,
    int TagScopeStart,
    int TagScopeLength,
    int ConditionId);
