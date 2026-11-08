using System;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>A publication account refused before any compiler file was written.</summary>
internal sealed class RendererPerfPublicationCapacityException(RendererPerfPublicationCapacityRefusal reason, string message)
    : InvalidOperationException(message)
{
    /// <summary>Why the account refused.</summary>
    internal RendererPerfPublicationCapacityRefusal Reason { get; } = reason;
}
