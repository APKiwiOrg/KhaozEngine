using System.Text.Json;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>A compiler inventory or compiler file the reader refuses.</summary>
internal sealed class RendererPerfCompilerJsonException(RendererPerfCompilerJsonRefusal reason, string message)
    : JsonException(message)
{
    /// <summary>Why the reader refused.</summary>
    internal RendererPerfCompilerJsonRefusal Reason { get; } = reason;
}
