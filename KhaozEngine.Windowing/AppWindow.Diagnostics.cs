using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// Read-only GPU-diagnostics accessors on <see cref="AppWindow"/>: facts about the device the window created
    /// that a game surfaces in its own debug overlay or writes into a bug report. They are here rather than in
    /// <c>AppWindow.cs</c> because that file is at its size ceiling, and because this is a distinct concern from
    /// the frame loop: nothing on this partial participates in windowing, input, or presentation.
    /// </summary>
    public sealed partial class AppWindow
    {
        /// <summary>
        /// The graphics driver's multi-threading capabilities, on Direct3D11 ONLY. Null on every other backend,
        /// off Windows, and when the query failed. Render it with <see cref="GpuThreadingDiagnostics.Describe"/>,
        /// which turns all three of those into one honest "unknown".
        /// <para>
        /// A false <see cref="GpuThreadingCaps.DriverCommandLists"/> is the case worth putting on screen: it means
        /// Windows is emulating Direct3D11 command lists in software instead of the driver building them, which
        /// costs CPU time on every recorded command and can cost an order of magnitude of frame rate. The engine
        /// already logs a warning for it at device creation, so an overlay row is for the player who never opens
        /// a log.
        /// </para>
        /// </summary>
        public GpuThreadingCaps? ThreadingCaps => _gpu.ThreadingCaps;
    }
}
