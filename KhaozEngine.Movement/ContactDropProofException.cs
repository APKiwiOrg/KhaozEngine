using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Stable reasons an unavailable Contact query refuses a Drop proof.</summary>
public enum ContactDropProofFailure
{
    /// <summary>Water pacing could not supply known evidence.</summary>
    PacingUnavailable,
    /// <summary>A Contact step could not supply usable environment or frame evidence.</summary>
    StepUnavailable,
}

/// <summary>A Drop proof refused unavailable evidence instead of publishing a partial graph.</summary>
public sealed class ContactDropProofException : InvalidOperationException
{
    internal ContactDropProofException(MovementAvailability availability)
        : base($"Contact Drop pacing returned {availability}.")
    {
        Reason = ContactDropProofFailure.PacingUnavailable;
        Availability = availability;
    }

    internal ContactDropProofException(MovementStepOutcome outcome)
        : base($"Contact Drop step returned {outcome}.")
    {
        Reason = ContactDropProofFailure.StepUnavailable;
        Outcome = outcome;
    }

    /// <summary>The stable phase that refused the proof.</summary>
    public ContactDropProofFailure Reason { get; }
    /// <summary>The unavailable water pacing result, or null for a step refusal.</summary>
    public MovementAvailability? Availability { get; }
    /// <summary>The refused step result, or null for a pacing refusal.</summary>
    public MovementStepOutcome? Outcome { get; }
}
