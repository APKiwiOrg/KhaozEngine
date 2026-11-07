using System;

namespace KhaozEngine.Render3D;

/// <summary>Stable format 1 host coverage refusal reasons.</summary>
public enum HostCoverageRefusal
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

    /// <summary>A region key or authoring label is blank or malformed.</summary>
    InvalidLabel,

    /// <summary>Topology must contain 1 to 65536 source render vertices.</summary>
    TopologyCapacity,

    /// <summary>A map must contain 1 to 32 regions.</summary>
    RegionCapacity,

    /// <summary>A region must contain 1 to 65536 triangle ranges.</summary>
    RegionTriangleCapacity,

    /// <summary>A region may reference at most 65536 triangles.</summary>
    RegionTriangleTotalCapacity,

    /// <summary>A region must contain 1 to 65536 surface strengths.</summary>
    StrengthCapacity,

    /// <summary>All regions together may reference at most 65536 triangles.</summary>
    TotalTriangleCapacity,

    /// <summary>Refinement may contain at most 65536 ranges.</summary>
    RefinementRangeCapacity,

    /// <summary>Refinement may reference at most 65536 triangles.</summary>
    RefinementTriangleCapacity,

    /// <summary>Topology ordinals and surface IDs must be non-negative.</summary>
    InvalidTopology,

    /// <summary>A strength entry's surface ID must be non-negative.</summary>
    InvalidSurfaceId,

    /// <summary>A source render vertex appears more than once.</summary>
    DuplicateTopologyVertex,

    /// <summary>Region keys must be unique.</summary>
    DuplicateRegion,

    /// <summary>A triangle role must be core or collar.</summary>
    InvalidRole,

    /// <summary>A surface strength must be finite and in [0,1].</summary>
    InvalidStrength,

    /// <summary>A region declares a surface strength more than once.</summary>
    DuplicateStrength,

    /// <summary>A region strength refers to a surface absent from topology.</summary>
    UnknownSurface,

    /// <summary>A surface has positive strength in multiple regions.</summary>
    PositiveStrengthOverlap,

    /// <summary>A core triangle vertex must have full strength, checked when host geometry is bound.</summary>
    CoreStrength,

    /// <summary>A collar triangle vertex must have an authored strength, checked when host geometry is bound.</summary>
    CollarStrength,

    /// <summary>A refinement level must be between 1 and 4 inclusive.</summary>
    RefinementLevel,

    /// <summary>A region entry must not be null.</summary>
    NullRegion,

}

/// <summary>Structured malformed-data refusal for host coverage. Messages are developer prose.</summary>
public sealed class HostCoverageDataException : MalformedDataException
{
    /// <summary>Create a refusal, optionally preserving its original cause.</summary>
    public HostCoverageDataException(HostCoverageRefusal reason, string message, Exception? innerException = null)
        : base(message, innerException) => Reason = reason;

    /// <summary>The stable rule that refused the input.</summary>
    public HostCoverageRefusal Reason { get; }

    /// <summary>Caller-supplied asset identity, set by the JSON reader.</summary>
    public string? Identity { get; init; }

    /// <summary>Rooted JSON path of the refused value or collection.</summary>
    public string Path { get; init; } = "$";

    /// <summary>Observed count, or the first excess witness when enumeration is bounded.</summary>
    public long? Count { get; init; }

    /// <summary>Inclusive upper limit of the refused count.</summary>
    public long? Limit { get; init; }

    /// <summary>Missing, unknown or duplicated JSON member name.</summary>
    public string? MemberName { get; init; }

    /// <summary>Refused format version when representable as an Int32.</summary>
    public int? FormatVersion { get; init; }

    /// <summary>Region key involved in the refusal.</summary>
    public string? RegionKey { get; init; }

    /// <summary>Welded surface ID involved in the refusal.</summary>
    public int? SurfaceId { get; init; }

    /// <summary>Source mesh ordinal involved in the refusal.</summary>
    public int? Mesh { get; init; }

    /// <summary>Source primitive ordinal involved in the refusal.</summary>
    public int? Primitive { get; init; }

    /// <summary>Source render vertex ordinal involved in the refusal.</summary>
    public int? Vertex { get; init; }

    /// <summary>Source first triangle ordinal involved in the refusal.</summary>
    public int? FirstTriangle { get; init; }

    internal HostCoverageDataException WithContext(string? identity = null, string? prefix = null, string? path = null) =>
        new(Reason, identity is null ? Message : $"{identity}: {Message}", this)
        {
            Identity = identity ?? Identity,
            Path = path ?? (prefix is null ? Path : prefix + Path[1..]),
            Count = Count,
            Limit = Limit,
            MemberName = MemberName,
            FormatVersion = FormatVersion,
            RegionKey = RegionKey,
            SurfaceId = SurfaceId,
            Mesh = Mesh,
            Primitive = Primitive,
            Vertex = Vertex,
            FirstTriangle = FirstTriangle,
        };
}
