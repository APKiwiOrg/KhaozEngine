using System;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>Reads a stored container operation event without guessing a payload or a slot its version
/// never wrote. Later event versions add the missing data without changing these version 1 bytes.</summary>
public static class ContainerOperationEventCodec
{
    /// <summary>A varint or field ended before its declared bytes.</summary>
    public const string Truncated = "event-truncated";

    /// <summary>A varint overflowed or used a nonminimal encoding.</summary>
    public const string Varint = "event-varint";

    /// <summary>A container name is not valid UTF-8.</summary>
    public const string Utf8 = "event-utf8";

    /// <summary>Bytes remain after the operation's last field.</summary>
    public const string Trailing = "event-trailing";

    /// <summary>An unknown kind or a durable event name disagrees with its kind.</summary>
    public const string Kind = "event-kind";

    /// <summary>The stored event schema version is not supported.</summary>
    public const string Version = "event-version";

    /// <summary>A field cannot fit its declared type or violates the operation grammar.</summary>
    public const string FieldRange = "event-field-range";

    /// <summary>A version 1 grant named an instance but omitted its payload.</summary>
    public const string LegacyPayloadOmitted = "legacy-payload-omitted";

    /// <summary>A version 1 craft body records before and after payloads but no target slot.</summary>
    public const string LegacyLocationOmitted = "legacy-location-omitted";

    /// <summary>Reads one event for deterministic container replay. A version 1 craft remains readable by
    /// <see cref="ItemCraftedEvent.TryRead"/> for audit, but it cannot be applied from its body alone.</summary>
    public static bool TryRead(string eventType, int schemaVersion, ReadOnlyMemory<byte> body,
        out ContainerOperation operation, out string? reason)
    {
        operation = default;
        if (schemaVersion != 1)
        {
            reason = Version;
            return false;
        }
        if (StringComparer.Ordinal.Equals(eventType, ItemInstanceEvents.Crafted))
        {
            reason = LegacyLocationOmitted;
            return false;
        }
        if (!ContainerOperation.TryReadCanonical(body.Span, out ContainerOperation read, out reason))
            return false;
        if (!StringComparer.Ordinal.Equals(read.EventType, eventType))
        {
            reason = Kind;
            return false;
        }
        if (read.Kind == ContainerOperationKind.Grant && read.InstanceId != 0)
        {
            reason = LegacyPayloadOmitted;
            return false;
        }

        operation = read;
        reason = null;
        return true;
    }
}
