using System;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>A stance shape refusal retaining the NotSupportedException catch contract.</summary>
public sealed class MapStanceUnsupportedException : NotSupportedException
{
    /// <summary>Creates a refusal with the original developer-facing message.</summary>
    public MapStanceUnsupportedException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    /// <summary>Creates a refusal retaining its original cause.</summary>
    public MapStanceUnsupportedException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;
    /// <summary>The rule that refused the shape.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The unsupported runtime shape type.</summary>
    public Type? ShapeType { get; init; }
}
