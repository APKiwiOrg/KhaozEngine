using System;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>A stance producer refusal retaining the InvalidOperationException catch contract.</summary>
public sealed class MapStanceException : InvalidOperationException
{
    /// <summary>Creates a refusal with the original developer-facing message.</summary>
    public MapStanceException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>Creates a refusal retaining its original cause.</summary>
    public MapStanceException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;
    /// <summary>The rule that refused stance generation.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The callback or seated component that failed.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The placement whose stance was being sampled.</summary>
    public string? PlacementId { get; init; }
    /// <summary>The nonfinite producer result.</summary>
    public double? ActualNumber { get; init; }
    /// <summary>The resolver version requiring the missing callback.</summary>
    public int? ResolverVersion { get; init; }
}
