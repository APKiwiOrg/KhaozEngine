namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Stable reasons the compiler-read byte proof refuses an encoded result.</summary>
internal enum RendererPerfCompilerEncodingRefusal
{
    /// <summary>The encoded report, its evidence and the fixed association syntax need more bytes than the read
    /// profile reserved.</summary>
    AboveReservation
}
