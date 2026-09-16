using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// One resolved step of one currency: its authored position, the operation it names, that operation's four
/// parameters and the guard set evaluated immediately before it.
/// <para>
/// <b>The operation number covers two vocabularies and the number itself is the split.</b>
/// <see cref="CurrencyStepContentType.MinPrimitiveOperation"/> to
/// <see cref="CurrencyStepContentType.MaxPrimitiveOperation"/> is one of the fourteen primitives spec 10.2
/// closes, and anything at or above <see cref="CurrencyStepContentType.FirstGameOperation"/> is a game
/// operation reached through <see cref="CraftingRegistry"/>. The two share the step list ON PURPOSE, so one
/// currency can mix them, which is the whole reason the registry exists.
/// </para>
/// <para>
/// <b>A SELECTOR occupies TWO parameter slots, its kind then its parameter.</b> Primitives 2, 3 and 10 take
/// one, so <see cref="ParameterA"/> is a <see cref="CraftSelectorKind"/> and <see cref="ParameterB"/> is
/// that kind's own mod id, index or mod kind. Nothing here interprets them: the executor is the one place
/// the parameter map lives, so no two readers can drift apart on it.
/// </para>
/// </summary>
public sealed class CraftPlanStep
{
    /// <summary>How many parameters a step carries, which is <c>parameter_a</c> to <c>parameter_d</c>.</summary>
    public const int ParameterCount = 4;

    readonly int[] _parameters;
    readonly CraftGuard[] _guards;

    /// <summary>Resolves one <c>currency_step</c> row and its guards into a step of a plan.</summary>
    /// <param name="stepId">The <c>currency_step</c> row id, kept so a refusal names the authored row.</param>
    /// <param name="sort">Its authored position within the currency.</param>
    /// <param name="operation">A primitive of spec 10.2 or a game operation of spec 10.5.</param>
    /// <param name="parameters">The four authored parameters, in authored order.</param>
    /// <param name="guards">The guard set evaluated immediately before this step, in authored order.</param>
    /// <exception cref="ArgumentNullException">Either array is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="parameters"/> is not
    /// <see cref="ParameterCount"/> long.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is neither a primitive nor
    /// a game operation, which is the gap the step codec refuses on both sides.</exception>
    public CraftPlanStep(int stepId, int sort, int operation, int[] parameters, CraftGuard[] guards)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(guards);

        if (parameters.Length != ParameterCount)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"A step carries exactly {ParameterCount} parameters and this one carries {parameters.Length}. An unused parameter is 0, so a parameter an author zeroed and a parameter nobody wrote are the same authored row."),
                nameof(parameters));
        }

        if (!CurrencyStepContentType.IsPrimitive(operation) && !CurrencyStepContentType.IsGameOperation(operation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                FormattableString.Invariant(
                    $"Operation {operation} is neither one of the {CurrencyStepContentType.MaxPrimitiveOperation} primitives nor a game operation at or above {CurrencyStepContentType.FirstGameOperation}."));
        }

        StepId = stepId;
        Sort = sort;
        Operation = operation;
        _parameters = parameters;
        _guards = guards;
    }

    /// <summary>The <c>currency_step</c> row this step resolved from.</summary>
    public int StepId { get; }

    /// <summary>Its authored position, which is the only thing <c>sort</c> means.</summary>
    public int Sort { get; }

    /// <summary>The operation number, which is the authored <c>operation</c> field verbatim.</summary>
    public int Operation { get; }

    /// <summary>Whether this step is a GAME operation rather than one of the fourteen primitives.</summary>
    public bool IsGameOperation => CurrencyStepContentType.IsGameOperation(Operation);

    /// <summary>
    /// The primitive this step names, meaningful only while <see cref="IsGameOperation"/> is false. The
    /// enum's numbers ARE the authored operation values, so this is a cast rather than a table.
    /// </summary>
    public CraftPrimitive Primitive => (CraftPrimitive)Operation;

    /// <summary>The four authored parameters, in authored order, with 0 for an unused slot.</summary>
    public ReadOnlyMemory<int> Parameters => _parameters;

    /// <summary>The step's first parameter.</summary>
    public int ParameterA => _parameters[0];

    /// <summary>Its second.</summary>
    public int ParameterB => _parameters[1];

    /// <summary>Its third.</summary>
    public int ParameterC => _parameters[2];

    /// <summary>Its fourth.</summary>
    public int ParameterD => _parameters[3];

    /// <summary>
    /// The guards evaluated immediately BEFORE this step, ANDed, in authored <c>sort</c> order. An empty
    /// set passes, and a set that fails SKIPS this step while the craft continues.
    /// </summary>
    public ReadOnlyMemory<CraftGuard> Guards => _guards;
}

/// <summary>
/// One <c>crafting_currency</c> row and its two children, RESOLVED: the target guard set, then the ordered
/// steps, each with its own guard set and either a primitive with parameters or a game operation id.
/// <para>
/// <b>A currency resolves into a plan ONCE at boot and never inside a tick.</b> The resolution is a
/// function of the content version and of nothing else, so it is built by <see cref="CraftPlanIndex"/> at
/// boot step 7b and is immutable for the life of the process. A lazy build inside a tick is a latency
/// spike, and a plan half built is a craft that does less than its rows say.
/// </para>
/// <para>
/// <b>Target guards and step guards are ONE authored type told apart by one empty reference</b>, and that
/// difference is the whole reason both exist. A target guard is a PRECONDITION on the craft, so failing one
/// refuses everything and consumes nothing. A step guard is a condition on one step, so failing one SKIPS
/// that step while the craft continues, which is how spec 10.4's whetstone repairs an item already at
/// quality 20 instead of refusing to touch it.
/// </para>
/// <para>
/// <b>A currency consuming NO definition carries no currency fields at all.</b> An empty
/// <c>consumes_definition_id</c> is a FREE operation rather than an authoring mistake, and a COUNT beside
/// no item names nothing, so resolution drops the count too rather than carrying a number a caller would
/// have to remember not to read.
/// </para>
/// </summary>
public sealed class CraftPlan
{
    readonly CraftGuard[] _targetGuards;
    readonly CraftPlanStep[] _steps;

    /// <summary>Resolves one currency row and its children into a plan.</summary>
    /// <param name="currencyId">The <c>crafting_currency</c> row id.</param>
    /// <param name="consumesDefinitionId">The item base one run spends, or 0 for a free operation.</param>
    /// <param name="consumesCount">How many of it, which is forced to 0 when nothing is spent.</param>
    /// <param name="targetGuards">The guards with no <c>currency_step_id</c>, in authored order.</param>
    /// <param name="steps">The steps, in authored <c>sort</c> order.</param>
    /// <exception cref="ArgumentNullException">Either array is null.</exception>
    public CraftPlan(
        int currencyId,
        int consumesDefinitionId,
        int consumesCount,
        CraftGuard[] targetGuards,
        CraftPlanStep[] steps)
    {
        ArgumentNullException.ThrowIfNull(targetGuards);
        ArgumentNullException.ThrowIfNull(steps);

        CurrencyId = currencyId;
        ConsumesDefinitionId = consumesDefinitionId;
        ConsumesCount = consumesDefinitionId == 0 ? 0 : consumesCount;
        _targetGuards = targetGuards;
        _steps = steps;
    }

    /// <summary>The <c>crafting_currency</c> row this plan resolved from.</summary>
    public int CurrencyId { get; }

    /// <summary>The <c>item</c> base one run spends, or 0 for a free operation.</summary>
    public int ConsumesDefinitionId { get; }

    /// <summary>How many of that item one run spends, and always 0 when nothing is spent.</summary>
    public int ConsumesCount { get; }

    /// <summary>Whether a run of this currency spends nothing, which a bench legitimately does.</summary>
    public bool IsFree => ConsumesDefinitionId == 0;

    /// <summary>
    /// The guards evaluated ONCE, before any step, against the target. A failure refuses the whole craft
    /// and consumes nothing.
    /// </summary>
    public ReadOnlyMemory<CraftGuard> TargetGuards => _targetGuards;

    /// <summary>The steps, in authored <c>sort</c> order, which is the order they run in.</summary>
    public ReadOnlyMemory<CraftPlanStep> Steps => _steps;
}
