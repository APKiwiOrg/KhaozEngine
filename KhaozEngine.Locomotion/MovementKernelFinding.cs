namespace KhaozEngine.Locomotion;

/// <summary>The first failed kernel predicate, before any adapter adds its existing prose.</summary>
internal readonly record struct MovementKernelFinding(MovementConfigurationRefusal Reason)
{
    internal string? FieldName { get; init; }
    internal double? ActualNumber { get; init; }
    internal double? MinimumNumber { get; init; }
    internal double? MinimumExclusive { get; init; }
    internal double? MaximumNumber { get; init; }
    internal float? CapsuleRadius { get; init; }
    internal float? CapsuleHalfHeight { get; init; }
    internal float? ActualSlope { get; init; }
    internal float? MaximumExclusive { get; init; }
    internal float? StartFraction { get; init; }
    internal float? EndFraction { get; init; }
    internal float? ExitFraction { get; init; }
    internal float? EnterFraction { get; init; }

    internal MovementConfigurationException ToException(string message) => new(Reason, message, "tuning")
    {
        FieldName = FieldName,
        ActualNumber = ActualNumber,
        MinimumNumber = MinimumNumber,
        MinimumExclusive = MinimumExclusive,
        MaximumNumber = MaximumNumber,
        CapsuleRadius = CapsuleRadius,
        CapsuleHalfHeight = CapsuleHalfHeight,
        ActualSlope = ActualSlope,
        MaximumExclusive = MaximumExclusive,
        StartFraction = StartFraction,
        EndFraction = EndFraction,
        ExitFraction = ExitFraction,
        EnterFraction = EnterFraction
    };
}
