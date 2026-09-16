using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using KhaozEngine.Catalog;
using static KhaozEngine.ItemInstances.InstanceContentChecks;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The boot-side half of crafting: the <see cref="IContentLoadIndex"/> a host registers against the
/// <c>crafting_currency</c> type, so every currency is resolved into an immutable <see cref="CraftPlan"/>
/// once at boot step 7b with no second pass wired anywhere.
/// <para>
/// It reads its OWN rows plus <c>currency_step</c> and <c>currency_guard</c> through the snapshot it is
/// handed, which the interface explicitly permits, and reads NO other index. It THROWS to fail the boot
/// closed rather than handing back a partial plan set, which the runtime turns into a
/// <see cref="ContentLoadIndexException"/> naming the type.
/// </para>
/// <para>
/// <b>A currency resolves ONCE, at boot, and never inside a tick</b>, which is the same argument the
/// candidate tables make: a lazy build inside a tick is a latency spike, and a plan derived from content is
/// immutable for the version. A new content version becomes active at server RESTART, so this builds no
/// swap and a second <see cref="Build"/> on the same instance is refused.
/// </para>
/// <para>
/// <b>Everything it throws for is something the <c>KEC0100</c> band already refuses at publish.</b> A
/// duplicate <c>sort</c> within one currency is <c>KEC0110</c>, a guard reaching into another currency's
/// steps is <c>KEC0100</c>, and an operation in the dead gap between the primitives and the game band is
/// refused on both sides of the step row codec. Resolution is the SECOND door rather than the only one,
/// because the alternative to throwing is picking one of two orders or seating a guard on nothing, and both
/// are a craft that quietly does something other than its rows say.
/// </para>
/// <para>
/// <b>An operation the process has no registration for is NOT one of those.</b> That is spec 10.5's
/// deliberate softening and OWNER DECISION 7: a missing registration is a deploy mismatch rather than a
/// missing content version, so it refuses at the first USE with
/// <see cref="CraftRefusalKind.OperationUnregistered"/> instead of taking a server down at boot for one
/// unusable currency row. Nothing here looks an operation up.
/// </para>
/// <para>
/// What this DOES do to the registry it is handed is FREEZE it, because the first pack load is the moment
/// after which no game code may register: a plan resolved before a registration and a plan resolved after
/// one would reach different operations for the same authored row.
/// </para>
/// </summary>
/// <param name="operations">The game operation registry to freeze at the first pack load, or null for a
/// process that registers none.</param>
public sealed class CraftPlanIndex(CraftingRegistry? operations = null) : IContentLoadIndex
{
    Dictionary<int, CraftPlan>? _plans;

    /// <inheritdoc />
    public ContentTypeId Type => new(InstanceContentTypeIds.CraftingCurrencyTypeId);

    /// <summary>Every resolved plan, keyed by its <c>crafting_currency</c> row id.</summary>
    /// <exception cref="InvalidOperationException">The boot has not built them, or its build failed.</exception>
    public IReadOnlyDictionary<int, CraftPlan> Plans => Built;

    /// <summary>One currency's plan, or false for a currency this version carries no live row for.</summary>
    /// <param name="currencyId">The <c>crafting_currency</c> row id.</param>
    /// <param name="plan">The plan, when this version carries that currency.</param>
    /// <exception cref="InvalidOperationException">The boot has not built them, or its build failed.</exception>
    public bool TryGetPlan(int currencyId, [MaybeNullWhen(false)] out CraftPlan plan)
        => Built.TryGetValue(currencyId, out plan);

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="InvalidOperationException">This index is built already, or the version carries a
    /// currency the publish band should have refused.</exception>
    public void Build(IContentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_plans is not null)
        {
            throw new InvalidOperationException(
                "The craft plans are built already. A process holds exactly one plan set, built at boot, because a new content version becomes active at server restart rather than through a swap.");
        }

        Dictionary<int, List<ContentRow>> stepsByCurrency = ReadSteps(snapshot);
        Dictionary<int, List<ContentRow>> targetGuards = [];
        Dictionary<int, List<ContentRow>> guardsByStep = ReadGuards(snapshot, stepsByCurrency, targetGuards);

        var plans = new Dictionary<int, CraftPlan>(stepsByCurrency.Count);
        foreach (ContentRow currency in LiveRows(snapshot, InstanceContentTypeIds.CraftingCurrencyTypeId))
        {
            plans.Add(currency.Id, Resolve(currency, stepsByCurrency, guardsByStep, targetGuards));
        }

        _plans = plans;

        // The first pack load is the freeze, because every plan above is now resolved against whatever the
        // process registered, and a later registration would make that answer depend on timing.
        operations?.Freeze();
    }

    /// <summary>The built plans, or the refusal a caller asking too early has earned.</summary>
    Dictionary<int, CraftPlan> Built => _plans ?? throw new InvalidOperationException(
        "The craft plans were asked for before boot step 7b built them, or after a build that failed. They are built ONCE, at boot, and a failed build leaves nothing behind on purpose.");

    /// <summary>
    /// Every live step row, grouped by the currency it belongs to and checked for the two things the
    /// publish band already refuses: an orphaned parent and two steps of one currency sharing a
    /// <c>sort</c>.
    /// </summary>
    static Dictionary<int, List<ContentRow>> ReadSteps(IContentSnapshot snapshot)
    {
        var byCurrency = new Dictionary<int, List<ContentRow>>();
        var sortsTaken = new HashSet<(int Currency, long Sort)>();

        foreach (ContentRow step in LiveRows(snapshot, InstanceContentTypeIds.CurrencyStepTypeId))
        {
            int currencyId = (int)(Number(step, CurrencyStepContentType.CraftingCurrencyIdIndex) ?? 0);
            if (!IsLive(snapshot, InstanceContentTypeIds.CraftingCurrencyTypeId, currencyId))
            {
                throw Closed(FormattableString.Invariant(
                    $"Step {step.Id} names currency {currencyId}, which this version carries no live row for. That is KEC0100 at publish, so a pack carrying it was never validated."));
            }

            long sort = Number(step, CurrencyStepContentType.SortIndex) ?? 0;
            if (!sortsTaken.Add((currencyId, sort)))
            {
                throw Closed(FormattableString.Invariant(
                    $"Step {step.Id} shares sort {sort} with another step of currency {currencyId}. That is KEC0110 at publish, and resolution refuses rather than picking one of the two orders."));
            }

            long operation = Number(step, CurrencyStepContentType.OperationIndex) ?? 0;
            if (!CurrencyStepContentType.IsPrimitive(operation) && !CurrencyStepContentType.IsGameOperation(operation))
            {
                throw Closed(FormattableString.Invariant(
                    $"Step {step.Id} names operation {operation}, which is neither one of the {CurrencyStepContentType.MaxPrimitiveOperation} primitives nor a game operation at or above {CurrencyStepContentType.FirstGameOperation}. The step row codec refuses that gap on both sides."));
            }

            if (!byCurrency.TryGetValue(currencyId, out List<ContentRow>? steps))
            {
                steps = [];
                byCurrency.Add(currencyId, steps);
            }

            steps.Add(step);
        }

        return byCurrency;
    }

    /// <summary>
    /// Every live guard row, split by the one empty reference spec 10.4 tells the two sets apart with: no
    /// <c>currency_step_id</c> is a TARGET guard, and one naming a step is that step's.
    /// </summary>
    static Dictionary<int, List<ContentRow>> ReadGuards(
        IContentSnapshot snapshot,
        Dictionary<int, List<ContentRow>> stepsByCurrency,
        Dictionary<int, List<ContentRow>> targetGuards)
    {
        var byStep = new Dictionary<int, List<ContentRow>>();

        foreach (ContentRow guard in LiveRows(snapshot, InstanceContentTypeIds.CurrencyGuardTypeId))
        {
            int currencyId = (int)(Number(guard, CurrencyGuardContentType.CraftingCurrencyIdIndex) ?? 0);
            if (!IsLive(snapshot, InstanceContentTypeIds.CraftingCurrencyTypeId, currencyId))
            {
                throw Closed(FormattableString.Invariant(
                    $"Guard {guard.Id} names currency {currencyId}, which this version carries no live row for. That is KEC0100 at publish."));
            }

            int stepId = (int)(Number(guard, CurrencyGuardContentType.CurrencyStepIdIndex) ?? 0);
            if (stepId == 0)
            {
                Seat(targetGuards, currencyId).Add(guard);
                continue;
            }

            if (!Owns(stepsByCurrency, currencyId, stepId))
            {
                throw Closed(FormattableString.Invariant(
                    $"Guard {guard.Id} of currency {currencyId} names step {stepId}, which belongs to another currency or to no live one. That is KEC0100 at publish, and a guard seated on nothing would silently never run."));
            }

            Seat(byStep, stepId).Add(guard);
        }

        return byStep;
    }

    /// <summary>One currency row and its children, resolved into the plan a craft runs.</summary>
    static CraftPlan Resolve(
        ContentRow currency,
        Dictionary<int, List<ContentRow>> stepsByCurrency,
        Dictionary<int, List<ContentRow>> guardsByStep,
        Dictionary<int, List<ContentRow>> targetGuards)
    {
        List<ContentRow> rows = stepsByCurrency.TryGetValue(currency.Id, out List<ContentRow>? held) ? held : [];
        if (rows.Count > CraftingCurrencyContentType.MaxSteps)
        {
            throw Closed(FormattableString.Invariant(
                $"Currency {currency.Id} carries {rows.Count} steps and v1 permits {CraftingCurrencyContentType.MaxSteps}. That is KEC0110 at publish."));
        }

        rows.Sort(static (left, right) =>
        {
            int order = (Number(left, CurrencyStepContentType.SortIndex) ?? 0)
                .CompareTo(Number(right, CurrencyStepContentType.SortIndex) ?? 0);
            return order != 0 ? order : left.Id.CompareTo(right.Id);
        });

        var steps = new CraftPlanStep[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            ContentRow row = rows[index];
            steps[index] = new CraftPlanStep(
                row.Id,
                (int)(Number(row, CurrencyStepContentType.SortIndex) ?? 0),
                (int)(Number(row, CurrencyStepContentType.OperationIndex) ?? 0),
                [
                    (int)(Number(row, CurrencyStepContentType.ParameterAIndex) ?? 0),
                    (int)(Number(row, CurrencyStepContentType.ParameterBIndex) ?? 0),
                    (int)(Number(row, CurrencyStepContentType.ParameterCIndex) ?? 0),
                    (int)(Number(row, CurrencyStepContentType.ParameterDIndex) ?? 0),
                ],
                Guards(guardsByStep, row.Id));
        }

        // An empty consumes_definition_id is a FREE operation rather than an authoring mistake, and the
        // plan's constructor is what drops the count that would otherwise sit beside no item.
        return new CraftPlan(
            currency.Id,
            (int)(Number(currency, CraftingCurrencyContentType.ConsumesDefinitionIdIndex) ?? 0),
            (int)(Number(currency, CraftingCurrencyContentType.ConsumesCountIndex) ?? 0),
            Guards(targetGuards, currency.Id),
            steps);
    }

    /// <summary>One guard set, in authored <c>sort</c> order, which is the order it is ANDed in.</summary>
    static CraftGuard[] Guards(Dictionary<int, List<ContentRow>> sets, int owner)
    {
        if (!sets.TryGetValue(owner, out List<ContentRow>? rows))
        {
            return [];
        }

        rows.Sort(static (left, right) =>
        {
            int order = (Number(left, CurrencyGuardContentType.SortIndex) ?? 0)
                .CompareTo(Number(right, CurrencyGuardContentType.SortIndex) ?? 0);
            return order != 0 ? order : left.Id.CompareTo(right.Id);
        });

        var guards = new CraftGuard[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            guards[index] = new CraftGuard(
                (CraftGuardKind)(Number(rows[index], CurrencyGuardContentType.GuardKindIndex) ?? 0),
                (int)(Number(rows[index], CurrencyGuardContentType.ParameterAIndex) ?? 0),
                (int)(Number(rows[index], CurrencyGuardContentType.ParameterBIndex) ?? 0));
        }

        return guards;
    }

    /// <summary>Whether that currency owns that step, which is what a step guard may reach.</summary>
    static bool Owns(Dictionary<int, List<ContentRow>> stepsByCurrency, int currencyId, int stepId)
    {
        if (!stepsByCurrency.TryGetValue(currencyId, out List<ContentRow>? steps))
        {
            return false;
        }

        foreach (ContentRow step in steps)
        {
            if (step.Id == stepId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One owner's list, created on first use.</summary>
    static List<ContentRow> Seat(Dictionary<int, List<ContentRow>> sets, int owner)
    {
        if (!sets.TryGetValue(owner, out List<ContentRow>? rows))
        {
            rows = [];
            sets.Add(owner, rows);
        }

        return rows;
    }

    /// <summary>The refusal that fails the boot closed, which the runtime names the type on.</summary>
    static InvalidOperationException Closed(string message) => new(message);
}
