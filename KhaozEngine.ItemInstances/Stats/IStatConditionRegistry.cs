namespace KhaozEngine.ItemInstances;

/// <summary>
/// Where a GAME answers whether one of its own conditions holds, spec 11.3. It arrives through
/// <see cref="ContentStatEvaluator"/>'s constructor and never through a static or a locator (contracts
/// 14.4), and an evaluator built without one simply drops every conditional line.
/// <para>
/// <b>The engine owns ids 0 to <see cref="EngineBandMaximum"/> and defines NONE of them in v1</b>, so the
/// whole band stays free for a later engine condition and no game id can ever collide with one that has not
/// been written yet. An id in that band never reaches this interface: id
/// <see cref="Unconditional"/> applies always without asking anything, and every other id in the band names
/// a condition that does not exist, so its line does not apply.
/// </para>
/// <para>
/// <b>It is consulted at RECOMPUTE time only.</b> A cached stat value asks nothing, so a condition that
/// reads a moving value is a condition the game must dirty the evaluator on, through
/// <see cref="ContentStatEvaluator.Recompute(in StatSourceKey)"/>. That is the whole reason spec 11.5
/// writes the five dirtying events as a closed list.
/// </para>
/// </summary>
public interface IStatConditionRegistry
{
    /// <summary>The id of a line that is gated by nothing, which applies always and asks no registry.</summary>
    public const int Unconditional = 0;

    /// <summary>The top of the engine's own reserved band. A game registers strictly above this.</summary>
    public const int EngineBandMaximum = 1023;

    /// <summary>
    /// Whether the game condition under that id holds for this evaluation.
    /// </summary>
    /// <param name="conditionId">The id, always above <see cref="EngineBandMaximum"/>.</param>
    /// <param name="context">The evaluation context, whose mask is the usual place an answer comes from.</param>
    /// <returns>True when the lines gated by that condition apply.</returns>
    bool Evaluate(int conditionId, in StatContext context);
}
