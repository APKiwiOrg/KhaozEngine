using System;

namespace KhaozEngine.WorldStore.Journal;

/// <summary>A journal state refusal that preserves the InvalidOperationException catch contract.</summary>
public sealed class JournalStoreStateException : InvalidOperationException
{
    /// <summary>Creates a state refusal with its rule and unchanged developer prose.</summary>
    public JournalStoreStateException(JournalStoreRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>Creates a state refusal with its rule, unchanged developer prose and the failure that caused it.</summary>
    public JournalStoreStateException(JournalStoreRefusal reason, string message, Exception? innerException)
        : base(message, innerException) => Reason = reason;

    /// <summary>The journal state rule refused.</summary>
    public JournalStoreRefusal Reason { get; }

    /// <summary>The returned batch outcome for an undefined or unsupported outcome refusal.</summary>
    public int? Outcome { get; init; }
}
