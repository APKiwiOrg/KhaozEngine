namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Stable reasons the artifact verifier's final rehash refuses a retained file.</summary>
internal enum RendererPerfArtifactVerificationRefusal
{
    /// <summary>A retained file's length, readable bytes or SHA-256 changed after it was decoded.</summary>
    ContentChanged
}
