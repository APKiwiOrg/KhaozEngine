using System;
using System.Collections.Generic;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The closed set of PAGE level refusals <see cref="ItemContainerPageCodec"/> answers with, spec 4.4 and
/// 4.5. Contracts 9.7's eight tokens are PAYLOAD reasons and say nothing about the page a payload rides
/// in, so a page that fails on its version, its header, its entry order or its length has no token there.
/// <para>
/// <b>These are page reasons rather than quarantine reason CODES.</b> Spec 5.5 step 1 quarantines a failed
/// page as a UNIT, and the <c>KECQ</c> wrapper's <c>ReasonCode</c> byte is an ordinal from the payload set
/// (spec 12.4), so nothing here is ever written to a durable byte. They are diagnostic tokens, stable
/// across runs so a log line and a test can both name one.
/// </para>
/// <para>
/// Two other closed sets reach a caller through the same <c>reason</c> parameter and neither is folded
/// into this one. A malformed VARINT answers with <c>ContentVarint</c>'s own token, which is more precise
/// than any page token could be about which byte failed. A version 1 blob that the version 1 reader
/// refuses answers with that reader's own message, because the refusal belongs to the reader that owns
/// the format.
/// </para>
/// </summary>
public static class ItemContainerPageReason
{
    /// <summary>The version field names a format this build does not read.</summary>
    public const string Version = "page-version";

    /// <summary>The page ends before a field the header or an entry declared.</summary>
    public const string Truncated = "page-truncated";

    /// <summary>
    /// The page's slot ORIGIN or EXTENT does not fit the caller's geometry: <c>FirstSlot</c> is not
    /// <c>PageIndex</c> times the caller's page size, which is what catches a page written into the wrong
    /// section, or <c>SlotCount</c> is larger than that page size, which is what stops a page declaring
    /// more slots than the container it is being read into holds. The redundancy costs two bytes per page
    /// and the failures it catches are otherwise silent.
    /// <para>
    /// The extent bound is one sided on purpose. A SHORT last page is legal (5.2), so an equality would
    /// refuse a container whose slot count is not a whole number of pages.
    /// </para>
    /// </summary>
    public const string SlotOrigin = "page-slot-origin";

    /// <summary>Entries are not strictly ascending by slot. Order is what makes a duplicate slot impossible
    /// without a second pass, and the encoder only ever writes ascending.</summary>
    public const string SlotOrder = "page-slot-order";

    /// <summary>The declared entry count is more than the caller's buffer can hold.</summary>
    public const string EntryCount = "page-entry-count";

    /// <summary>An entry carries a field outside its declared range: a zero definition id, a zero count, a
    /// slot outside the page, or a value above <see cref="int.MaxValue"/> in a field declared int32.</summary>
    public const string EntryMalformed = "page-entry-malformed";

    /// <summary>Bytes remain after the last declared entry.</summary>
    public const string TrailingBytes = "page-trailing-bytes";

    /// <summary>Every page reason, in the order they are declared above.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        Version,
        Truncated,
        SlotOrigin,
        SlotOrder,
        EntryCount,
        EntryMalformed,
        TrailingBytes,
    };
}
