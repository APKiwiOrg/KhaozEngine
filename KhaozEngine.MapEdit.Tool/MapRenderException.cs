using System;
using KhaozEngine.Gpu;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEdit;

/// <summary>A render capture refusal preserving its InvalidOperationException catch contract and original cause.</summary>
public sealed class MapRenderException : InvalidOperationException
{
    public MapRenderException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;
    public MapRenderException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;
    /// <summary>The known lower document or render rule, or RenderFailed for a capture failure without one.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The selected graphics backend.</summary>
    public GpuBackendKind? Backend { get; init; }
    /// <summary>The render operation whose capture failed.</summary>
    public string? Operation { get; init; }
    /// <summary>The original document refusal's complete typed context, when one is available.</summary>
    public MapDocumentFinding? Finding { get; init; }
}
