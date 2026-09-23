using System;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;

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

    /// <summary>A crafted envelope contains an unreadable audit body.</summary>
    public const string CraftBody = "event-craft-body";

    /// <summary>A granted envelope contains malformed instance property bytes.</summary>
    public const string Payload = "event-payload";

    /// <summary>The bytes the builder must reserve for one event before changing a working copy.</summary>
    internal static int EncodedSize(in ContainerOperation operation)
    {
        int canonical = operation.CanonicalByteCount;
        if (operation.Kind is not (ContainerOperationKind.Grant or ContainerOperationKind.Craft))
            return canonical;

        int extra = operation.Kind == ContainerOperationKind.Grant
            ? operation.Payload.Length : operation.EventPayload.Length;
        return checked(ContentVarint.Size((uint)canonical) + canonical
            + ContentVarint.Size((uint)extra) + extra);
    }

    /// <summary>Encodes the event this operation writes. Grants and crafts carry their missing replay data
    /// under schema version 2. Every other existing kind retains its version 1 canonical body.</summary>
    public static (int SchemaVersion, byte[] Body) Write(in ContainerOperation operation)
    {
        operation.Validate();
        if (operation.Kind is not (ContainerOperationKind.Grant or ContainerOperationKind.Craft))
            return (1, operation.ToCanonicalArray());

        ReadOnlyMemory<byte> extra = operation.Kind == ContainerOperationKind.Grant
            ? operation.Payload : operation.EventPayload;
        if (operation.Kind == ContainerOperationKind.Grant)
        {
            if (extra.Length > ItemInstancePayload.MaxInstancePayloadBytes
                || ItemInstancePayload.Validate(extra.Span) is not null)
                throw new ArgumentException("A granted item carries one canonical payload within the item cap.",
                    nameof(operation));
        }
        else
        {
            if (!ItemCraftedEvent.TryRead(extra, out ItemCraftedEvent audit, out _)
                || audit.InstanceId != operation.InstanceId
                || !audit.After.Span.SequenceEqual(operation.Payload.Span))
                throw new ArgumentException("A craft event's audit body must name the target and its after payload.",
                    nameof(operation));
        }

        byte[] canonical = operation.ToCanonicalArray();
        byte[] body = new byte[EncodedSize(operation)];
        int offset = ContentVarint.Write(body, (uint)canonical.Length);
        canonical.CopyTo(body.AsSpan(offset));
        offset += canonical.Length;
        offset += ContentVarint.Write(body.AsSpan(offset), (uint)extra.Length);
        extra.Span.CopyTo(body.AsSpan(offset));
        return (2, body);
    }

    /// <summary>Reads one event for deterministic container replay. A version 1 craft remains readable by
    /// <see cref="ItemCraftedEvent.TryRead"/> for audit, but it cannot be applied from its body alone.</summary>
    public static bool TryRead(string eventType, int schemaVersion, ReadOnlyMemory<byte> body,
        out ContainerOperation operation, out string? reason)
    {
        operation = default;
        if (schemaVersion == 2)
        {
            if (!StringComparer.Ordinal.Equals(eventType, ItemInstanceEvents.Granted)
                && !StringComparer.Ordinal.Equals(eventType, ItemInstanceEvents.Crafted))
            {
                reason = Version;
                return false;
            }
            return TryReadV2(eventType, body, out operation, out reason);
        }
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

    static bool TryReadV2(string eventType, ReadOnlyMemory<byte> body,
        out ContainerOperation operation, out string? reason)
    {
        operation = default;
        int offset = 0;
        if (!ReadPart(body, ref offset, out ReadOnlyMemory<byte> canonical, out reason)
            || !ReadPart(body, ref offset, out ReadOnlyMemory<byte> extra, out reason)) return false;
        if (offset != body.Length)
        {
            reason = Trailing;
            return false;
        }
        if (!ContainerOperation.TryReadCanonical(canonical.Span, out ContainerOperation read, out reason))
            return false;
        if (!StringComparer.Ordinal.Equals(read.EventType, eventType))
        {
            reason = Kind;
            return false;
        }

        if (read.Kind == ContainerOperationKind.Grant)
        {
            if (extra.Length > ItemInstancePayload.MaxInstancePayloadBytes
                || ItemInstancePayload.Validate(extra.Span) is not null)
            {
                reason = Payload;
                return false;
            }
            operation = read with { Payload = extra };
        }
        else if (read.Kind == ContainerOperationKind.Craft)
        {
            if (!ItemCraftedEvent.TryRead(extra, out ItemCraftedEvent audit, out _))
            {
                reason = CraftBody;
                return false;
            }
            if (audit.InstanceId != read.InstanceId)
            {
                reason = FieldRange;
                return false;
            }
            operation = read with { Payload = audit.After, EventPayload = extra };
        }
        else
        {
            reason = Kind;
            return false;
        }

        reason = null;
        return true;
    }

    static bool ReadPart(ReadOnlyMemory<byte> body, ref int offset,
        out ReadOnlyMemory<byte> part, out string? reason)
    {
        part = default;
        if (!ContentVarint.TryRead(body.Span, ref offset, out uint rawLength, out string? varintReason))
        {
            reason = varintReason == ContentVarint.ReasonTruncated ? Truncated : Varint;
            return false;
        }
        if (rawLength > int.MaxValue)
        {
            reason = FieldRange;
            return false;
        }
        int length = (int)rawLength;
        if (length > body.Length - offset)
        {
            reason = Truncated;
            return false;
        }
        part = body.Slice(offset, length);
        offset += length;
        reason = null;
        return true;
    }
}
