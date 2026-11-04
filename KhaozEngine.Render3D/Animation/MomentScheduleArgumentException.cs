using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of a moment schedule table.</summary>
public sealed class MomentScheduleArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public MomentScheduleArgumentException(MomentScheduleRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or access was refused.</summary>
    public MomentScheduleRefusal Reason { get; }

    /// <summary>The name of the refused option.</summary>
    public string? OptionName { get; init; }

    /// <summary>The refused option value, including the integer empty-run limit represented as a double.</summary>
    public double? OptionValue { get; init; }

    /// <summary>The number of families in an empty table.</summary>
    public int? FamilyCount { get; init; }

    /// <summary>The name of the refused moment family.</summary>
    public string? FamilyName { get; init; }

    /// <summary>The index of the refused moment family in the caller table.</summary>
    public int? FamilyIndex { get; init; }

    /// <summary>The refused family weight.</summary>
    public double? Weight { get; init; }

    /// <summary>The refused family duration in seconds.</summary>
    public double? LengthSeconds { get; init; }

    /// <summary>The seconds available between a slot's quiet ends.</summary>
    public double? AvailableSeconds { get; init; }

    /// <summary>The configured slot duration in seconds.</summary>
    public double? SlotSeconds { get; init; }

    /// <summary>The configured duration of each quiet end in seconds.</summary>
    public double? QuietSeconds { get; init; }

    /// <summary>The non-finite sum of family weights.</summary>
    public double? TotalWeight { get; init; }
}
