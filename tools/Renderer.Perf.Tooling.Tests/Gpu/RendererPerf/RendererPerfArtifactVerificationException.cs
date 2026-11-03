using System.IO;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>A retained artifact that changed under the verifier's held handle.</summary>
internal sealed class RendererPerfArtifactVerificationException(RendererPerfArtifactVerificationRefusal reason, string message)
    : IOException(message)
{
    /// <summary>Why verification refused the file.</summary>
    internal RendererPerfArtifactVerificationRefusal Reason { get; } = reason;

    /// <summary>The artifact path relative to the verified directory.</summary>
    internal string? Path { get; init; }
}
