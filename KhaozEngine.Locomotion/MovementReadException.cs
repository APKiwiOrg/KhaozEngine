using System;

namespace KhaozEngine.Locomotion;

/// <summary>A live movement read refusal. Existing invalid-operation catches remain valid.</summary>
public sealed class MovementReadException : InvalidOperationException
{
    /// <summary>Creates a refusal with the unchanged developer message.</summary>
    public MovementReadException(MovementReadRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>The live read rule that refused the operation.</summary>
    public MovementReadRefusal Reason { get; }
    /// <summary>The frame retained by the refused pin or lease.</summary>
    public MovementFrameDescriptor? Frame { get; init; }
    /// <summary>The lease identity compared by the refused binding guard.</summary>
    public MovementQueryLeaseId? LeaseId { get; init; }
    /// <summary>The managed thread that acquired the lease.</summary>
    public int? OwnerThreadId { get; init; }
    /// <summary>The managed thread that attempted to use the lease.</summary>
    public int? CallerThreadId { get; init; }
}
