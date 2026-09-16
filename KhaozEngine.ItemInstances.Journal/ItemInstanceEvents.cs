using System;
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

    /// <summary>Spec 6.4's event for a relocated entry, whether inside one container or across two on one
    /// stream.</summary>
    public const string Moved = "item-moved";

    /// <summary>Units of a plain stack leaving for an empty slot of the same container.</summary>
    public const string StackSplit = "stack-split";

    /// <summary>
    /// Spec 4.6's event for a merge, which names BOTH instance ids and the resulting count. A merge is the
    /// one operation that DESTROYS an instance id, and this event is the whole mitigation: the destroyed id
    /// stays answerable from the journal for the retention window.
    /// </summary>
    public const string StackMerged = "stack-merged";

    /// <summary>An entry arriving in a container, which is the durable half of a grant.</summary>
    public const string Granted = "item-granted";

    /// <summary>Units leaving a slot, which is the half of a withdraw the source stream owns.</summary>
    public const string Taken = "item-taken";

    /// <summary>Every event type this package defines, in the order the spec defines them.</summary>
    public static IReadOnlyList<string> All { get; } =
        new[] { Generated, Crafted, Moved, StackSplit, StackMerged, Granted, Taken };

    /// <summary>
    /// The durable event type one container operation writes. It is a switch on a KIND rather than on a
    /// stored string, which is the direction the no-switching rule allows: the number is this build's and
    /// the string is the durable one.
    /// </summary>
    /// <param name="kind">The operation kind.</param>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not an operation kind.</exception>
    public static string EventTypeOf(ContainerOperationKind kind) => kind switch
    {
        ContainerOperationKind.Move => Moved,
        ContainerOperationKind.Split => StackSplit,
        ContainerOperationKind.Merge => StackMerged,
        ContainerOperationKind.Grant => Granted,
        ContainerOperationKind.Take => Taken,
        ContainerOperationKind.Craft => Crafted,
        _ => throw new ArgumentException(
            FormattableString.Invariant($"{kind} is not an operation kind, so it writes no event."), nameof(kind)),
    };
}
