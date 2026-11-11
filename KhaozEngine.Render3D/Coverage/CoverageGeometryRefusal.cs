using System;

namespace KhaozEngine.Render3D;

/// <summary>Stable malformed-source, fixed-refinement and cold-support refusal rules.</summary>
public enum CoverageGeometryRefusal
{
    /// <summary>A source must contain displayed vertices and triangles.</summary>
    EmptySource,
    /// <summary>Source position and provenance arrays disagree in length. Count and Limit name the actual and expected counts.</summary>
    VertexArrayLength,
    /// <summary>Source position and skin arrays disagree in length. Count and Limit name the actual and expected counts.</summary>
    SkinArrayLength,
    /// <summary>A source position is not finite. Vertex names its source-array index.</summary>
    NonfinitePosition,
    /// <summary>A source vertex has an invalid provenance ordinal. Vertex names its source-array index.</summary>
    InvalidVertexProvenance,
    /// <summary>A source triangle has an invalid provenance ordinal. Triangle names its source-array index.</summary>
    InvalidTriangleProvenance,
    /// <summary>A source triangle ordinal is repeated for the same mesh, primitive and occurrence.</summary>
    DuplicateTriangle,
    /// <summary>A source triangle corner is outside the vertex array.</summary>
    TriangleIndex,
    /// <summary>A source triangle references another primitive, occurrence or material's vertex.</summary>
    TriangleOwnership,
    /// <summary>Raw influence and decoded skin array lengths disagree. Count and Limit name the actual and expected counts.</summary>
    InfluenceArrayLength,
    /// <summary>A decoded skin position disagrees with the source position.</summary>
    SkinPositionMismatch,
    /// <summary>Raw source influences disagree with decoded skin attributes.</summary>
    SkinInfluenceMismatch,
    /// <summary>An occurrence identity is repeated.</summary>
    DuplicateOccurrence,
    /// <summary>A material slice identity is repeated.</summary>
    DuplicateMaterial,
    /// <summary>A triangle has no declared occurrence identity.</summary>
    MissingOccurrence,
    /// <summary>A triangle has no declared material identity.</summary>
    MissingMaterial,
    /// <summary>A selected triangle range has invalid ordinals.</summary>
    InvalidTriangleRange,
    /// <summary>Selected triangle ranges overlap.</summary>
    TriangleRangeOverlap,
    /// <summary>A selected triangle range is absent from the displayed source.</summary>
    TriangleRangeMissing,
    /// <summary>Predicted final refinement vertices exceed 65536. Count and Limit are populated.</summary>
    RefinedVertexCapacity,
    /// <summary>Predicted final refinement triangles exceed 65536. Count and Limit are populated.</summary>
    RefinedTriangleCapacity,
    /// <summary>A support selects no inward triangles.</summary>
    EmptySupport,
    /// <summary>A support selects more than 1024 inward triangles. Count and Limit are populated.</summary>
    SupportTriangleCapacity,
    /// <summary>A support frame is not finite, positive and certifiably invertible affine.</summary>
    UnsupportedTransform,
    /// <summary>A support frame has condition estimate greater than one million.</summary>
    IllConditionedTransform,
    /// <summary>An inward triangle uses a nonopaque displayed material.</summary>
    NonopaqueSupport,
    /// <summary>A transformed source position cannot be bounded by finite floats.</summary>
    UncertifiablePosition,
    /// <summary>The support's projected scale cannot be certified.</summary>
    SupportScale,
    /// <summary>A support triangle is degenerate in projection.</summary>
    DegenerateSupportTriangle,
    /// <summary>The support sheet's projected winding is inconsistent.</summary>
    SupportWinding,
    /// <summary>The support sheet is nonmanifold.</summary>
    SupportTopology,
    /// <summary>A support boundary has fewer than three edges.</summary>
    SupportBoundaryTooSmall,
    /// <summary>A support boundary exceeds 256 edges. Count and Limit are populated.</summary>
    SupportBoundaryCapacity,
    /// <summary>The support boundary repeats a directed endpoint.</summary>
    SupportBoundaryNotSimple,
    /// <summary>The support boundary has a missing edge.</summary>
    SupportBoundaryGap,
    /// <summary>The support contains a hole or disconnected occurrence.</summary>
    SupportDisconnected,
    /// <summary>The support boundary is not convex.</summary>
    SupportNonconvex,
    /// <summary>Projected support triangles overlap.</summary>
    SupportOverlap,
    /// <summary>The support sheet does not cover its boundary.</summary>
    SupportIncomplete,
    /// <summary>Coincident projected vertices have different heights.</summary>
    SupportMultipleDepths,
    /// <summary>A support predicate has uncertifiable arithmetic.</summary>
    SupportPredicate,
    /// <summary>A support edge contains an unmatched vertex.</summary>
    SupportJunction,
    /// <summary>A selected sheet is the outer side of its displayed closed solid.</summary>
    SupportOuterSide,
    /// <summary>A finite boundary query cannot be certified numerically. Identity, X and Y are populated.</summary>
    UncertifiableBoundaryQuery,
}

/// <summary>Structured malformed-data refusal for source evidence, refinement and support compilation.</summary>
public sealed class CoverageGeometryDataException : MalformedDataException
{
    public CoverageGeometryDataException(CoverageGeometryRefusal reason, string message) : base(message) => Reason = reason;
    public CoverageGeometryRefusal Reason { get; }
    public string? Identity { get; init; }
    /// <summary>Observed count or first excess witness.</summary>
    public long? Count { get; init; }
    /// <summary>Inclusive capacity of the refused collection.</summary>
    public long? Limit { get; init; }
    public int? Mesh { get; init; }
    public int? Primitive { get; init; }
    /// <summary>Source-array vertex index, or the invalid corner value for TriangleIndex.</summary>
    public int? Vertex { get; init; }
    /// <summary>Source-array triangle index, or the authored first ordinal for a range refusal.</summary>
    public int? Triangle { get; init; }
    public int? Node { get; init; }
    public int? MaterialSlice { get; init; }
}

/// <summary>Structured numerical refusal of a finite boundary query, preserving the original ArgumentException base.</summary>
public sealed class CoverageGeometryArgumentException : ArgumentException
{
    public CoverageGeometryArgumentException(CoverageGeometryRefusal reason, string message) : base(message) => Reason = reason;
    public CoverageGeometryRefusal Reason { get; }
    public string? Identity { get; init; }
    /// <summary>The finite query point's X coordinate.</summary>
    public double X { get; init; }
    /// <summary>The finite query point's Y coordinate.</summary>
    public double Y { get; init; }
}
