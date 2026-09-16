using System;
using KhaozEngine.Catalog;
using KhaozEngine.Primitives;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The ONE place a target guard set, the ordered steps, each step's guard set and a game operation are
/// composed. Spec 10 gives the primitives, the guards, the plan and the registry, and never says what runs
/// them, so this type is named here rather than there and the choice is recorded with it.
/// <para>
/// <b>It holds the <c>IRandomSource</c> by CONSTRUCTOR for the same reason <see cref="ItemGenerator"/>
/// does</b> (contracts 14.4, spec 9.1): a type with no source in its constructor provably cannot roll, and
/// that property is only worth anything if nothing anywhere takes one as a method argument. The generator
/// this executor drives for <c>AddRandomMod</c>, <c>RerollMods</c> and the <c>RandomOfKind</c> selector is
/// built HERE, over that SAME source, so a craft's draws all come off one stream and a seeded session
/// replays.
/// </para>
/// <para>
/// <b>Its generator is built over an allocator that refuses every request.</b> A craft rewrites the payload
/// of an item that already exists, so it never mints an instance id, and the only thing in
/// <see cref="ItemGenerator"/> that asks for one is <c>Generate</c>, which nothing here calls. The refusing
/// store is the standing guard on that: the day a craft path reaches for a whole rolled item, it throws
/// rather than quietly handing out ids from a node nobody reserved.
/// </para>
/// <para>
/// <b>It is NOT reentrant and it is NOT thread safe. One instance per thread, or per craft loop.</b> That
/// is inherited rather than chosen: the generator it owns is not reentrant either, because every working
/// array in it is instance state that one roll overwrites and reads back inside one call. A second
/// <see cref="Apply"/> that starts while one is running hands the outer craft the inner craft's pool.
/// Shared IMMUTABLE tables across threads are the supported shape, so build an executor each.
/// </para>
/// <para>
/// <b>An unregistered game operation refuses AT USE, with a counter</b> (spec 10.5, OWNER DECISION 7 and
/// open question 8). The fail-closed rule of contracts 10.5 is about a missing CONTENT VERSION rather than
/// about a code registration: a server that shipped without an operation is a DEPLOY MISMATCH, which is
/// loud at the first use and would be a boot failure for every player if it were fail-closed at boot.
/// <see cref="UnregisteredOperationRefusals"/> is what a host reads to see it happening.
/// </para>
/// </summary>
public sealed class CraftExecutor
{
    /// <summary>The scratch one selector resolves into, which is kind 131's whole byte-counted ceiling.</summary>
    const int SelectionScratch = CraftWorkingCopy.MaxAffixes;

    readonly IContentSnapshot _snapshot;
    readonly CraftingRegistry _operations;
    readonly IRandomSource _random;
    readonly ItemGenerator _generator;

    /// <summary>
    /// Builds an executor over one content version's tables, one operation registry and one random source.
    /// </summary>
    /// <param name="snapshot">The content version the guards and the primitives read. It is the version the
    /// working copies handed to <see cref="Apply"/> were opened over.</param>
    /// <param name="tables">One version's generation tables, built once at boot and immutable afterwards.
    /// <b>Spec 10 wrote this argument as <c>ModCandidateTables</c></b>, and the shipped generator takes
    /// <see cref="GenerationTables"/> instead, which carries the candidates PLUS the content fold and the
    /// run ceiling. Taking the narrow one here would mean re-folding the content per executor, which is the
    /// cost <see cref="GenerationTables"/> exists to remove.</param>
    /// <param name="operations">The game operations this process registered, frozen by the first pack
    /// load.</param>
    /// <param name="random">The gameplay randomness seam, which the generator below shares.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public CraftExecutor(
        IContentSnapshot snapshot,
        GenerationTables tables,
        CraftingRegistry operations,
        IRandomSource random)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(random);

        _snapshot = snapshot;
        _operations = operations;
        _random = random;
        _generator = new ItemGenerator(
            tables,
            random,
            new InstanceIdAllocator(new RefusingInstanceIdStore(), RefusingInstanceIdStore.LiveEpoch));
    }

    /// <summary>
    /// How many times a step named a game operation this process has no registration for. A host reads it
    /// as telemetry, because the number going above 0 is a deploy mismatch rather than a player doing
    /// something odd.
    /// </summary>
    public int UnregisteredOperationRefusals { get; private set; }

    /// <summary>
    /// Runs one currency against one target: the target guard set, then every step in authored order with
    /// its own guard set evaluated immediately before it.
    /// </summary>
    /// <param name="plan">The currency, resolved at boot by <see cref="CraftPlanIndex"/>.</param>
    /// <param name="copy">The craft in progress. A refusal leaves it refused, and the caller's durable
    /// bytes are untouched either way, because a working copy never patches a stored byte.</param>
    /// <returns>What ran, what was skipped, and why the craft refused when it did.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    /// <exception cref="ArgumentException">The plan carries more steps than
    /// <see cref="CraftOutcome.MaxMaskedSteps"/>, which no published currency can, because v1 caps a
    /// currency at <see cref="CraftingCurrencyContentType.MaxSteps"/>.</exception>
    public CraftOutcome Apply(in CraftPlan plan, ref CraftWorkingCopy copy)
    {
        ArgumentNullException.ThrowIfNull(plan);

        ReadOnlySpan<CraftPlanStep> steps = plan.Steps.Span;
        if (steps.Length > CraftOutcome.MaxMaskedSteps)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"A plan of {steps.Length} steps cannot be reported on, because an outcome answers about {CraftOutcome.MaxMaskedSteps} and v1 caps a currency at {CraftingCurrencyContentType.MaxSteps}."),
                nameof(plan));
        }

        if (copy.IsRefused)
        {
            return new CraftOutcome(copy.Refusal, steps.Length, 0, 0);
        }

        // Standing rule 1 ahead of the first writing step. The working copy checks the same thing at every
        // write, so this is the EARLY answer rather than the only one, and it is what makes a corrupted
        // item refuse at the ask instead of at whatever write the first step happened to end in.
        if (CraftStandingRules.RefuseCorrupted(ref copy) is CraftRefusal corrupted)
        {
            return new CraftOutcome(corrupted, steps.Length, 0, 0);
        }

        if (CraftGuardEvaluator.EvaluateSet(CraftGuardScope.Target, plan.TargetGuards.Span, ref copy, _snapshot, out _)
            == CraftGuardOutcome.Refused)
        {
            return new CraftOutcome(copy.Refusal, steps.Length, 0, 0);
        }

        uint ran = 0;
        uint skipped = 0;
        for (int index = 0; index < steps.Length; index++)
        {
            CraftGuardOutcome guarded = CraftGuardEvaluator.EvaluateSet(
                CraftGuardScope.Step,
                steps[index].Guards.Span,
                ref copy,
                _snapshot,
                out _);

            if (guarded == CraftGuardOutcome.Skipped)
            {
                // A step guard failing SKIPS that one step and the craft continues, which is the whole
                // difference between it and a target guard.
                skipped |= 1u << index;
                continue;
            }

            if (guarded == CraftGuardOutcome.Refused || Run(steps[index], ref copy) is not null)
            {
                return new CraftOutcome(copy.Refusal, steps.Length, ran, skipped);
            }

            ran |= 1u << index;
        }

        return new CraftOutcome(default, steps.Length, ran, skipped);
    }

    /// <summary>One step, which is a primitive the engine owns or an operation the game registered.</summary>
    CraftRefusal? Run(CraftPlanStep step, ref CraftWorkingCopy copy)
    {
        if (!step.IsGameOperation)
        {
            return RunPrimitive(step, ref copy);
        }

        if (_operations.TryGet(step.Operation, out ICraftOperation? operation))
        {
            // A RETURNED refusal is seated on the copy here. The loop stops on the return value either way,
            // but the outcome and the encode are built from the COPY, so an operation that answered a
            // refusal without recording it would otherwise report a craft that succeeded and hand back the
            // half applied payload. Refuse keeps the first refusal, so an operation that did record its own
            // keeps it.
            return operation.Apply(ref copy, step.Parameters.Span) is CraftRefusal refusal
                ? copy.Refuse(refusal)
                : null;
        }

        UnregisteredOperationRefusals++;
        return copy.Refuse(new CraftRefusal(CraftRefusalKind.OperationUnregistered, step.Operation));
    }

    /// <summary>
    /// The parameter map of spec 10.2, which lives HERE and nowhere else, so no two readers can drift apart
    /// on what a step's four integers mean.
    /// <para>
    /// <b>A SELECTOR occupies two slots, its kind then its parameter</b>, for primitives 2, 3 and 10. A
    /// boolean occupies one and is any non-zero value, which is how <c>SetRarity</c> is told to fill,
    /// <c>SetQuality</c> is told its value is an absolute rather than a delta, and <c>SetFlag</c> is told
    /// which way to write the bit.
    /// </para>
    /// <para>
    /// <b>Primitives 7 and 8 are refused from a plan</b>, because they are the only two that touch TWO
    /// slots and the second slot is an ITEM the caller holds. A <c>currency_step</c> row carries four
    /// integers and no item, so a plan cannot name one, and a step reaching for either is refused at use
    /// rather than silently doing nothing. Spec 10.2 writes the parameters as "socket index, source slot"
    /// and a slot is a container position this type has no access to by design.
    /// </para>
    /// </summary>
    CraftRefusal? RunPrimitive(CraftPlanStep step, ref CraftWorkingCopy copy)
    {
        Span<int> selection = stackalloc int[SelectionScratch];
        int count;
        switch (step.Primitive)
        {
            case CraftPrimitive.AddRandomMod:
                return CraftPrimitives.AddRandomMod(ref copy, _generator, step.ParameterA, step.ParameterB);

            case CraftPrimitive.RemoveMod:
                return Select(ref copy, step, InstancePropertyKind.Affixes, selection, out count)
                    ?? CraftPrimitives.RemoveMod(ref copy, selection[..count]);

            case CraftPrimitive.RerollValues:
                return Select(ref copy, step, InstancePropertyKind.Affixes, selection, out count)
                    ?? CraftPrimitives.RerollValues(ref copy, _random, selection[..count]);

            case CraftPrimitive.RerollMods:
                return CraftPrimitives.RerollMods(ref copy, _generator, unchecked((uint)step.ParameterA));

            case CraftPrimitive.SetRarity:
                return CraftPrimitives.SetRarity(ref copy, _generator, step.ParameterA, step.ParameterB != 0);

            case CraftPrimitive.AddSocket:
                return CraftPrimitives.AddSocket(ref copy, step.ParameterA);

            case CraftPrimitive.Socket:
            case CraftPrimitive.Unsocket:
                return copy.Refuse(new CraftRefusal(CraftRefusalKind.StepNeedsSecondSlot, step.Operation));

            case CraftPrimitive.ApplyEnchant:
                return CraftPrimitives.ApplyEnchant(ref copy, step.ParameterA, step.ParameterB);

            case CraftPrimitive.RemoveEnchant:
                return Select(ref copy, step, InstancePropertyKind.Enchantments, selection, out count)
                    ?? CraftPrimitives.RemoveEnchant(ref copy, selection[..count]);

            case CraftPrimitive.Repair:
                return CraftPrimitives.Repair(ref copy, step.ParameterA);

            case CraftPrimitive.SetQuality:
                return CraftPrimitives.SetQuality(ref copy, step.ParameterA, step.ParameterB != 0);

            case CraftPrimitive.Identify:
                return CraftPrimitives.Identify(ref copy);

            case CraftPrimitive.SetFlag:
                return CraftPrimitives.SetFlag(ref copy, step.ParameterA, step.ParameterB != 0);

            default:
                // Unreachable through a resolved plan, because CraftPlanStep refuses the gap on the way in.
                return copy.Refuse(new CraftRefusal(CraftRefusalKind.OperationUnregistered, step.Operation));
        }
    }

    /// <summary>
    /// One selector, resolved into indexes, which is the only shape a primitive takes. The draw a
    /// <c>RandomOfKind</c> makes goes through the EXECUTOR's source, so a currency's draw count stays a
    /// function of its step list.
    /// </summary>
    CraftRefusal? Select(
        ref CraftWorkingCopy copy,
        CraftPlanStep step,
        ushort entryKind,
        scoped Span<int> selection,
        out int count)
        => new CraftSelector((CraftSelectorKind)step.ParameterA, step.ParameterB)
            .Resolve(ref copy, entryKind, _random, selection, out count);

    /// <summary>
    /// The durable half of the executor's allocator, which REFUSES every request. A craft never mints an
    /// instance id, so the allocator exists only because <see cref="ItemGenerator"/>'s constructor takes
    /// one, and the honest thing to hand it is a store that cannot serve.
    /// <para>
    /// It refuses through the epoch rather than through a throw in <see cref="Read"/>, because the
    /// allocator reads its store in its own constructor and a throw there would be a broken executor rather
    /// than a guarded one. A persisted mark under an epoch the allocator was not booted on is exactly the
    /// state spec 3.6 refuses to issue under, so every <c>Next</c> throws and nothing is ever written.
    /// </para>
    /// </summary>
    sealed class RefusingInstanceIdStore : IInstanceIdStore
    {
        /// <summary>The epoch the persisted mark names.</summary>
        internal const long PersistedEpoch = 1;

        /// <summary>The epoch the allocator is booted on, which is deliberately not the persisted one.</summary>
        internal const long LiveEpoch = 0;

        /// <inheritdoc />
        public InstanceIdState Read() => new(InstanceIdAllocator.Pack(0, 1), 0, PersistedEpoch, default);

        /// <inheritdoc />
        public void Persist(in InstanceIdState state)
            => throw new InvalidOperationException(
                "A craft never allocates an instance id, so this store is never written. Reaching it means a craft path asked the generator for a whole rolled item, which is a bug in that path rather than in the store.");
    }
}
