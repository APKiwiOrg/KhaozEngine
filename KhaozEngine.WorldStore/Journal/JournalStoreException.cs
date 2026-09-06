using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.WorldStore.Journal;

public enum JournalStoreFailureKind
{
    Unavailable,
    Timeout,
    Deadlock,
    Cancelled,
    UnknownOutcome,
    CorruptData,
    SchemaMismatch,
    ConstraintViolation,
}

public enum JournalStoreFailureCertainty
{
    DefinitelyNotCommitted,
    Unknown,
    CommittedDataUnreadable,
}

public enum JournalStoreFailureScope
{
    OperationStreams,
    WholeStore,
}

public sealed class JournalStoreException : Exception
{
    private readonly ReadOnlyCollection<string> streamKeys;

    public JournalStoreException(
        JournalStoreFailureKind kind,
        JournalStoreFailureCertainty certainty,
        JournalStoreFailureScope scope,
        IReadOnlyList<string>? streamKeys,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(message);
        string[] copy = CopyStreamKeys(streamKeys);
        if (scope == JournalStoreFailureScope.WholeStore && copy.Length != 0)
            throw new ArgumentException("A whole-store failure cannot name operation streams.", nameof(streamKeys));
        if (scope == JournalStoreFailureScope.OperationStreams && copy.Length == 0)
            throw new ArgumentException("An operation-stream failure must name at least one stream.", nameof(streamKeys));
        Kind = kind;
        Certainty = certainty;
        Scope = scope;
        this.streamKeys = Array.AsReadOnly(copy);
    }

    public JournalStoreFailureKind Kind { get; }
    public JournalStoreFailureCertainty Certainty { get; }
    public JournalStoreFailureScope Scope { get; }
    public IReadOnlyList<string> StreamKeys => streamKeys;

    private static string[] CopyStreamKeys(IReadOnlyList<string>? values)
    {
        if (values is null) return Array.Empty<string>();
        if (values.Count > JournalLimits.EngineMaximumStreamsPerOperation)
            throw new ArgumentOutOfRangeException(nameof(values), values.Count, $"Collection cannot exceed {JournalLimits.EngineMaximumStreamsPerOperation} items.");
        var copy = new string[values.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = JournalValidation.StreamKey(values[i], nameof(values));
        Array.Sort(copy, StringComparer.Ordinal);
        for (int i = 1; i < copy.Length; i++)
            if (StringComparer.Ordinal.Equals(copy[i - 1], copy[i])) throw new ArgumentException("Stream keys must be unique.", nameof(values));
        return copy;
    }
}
