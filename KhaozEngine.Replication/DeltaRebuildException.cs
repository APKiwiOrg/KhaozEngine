using System;

namespace KhaozEngine.Replication;

/// <summary>A format 2 operation failed for a typed reason a stream owner maps to one recovery action.</summary>
public sealed class DeltaRebuildException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">The typed reason. Never <see cref="DeltaRebuildFailure.None"/>.</param>
    /// <param name="message">A developer facing description.</param>
    public DeltaRebuildException(DeltaRebuildFailure failure, string message) : base(message)
    {
        if (failure == DeltaRebuildFailure.None)
            throw new ArgumentOutOfRangeException(nameof(failure), failure, "A rebuild failure needs a reason.");
        Failure = failure;
    }

    /// <summary>The typed reason for the failure.</summary>
    public DeltaRebuildFailure Failure { get; }
}
