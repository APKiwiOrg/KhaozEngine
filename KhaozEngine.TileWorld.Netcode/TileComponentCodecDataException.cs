using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>A malformed tile component payload. Catch MalformedDataException to handle malformed engine data.</summary>
public sealed class TileComponentCodecDataException : MalformedDataException
{
    /// <summary>Creates a malformed component refusal with its stable reason.</summary>
    public TileComponentCodecDataException(TileComponentCodecRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>Creates a malformed component refusal preserving its original cause.</summary>
    public TileComponentCodecDataException(TileComponentCodecRefusal reason, string message, Exception inner)
        : base(message, inner) => Reason = reason;

    /// <summary>The stable reason decoding was refused.</summary>
    public TileComponentCodecRefusal Reason { get; }

    /// <summary>The route's declared step count, for route read refusals.</summary>
    public int? DeclaredCount { get; init; }

    /// <summary>The route's wire step limit, for RouteReadLimitExceeded.</summary>
    public int? MaximumCount { get; init; }

    /// <summary>The name's declared UTF-8 byte count, for name read refusals.</summary>
    public int? DeclaredByteCount { get; init; }

    /// <summary>The name's UTF-8 byte limit, for NameReadLimitExceeded.</summary>
    public int? MaximumByteCount { get; init; }

    /// <summary>The bounded payload bytes left after the length prefix, for payload truncation refusals.</summary>
    public long? AvailableByteCount { get; init; }

    /// <summary>The bytes actually read, for unbounded stream short-read refusals.</summary>
    public int? ActualReadCount { get; init; }

    /// <summary>The component naming the refused plane, for PlaneOutsideWorld.</summary>
    public string? ComponentName { get; init; }

    /// <summary>The component's supplied plane, for PlaneOutsideWorld.</summary>
    public int? Plane { get; init; }

    /// <summary>The configured world's plane count, for PlaneOutsideWorld.</summary>
    public int? PlaneCount { get; init; }
}
