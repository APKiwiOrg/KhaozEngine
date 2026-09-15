using System.Collections.Generic;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// The DURABLE names an item operation writes into the journal: the action kind a commit is admitted under
/// and the event types its receipt carries.
/// <para>
/// <b>A durable string is never renamed and never switched on</b>, which is the rule
/// <c>ProcessingActionKinds</c> states and the reason these are constants rather than an enum: the string is
/// what is stored, an enum member is a number that means whatever this build's ordering says, and the two
/// disagree the first time a member is inserted.
/// </para>
/// <para>
/// <b>The payload codecs are NOT here yet, deliberately.</b> Spec 9.5's <c>item-generated</c> body and spec
/// 10.6's <c>item-crafted</c> body are the generator's and the crafting framework's, and both arrive with the
/// thing that writes them. Writing a codec for an event nothing emits would be a format frozen before its
/// first caller, which is exactly what the payload work was careful not to do. What the names buy now is that
/// the commit builder and the load path spell them the same way.
/// </para>
/// </summary>
public static class ItemInstanceEvents
{
    /// <summary>
    /// Spec 10.6's action kind for a craft, which is what the journal admits the operation under and what its
    /// normalized intent is hashed beneath.
    /// </summary>
    public const string CraftActionKind = "item-craft";

    /// <summary>
    /// Spec 9.5's event type, written on the commit that first seats a generated item somewhere durable. It
    /// records the RESOLVED item and never a seed, a state or a draw index, because a journal replay returns
    /// the original receipt rather than re-running anything.
    /// </summary>
    public const string Generated = "item-generated";

    /// <summary>
    /// Spec 10.6's event type for a craft, which carries the payload BEFORE and AFTER. The page is rewritten
    /// whole on the next commit, so without the before bytes nothing durable can answer what a craft changed.
    /// </summary>
    public const string Crafted = "item-crafted";

    /// <summary>Every event type this document defines, in the order the spec defines them.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Generated, Crafted };
}
