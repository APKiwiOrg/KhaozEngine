using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The seam a GAME reaches its own exotic crafting operations through, spec 10.5. The engine ships a closed
/// set of fourteen primitives and a currency composes them in data, so anything outside that list is code
/// the game owns, registered by id through <see cref="CraftingRegistry"/> and reached by a
/// <c>currency_step</c> naming that id.
/// <para>
/// <b>An operation that ROLLS takes its <c>IRandomSource</c> in ITS OWN constructor</b>, per contracts 14.4
/// and for the same reason the generator does. The registry holds INSTANCES rather than types, so the game
/// constructs its operation with the source the process is running on, and an operation with no source in
/// its constructor provably cannot roll. That is why <see cref="Apply"/> has no random parameter, and why
/// adding one would quietly destroy the only check anyone can make from outside.
/// </para>
/// <para>
/// <b>An operation gets the working copy and the same refusal vocabulary, and it gets NO new powers.</b> It
/// cannot write an unregistered property kind, cannot exceed
/// <see cref="ItemInstancePayload.MaxInstancePayloadBytes"/>, cannot produce a non-canonical payload and
/// cannot allocate an instance id, because <see cref="CraftWorkingCopy"/> is a BUILDER rather than a byte
/// array: every write asks the property registry first, the cap is checked at every write, canonical order
/// is the builder's, and there is no allocator anywhere in the type. All four are properties of the type
/// handed in rather than rules an implementer has to remember.
/// </para>
/// <para>
/// <b>An operation is not durable and is not versioned by the pack.</b> A currency row names its id, so an
/// operation this process has no registration for makes that currency refuse with
/// <see cref="CraftRefusalKind.OperationUnregistered"/> at the moment it is USED rather than at boot.
/// </para>
/// </summary>
public interface ICraftOperation
{
    /// <summary>
    /// This operation's number, at or above <see cref="CurrencyStepContentType.FirstGameOperation"/>. It is
    /// durable the moment one currency row names it, so it never moves, and the band is refused by
    /// <see cref="CraftingRegistry.Register"/> itself rather than by a rule in prose.
    /// </summary>
    int Id { get; }

    /// <summary>
    /// Applies the operation to one craft in progress.
    /// </summary>
    /// <param name="copy">The craft in progress, which is where a refusal is recorded and where all four
    /// of the powers above are refused.</param>
    /// <param name="parameters">The step's authored parameters, in authored order, always
    /// <see cref="CraftPlanStep.ParameterCount"/> long with 0 for an unused slot.</param>
    /// <returns>The refusal, or null when the operation applied. A RETURNED refusal is recorded on the
    /// working copy BY THE EXECUTOR, so an operation may answer one without calling
    /// <see cref="CraftWorkingCopy.Refuse"/> first and the craft still refuses whole. Recording it yourself
    /// is still the earlier answer, and the first refusal recorded is the one that stands.</returns>
    CraftRefusal? Apply(ref CraftWorkingCopy copy, ReadOnlySpan<int> parameters);
}
