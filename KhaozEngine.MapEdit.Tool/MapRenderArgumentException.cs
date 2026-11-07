using System;
using System.Numerics;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEdit;

/// <summary>A render view argument refusal preserving its ArgumentException catch contract.</summary>
public sealed class MapRenderArgumentException : ArgumentException
{
    public MapRenderArgumentException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    public MapRenderArgumentException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;
    /// <summary>The render view rule that refused the request.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The submitted eye position.</summary>
    public Vector3? Eye { get; init; }
    /// <summary>The submitted target position.</summary>
    public Vector3? Target { get; init; }
}
