using System;

namespace KhaozEngine.Attachments;

/// <summary>Stable format 1 equipment coverage refusal reasons.</summary>
public enum EquipmentCoverageRefusal
{
    /// <summary>The input is not one complete strict JSON value.</summary>
    InvalidJson,

    /// <summary>A schema object is required.</summary>
    ExpectedObject,

    /// <summary>A schema array is required.</summary>
    ExpectedArray,

    /// <summary>A schema string is required.</summary>
    ExpectedString,

    /// <summary>A required member is absent.</summary>
    MissingMember,

    /// <summary>A member is outside format 1.</summary>
    UnknownMember,

    /// <summary>A member appears more than once, including escaped spellings.</summary>
    DuplicateMember,

    /// <summary>Only format version 1 is supported.</summary>
    UnsupportedVersion,

    /// <summary>A digest is not 64 lowercase hexadecimal characters.</summary>
    MalformedDigest,

    /// <summary>A JSON string does not decode to well-formed UTF-16.</summary>
    InvalidText,

    /// <summary>An ordinal is not a 32-bit integer.</summary>
    InvalidInteger,

    /// <summary>A numeric field is not a finite binary32 value.</summary>
    NonFiniteNumber,

    /// <summary>A source range has invalid ordinals, count or end.</summary>
    InvalidRange,

    /// <summary>Source triangle ranges overlap.</summary>
    RangeOverlap,

    /// <summary>A companion must contain 1 to 4 commands.</summary>
    CommandCapacity,

    /// <summary>A command must contain 1 to 1024 support ranges.</summary>
    SupportRangeCapacity,

    /// <summary>A command may reference at most 1024 support triangles.</summary>
    SupportTriangleCapacity,

    /// <summary>A coverage frame must contain exactly 16 components.</summary>
    FrameComponentCapacity,

    /// <summary>A command ID must be a positive UInt32.</summary>
    InvalidCommandId,

    /// <summary>A part ID must be a positive UInt32.</summary>
    InvalidPartId,

    /// <summary>Command IDs must be unique.</summary>
    DuplicateCommand,

    /// <summary>A command must not be null.</summary>
    NullCommand,

    /// <summary>A host region must be non-blank, well-formed UTF-16.</summary>
    InvalidHostRegion,

    /// <summary>A coverage frame must be finite, affine, invertible and positively oriented.</summary>
    InvalidFrame,

    /// <summary>Collar width must be finite and positive.</summary>
    InvalidCollarWidth,

    /// <summary>Coverage caps must be finite with minZ below maxZ.</summary>
    InvalidCaps,

    /// <summary>Clearance must be finite and positive.</summary>
    InvalidClearance,

    /// <summary>Maximum world recession must be finite and non-negative.</summary>
    InvalidRecession,

}

/// <summary>Structured malformed-data refusal for equipment coverage. Messages are developer prose.</summary>
public sealed class EquipmentCoverageDataException : MalformedDataException
{
    /// <summary>Create a refusal, optionally preserving its original cause.</summary>
    public EquipmentCoverageDataException(EquipmentCoverageRefusal reason, string message, Exception? innerException = null)
        : base(message, innerException) => Reason = reason;

    /// <summary>The stable rule that refused the input.</summary>
    public EquipmentCoverageRefusal Reason { get; }

    /// <summary>Caller-supplied asset identity, set by the JSON reader.</summary>
    public string? Identity { get; init; }

    /// <summary>Rooted JSON path of the refused value or collection. When MemberNameOmitted is true, names the containing object.</summary>
    public string Path { get; init; } = "$";

    /// <summary>Observed count, or the first excess witness when enumeration is bounded.</summary>
    public long? Count { get; init; }

    /// <summary>Inclusive upper limit of the refused count.</summary>
    public long? Limit { get; init; }

    /// <summary>Missing, unknown or duplicated JSON member name.</summary>
    public string? MemberName { get; init; }

    /// <summary>True when an unknown name exceeds 64 decoded UTF-16 code units. MemberName is null and Path names its containing object.</summary>
    public bool MemberNameOmitted { get; init; }

    /// <summary>Refused format version when representable as an Int32.</summary>
    public int? FormatVersion { get; init; }

    /// <summary>Command ID involved in the refusal.</summary>
    public uint? CommandId { get; init; }

    /// <summary>Part ID involved in the refusal.</summary>
    public uint? PartId { get; init; }

    /// <summary>Host region key involved in the refusal.</summary>
    public string? RegionKey { get; init; }

    /// <summary>Source mesh ordinal involved in the refusal.</summary>
    public int? Mesh { get; init; }

    /// <summary>Source primitive ordinal involved in the refusal.</summary>
    public int? Primitive { get; init; }

    /// <summary>Source first support triangle ordinal involved in the refusal.</summary>
    public int? FirstTriangle { get; init; }

    /// <summary>Authored unsigned identifier, including a value exceeding UInt32, when representable.</summary>
    public ulong? IdentifierValue { get; init; }

    internal EquipmentCoverageDataException WithContext(string? identity = null, string? prefix = null) =>
        new(Reason, identity is null ? Message : $"{identity}: {Message}", this)
        {
            Identity = identity ?? Identity,
            Path = prefix is null ? Path : prefix + Path[1..],
            Count = Count,
            Limit = Limit,
            MemberName = MemberName,
            MemberNameOmitted = MemberNameOmitted,
            FormatVersion = FormatVersion,
            IdentifierValue = IdentifierValue,
            CommandId = CommandId,
            PartId = PartId,
            RegionKey = RegionKey,
            Mesh = Mesh,
            Primitive = Primitive,
            FirstTriangle = FirstTriangle,
        };
}
