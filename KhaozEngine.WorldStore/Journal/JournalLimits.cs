using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace KhaozEngine.WorldStore.Journal;

public sealed class JournalLimits
{
    public const int EngineMaximumStreamsPerOperation = 16;
    public const int EngineMaximumEventsPerOperation = 128;
    public const int EngineMaximumProjectionWritesPerOperation = 64;
    public const int EngineMaximumProjectionSectionsPerStream = 64;
    public const int EngineMaximumNormalizedIntentBytes = 64 * 1024;
    public const int EngineMaximumEventPayloadBytes = 256 * 1024;
    public const int EngineMaximumResultBytes = 64 * 1024;
    public const int EngineMaximumProjectionSectionBytes = 2 * 1024 * 1024;
    public const int EngineMaximumSnapshotBytes = 8 * 1024 * 1024;
    public const int EngineMaximumEventsPerReadPage = 2_048;
    public const int EngineMaximumAggregateCommitBytes = 8 * 1024 * 1024;
    public const int EngineMaximumAggregateEventReadBytes = 8 * 1024 * 1024;
    public const int EngineMaximumAggregateProjectionBytesPerStream = 8 * 1024 * 1024;
    public const int EngineMaximumStreamKeyCharacters = 256;
    public const int EngineMaximumIdentityCharacters = 128;

    public static JournalLimits Maximum { get; } = new();

    public JournalLimits(
        int streamsPerOperation = EngineMaximumStreamsPerOperation,
        int eventsPerOperation = EngineMaximumEventsPerOperation,
        int projectionWritesPerOperation = EngineMaximumProjectionWritesPerOperation,
        int projectionSectionsPerStream = EngineMaximumProjectionSectionsPerStream,
        int normalizedIntentBytes = EngineMaximumNormalizedIntentBytes,
        int eventPayloadBytes = EngineMaximumEventPayloadBytes,
        int resultBytes = EngineMaximumResultBytes,
        int projectionSectionBytes = EngineMaximumProjectionSectionBytes,
        int snapshotBytes = EngineMaximumSnapshotBytes,
        int eventsPerReadPage = EngineMaximumEventsPerReadPage,
        int aggregateCommitBytes = EngineMaximumAggregateCommitBytes,
        int aggregateEventReadBytes = EngineMaximumAggregateEventReadBytes,
        int aggregateProjectionBytesPerStream = EngineMaximumAggregateProjectionBytesPerStream,
        int streamKeyCharacters = EngineMaximumStreamKeyCharacters,
        int identityCharacters = EngineMaximumIdentityCharacters)
    {
        StreamsPerOperation = Downward(streamsPerOperation, EngineMaximumStreamsPerOperation, nameof(streamsPerOperation));
        EventsPerOperation = Downward(eventsPerOperation, EngineMaximumEventsPerOperation, nameof(eventsPerOperation));
        ProjectionWritesPerOperation = Downward(projectionWritesPerOperation, EngineMaximumProjectionWritesPerOperation, nameof(projectionWritesPerOperation));
        ProjectionSectionsPerStream = Downward(projectionSectionsPerStream, EngineMaximumProjectionSectionsPerStream, nameof(projectionSectionsPerStream));
        NormalizedIntentBytes = Downward(normalizedIntentBytes, EngineMaximumNormalizedIntentBytes, nameof(normalizedIntentBytes));
        EventPayloadBytes = Downward(eventPayloadBytes, EngineMaximumEventPayloadBytes, nameof(eventPayloadBytes));
        ResultBytes = Downward(resultBytes, EngineMaximumResultBytes, nameof(resultBytes));
        ProjectionSectionBytes = Downward(projectionSectionBytes, EngineMaximumProjectionSectionBytes, nameof(projectionSectionBytes));
        SnapshotBytes = Downward(snapshotBytes, EngineMaximumSnapshotBytes, nameof(snapshotBytes));
        EventsPerReadPage = Downward(eventsPerReadPage, EngineMaximumEventsPerReadPage, nameof(eventsPerReadPage));
        AggregateCommitBytes = Downward(aggregateCommitBytes, EngineMaximumAggregateCommitBytes, nameof(aggregateCommitBytes));
        AggregateEventReadBytes = Downward(aggregateEventReadBytes, EngineMaximumAggregateEventReadBytes, nameof(aggregateEventReadBytes));
        AggregateProjectionBytesPerStream = Downward(aggregateProjectionBytesPerStream, EngineMaximumAggregateProjectionBytesPerStream, nameof(aggregateProjectionBytesPerStream));
        StreamKeyCharacters = Downward(streamKeyCharacters, EngineMaximumStreamKeyCharacters, nameof(streamKeyCharacters));
        IdentityCharacters = Downward(identityCharacters, EngineMaximumIdentityCharacters, nameof(identityCharacters));
    }

    public int StreamsPerOperation { get; }
    public int EventsPerOperation { get; }
    public int ProjectionWritesPerOperation { get; }
    public int ProjectionSectionsPerStream { get; }
    public int NormalizedIntentBytes { get; }
    public int EventPayloadBytes { get; }
    public int ResultBytes { get; }
    public int ProjectionSectionBytes { get; }
    public int SnapshotBytes { get; }
    public int EventsPerReadPage { get; }
    public int AggregateCommitBytes { get; }
    public int AggregateEventReadBytes { get; }
    public int AggregateProjectionBytesPerStream { get; }
    public int StreamKeyCharacters { get; }
    public int IdentityCharacters { get; }

    private static int Downward(int value, int maximum, string parameterName)
    {
        if (value < 1 || value > maximum) throw new ArgumentOutOfRangeException(parameterName, value, $"Value must be from 1 through {maximum}.");
        return value;
    }
}

internal static class JournalValidation
{
    internal static string Identity(string value, string parameterName, int maximumCharacters, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!allowEmpty && value.Length == 0) throw new ArgumentException("Value cannot be empty.", parameterName);
        if (value.Length > maximumCharacters) throw new ArgumentOutOfRangeException(parameterName, value.Length, $"Value cannot exceed {maximumCharacters} characters.");
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool allowed = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '.' or '_' or ':' or '/' or '-';
            if (!allowed) throw new ArgumentException("Value must use only [A-Za-z0-9._:/-].", parameterName);
        }
        return value;
    }

    internal static string StreamKey(string value, string parameterName = "streamKey")
        => Identity(value, parameterName, JournalLimits.EngineMaximumStreamKeyCharacters);

    internal static byte[] CopyBytes(byte[] value, int maximumBytes, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length > maximumBytes) throw new ArgumentOutOfRangeException(parameterName, value.Length, $"Value cannot exceed {maximumBytes} bytes.");
        return (byte[])value.Clone();
    }

    internal static T[] CopyItems<T>(IReadOnlyList<T> values, int maximumCount, string parameterName) where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > maximumCount) throw new ArgumentOutOfRangeException(parameterName, values.Count, $"Collection cannot exceed {maximumCount} items.");
        var copy = new T[values.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = values[i] ?? throw new ArgumentException("Collection cannot contain null values.", parameterName);
        return copy;
    }

    internal static void Maximum(int actual, int maximum, string parameterName)
    {
        if (actual > maximum) throw new ArgumentOutOfRangeException(parameterName, actual, $"Value cannot exceed {maximum}.");
    }

    internal static void NonNegative(long value, string parameterName)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(parameterName, value, "Value cannot be negative.");
    }

    internal static void Positive(int value, string parameterName)
    {
        if (value < 1) throw new ArgumentOutOfRangeException(parameterName, value, "Value must be positive.");
    }

    internal static byte[] Hash(ReadOnlySpan<byte> value) => SHA256.HashData(value);

    internal static ReadOnlyMemory<byte> CopyForRead(byte[] value) => (byte[])value.Clone();

    internal static bool HashMatches(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expected)
        => expected.Length == SHA256.HashSizeInBytes && CryptographicOperations.FixedTimeEquals(Hash(value), expected);

    internal static bool ValidateEventPageContinuity(
        string streamKey,
        long afterVersion,
        long throughVersion,
        IReadOnlyList<JournalStoredEvent> events,
        bool stoppedByCountLimit,
        bool stoppedByByteLimit)
    {
        long expectedVersion = afterVersion;
        foreach (JournalStoredEvent storedEvent in events)
        {
            expectedVersion = checked(expectedVersion + 1);
            if (storedEvent.StreamVersion != expectedVersion)
                throw CorruptEventSequence(streamKey);
        }

        bool reachedThroughVersion = expectedVersion >= throughVersion;
        if (!reachedThroughVersion && !stoppedByCountLimit && !stoppedByByteLimit)
            throw CorruptEventSequence(streamKey);
        return reachedThroughVersion;
    }

    private static JournalStoreException CorruptEventSequence(string streamKey)
        => new(
            JournalStoreFailureKind.CorruptData,
            JournalStoreFailureCertainty.CommittedDataUnreadable,
            JournalStoreFailureScope.OperationStreams,
            new[] { streamKey },
            "Stored journal event sequence is not contiguous.");
}
