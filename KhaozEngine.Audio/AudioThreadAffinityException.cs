using System;

namespace KhaozEngine.Audio;

/// <summary>A structured refusal of a call into an <see cref="AudioSystem"/> from the wrong thread. Existing
/// InvalidOperationException catches remain valid.</summary>
public sealed class AudioThreadAffinityException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason.</summary>
    /// <param name="reason">The rule that refused the call.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    public AudioThreadAffinityException(AudioThreadAffinityRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the call.</summary>
    public AudioThreadAffinityRefusal Reason { get; }

    /// <summary>The public <see cref="AudioSystem"/> member the caller used, with no type prefix. A guard on a shared
    /// private helper reports the public member it stands in for, never the helper.</summary>
    public string? MemberName { get; init; }

    /// <summary>The managed id of the thread that constructed the <see cref="AudioSystem"/>.</summary>
    public int? OwnerThreadId { get; init; }

    /// <summary>The managed id of the thread that made the refused call.</summary>
    public int? CallerThreadId { get; init; }
}
