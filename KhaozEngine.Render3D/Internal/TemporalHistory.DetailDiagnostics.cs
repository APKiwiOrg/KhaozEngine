namespace KhaozEngine.Render3D.Internal;

internal sealed partial class TemporalHistory
{
    /// <summary>Request an extra live resolve output for test-only path readback before binding Fine.
    /// It borrows this history generation and is absent from production passes.</summary>
    internal bool CaptureDetailPathForTests { get; set; }
}
