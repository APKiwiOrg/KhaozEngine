using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The game operation registry of spec 10.5: registration once at process start, the freeze at the first
/// pack load, a later registration that throws, and an operation whose <see cref="ICraftOperation.Id"/> is
/// below <see cref="CurrencyStepContentType.FirstGameOperation"/> refused by the registry ITSELF rather
/// than by a rule written in prose.
/// <para>
/// <b>It is a map keyed by operation id and it is NOT a static</b>, following
/// <see cref="InstancePropertyRegistry"/> and <c>ContentTypeRegistry</c> one level up. Per instance,
/// deliberately: a static registry would be process-global state every crafting test writes, which forces a
/// <c>DisableParallelization</c> collection on the whole assembly and turns every craft fact into a
/// serialization point. Per instance costs one constructor argument on <see cref="CraftExecutor"/> and
/// keeps the suite parallel, which is the #349 rule applied before it bites.
/// </para>
/// <para>
/// <b>Every refusal here is a THROW at startup rather than a silent acceptance</b>, because each one is a
/// programming error in a registration and none of them is data. An operation a currency NAMES and this
/// process never registered is the opposite case and is not this type's: it is data, it is a deploy
/// mismatch, and it refuses AT USE through <see cref="CraftRefusalKind.OperationUnregistered"/>.
/// </para>
/// <para>
/// <b>The freeze is the first PACK LOAD, which is <see cref="CraftPlanIndex.Build"/>.</b> A currency row is
/// resolved into a plan at that moment, so a registration arriving afterwards would be an operation some
/// plans could reach and others could not, depending on when the host got round to it.
/// </para>
/// </summary>
public sealed class CraftingRegistry
{
    readonly SortedDictionary<int, ICraftOperation> _byId = new();
    ICraftOperation[]? _sorted;

    /// <summary>True once the first pack has loaded, after which a registration throws.</summary>
    public bool IsFrozen { get; private set; }

    /// <summary>How many operations this registry holds.</summary>
    public int Count => _byId.Count;

    /// <summary>Every registered operation, sorted ASCENDING by id, always.</summary>
    public IReadOnlyList<ICraftOperation> ById => _sorted ??= [.. _byId.Values];

    /// <summary>
    /// Registers one game operation. Runs ONCE, at process start, before any pack is loaded.
    /// </summary>
    /// <param name="operation">The operation, whose <see cref="ICraftOperation.Id"/> is the durable number
    /// a <c>currency_step</c> names.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The registry is already frozen.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The id is below
    /// <see cref="CurrencyStepContentType.FirstGameOperation"/>, which is the engine's own band.</exception>
    /// <exception cref="ArgumentException">The id is already taken.</exception>
    public void Register(ICraftOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (IsFrozen)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"The crafting operation registry is frozen, so operation {operation.Id} cannot register. Registration runs once at process start, before the first pack loads and therefore before any currency row is resolved."));
        }

        if (!CurrencyStepContentType.IsGameOperation(operation.Id))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation.Id,
                FormattableString.Invariant(
                    $"A game operation id starts at {CurrencyStepContentType.FirstGameOperation}. Everything below it belongs to the engine: {CurrencyStepContentType.MinPrimitiveOperation} to {CurrencyStepContentType.MaxPrimitiveOperation} is one of the fourteen primitives, and the gap above them is refused on both sides of the step codec."));
        }

        if (_byId.TryGetValue(operation.Id, out ICraftOperation? held))
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Operation {operation.Id} is already registered as {held.GetType().Name}. An id is never unregistered and an operation is never replaced, because a currency row names the number and every pack that names it means the same thing."),
                nameof(operation));
        }

        _byId.Add(operation.Id, operation);
        _sorted = null;
    }

    /// <summary>
    /// Freezes the registry, which the pack load path does. Idempotent, so a second boot path calling it is
    /// not an error, and lookup keeps answering afterwards.
    /// </summary>
    public void Freeze() => IsFrozen = true;

    /// <summary>Looks one operation up by the id a <c>currency_step</c> named.</summary>
    /// <param name="id">The operation number off the step row.</param>
    /// <param name="operation">The operation, when this process registered one under that id.</param>
    public bool TryGet(int id, [MaybeNullWhen(false)] out ICraftOperation operation)
        => _byId.TryGetValue(id, out operation);
}
