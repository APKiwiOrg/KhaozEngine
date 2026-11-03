using System;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>A compiler-read result whose encoding the reservation does not admit.</summary>
internal sealed class RendererPerfCompilerEncodingException(RendererPerfCompilerEncodingRefusal reason, string message)
    : InvalidOperationException(message)
{
    /// <summary>Why the encoding was refused.</summary>
    internal RendererPerfCompilerEncodingRefusal Reason { get; } = reason;

    /// <summary>The bytes the encoding had reached when it was refused.</summary>
    internal long RequiredBytes { get; init; }

    /// <summary>The reserved encoding capacity in bytes.</summary>
    internal int ReservationBytes { get; init; }
}
