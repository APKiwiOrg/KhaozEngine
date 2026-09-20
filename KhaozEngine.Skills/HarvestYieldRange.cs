using System;

namespace KhaozEngine.Skills;

/// <summary>A validated inclusive skill-level interval that distributes inclusive yield ceilings.</summary>
public readonly record struct HarvestYieldRange
{
    /// <summary>Creates a yield range.</summary>
    /// <param name="unlockLevel">The first level that can harvest.</param>
    /// <param name="capLevel">The last level that can raise the yield ceiling.</param>
    /// <param name="minimum">The inclusive minimum yield.</param>
    /// <param name="maximum">The highest inclusive yield ceiling.</param>
    public HarvestYieldRange(int unlockLevel, int capLevel, int minimum, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unlockLevel);
        ArgumentOutOfRangeException.ThrowIfLessThan(capLevel, unlockLevel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimum);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, minimum);

        UnlockLevel = unlockLevel;
        CapLevel = capLevel;
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>The first level that can harvest.</summary>
    public int UnlockLevel { get; }

    /// <summary>The last level that can raise the yield ceiling.</summary>
    public int CapLevel { get; }

    /// <summary>The inclusive minimum yield.</summary>
    public int Minimum { get; }

    /// <summary>The highest inclusive yield ceiling.</summary>
    public int Maximum { get; }

    /// <summary>Returns the inclusive yield ceiling at <paramref name="level"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is below
    /// <see cref="UnlockLevel"/>.</exception>
    public int CeilingAt(int level)
    {
        if (UnlockLevel <= 0 || Minimum <= 0 || CapLevel < UnlockLevel || Maximum < Minimum)
            throw new InvalidOperationException("The harvest yield range is not initialized.");
        if (level < UnlockLevel)
            throw new ArgumentOutOfRangeException(nameof(level), level,
                $"A harvest level must be at least the unlock level {UnlockLevel}.");

        long bands = (long)Maximum - Minimum + 1;
        long levels = (long)CapLevel - UnlockLevel + 1;
        long levelOffset = (long)Math.Min(level, CapLevel) - UnlockLevel;
        return Minimum + (int)Math.Min(bands - 1, levelOffset * bands / levels);
    }

    /// <summary>Returns the halved yield ceiling at <paramref name="level"/>, clamped to
    /// <see cref="Minimum"/>.</summary>
    public int ImprovisedCeilingAt(int level) => Math.Max(Minimum, CeilingAt(level) / 2);
}
