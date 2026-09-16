using System.Numerics;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// What one run of <see cref="CraftExecutor.Apply"/> did: whether the craft refused and why, which steps
/// ran, and which were SKIPPED by their own guard set.
/// <para>
/// <b>Ran and skipped are two masks rather than one list</b>, because a currency carries at most
/// <see cref="CraftingCurrencyContentType.MaxSteps"/> steps in v1 and a caller wants to ask about a step by
/// its position. Two <c>uint</c> masks answer that with no allocation at all, which matters because a craft
/// is a gameplay call rather than a boot one.
/// </para>
/// <para>
/// <b>Ran and skipped are not complements.</b> A refused craft stops where it stopped, so the steps after
/// the refusal are in NEITHER mask, and that is the difference between a craft that skipped a step and one
/// that never reached it.
/// </para>
/// <para>
/// <b>There is no draw count here, deliberately.</b> A craft's draws are made by the executor's
/// <c>IRandomSource</c> and by the generator it drives, and neither reports a count, so an honest number
/// would mean wrapping the caller's source in a counting decorator. That puts a virtual call on every draw
/// the generator makes and stands a second object between the caller's stream and the roll, which is
/// exactly the seam the constructor argument exists to keep obvious. A caller that wants the count wraps
/// its OWN source, which costs the engine nothing and is a decision it can take per host.
/// </para>
/// </summary>
/// <param name="Refusal">Why the craft refused, or a <see cref="CraftRefusalKind.None"/> value when it did
/// not.</param>
/// <param name="StepCount">How many steps the plan carried, whether or not they were reached.</param>
/// <param name="RanMask">Bit <c>index</c> per step that ran, by its position in the plan.</param>
/// <param name="SkippedMask">Bit <c>index</c> per step a guard set skipped.</param>
public readonly record struct CraftOutcome(CraftRefusal Refusal, int StepCount, uint RanMask, uint SkippedMask)
{
    /// <summary>The most steps a mask can answer about, which is a <c>uint</c>'s bit count.</summary>
    public const int MaxMaskedSteps = 32;

    /// <summary>Whether the craft refused, after which nothing durable was written.</summary>
    public bool IsRefused => Refusal.IsRefusal;

    /// <summary>How many steps ran.</summary>
    public int StepsRun => BitOperations.PopCount(RanMask);

    /// <summary>How many steps their own guard set skipped.</summary>
    public int StepsSkipped => BitOperations.PopCount(SkippedMask);

    /// <summary>Whether the step at that position in the plan ran.</summary>
    /// <param name="index">The step's position, which is its index in <see cref="CraftPlan.Steps"/>.</param>
    public bool Ran(int index) => IsSet(RanMask, index);

    /// <summary>Whether the step at that position was skipped by its guard set.</summary>
    /// <param name="index">The step's position, which is its index in <see cref="CraftPlan.Steps"/>.</param>
    public bool Skipped(int index) => IsSet(SkippedMask, index);

    /// <summary>One bit of one mask, with an index outside the mask answering false.</summary>
    static bool IsSet(uint mask, int index)
        => index is >= 0 and < MaxMaskedSteps && ((mask >> index) & 1u) != 0;
}
