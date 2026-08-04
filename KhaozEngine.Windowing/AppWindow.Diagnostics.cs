using System.Collections.Generic;
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

        /// <summary>
        /// The adapter this window's device is running on, or an empty string when the backend reports none. On
        /// Direct3D11 it is exactly the DXGI adapter description, so it is the line a bug report needs to say
        /// which physical card rendered. The same value as <c>Capabilities.DeviceName</c>, named for the reader
        /// who is chasing a Direct3D11 problem and would not think to look under capabilities.
        /// </summary>
        public string AdapterDescription => _gpu.AdapterDescription;

        /// <summary>
        /// Known third-party overlay / capture software found hooked into this process when the device was
        /// created, or null when nothing was scanned (off Windows, or the scan failed). An empty list means the
        /// scan ran and found none, which is the opposite fact from null, so render it with
        /// <see cref="GpuInjectedModules.Describe"/> rather than testing the count.
        /// <para>
        /// Worth a row in a debug overlay: this software injects itself into Direct3D and is a known cause of
        /// stutter, corrupted frames, and driver crashes that look like engine bugs. The engine already warns in
        /// the log when the list is non-empty.
        /// </para>
        /// </summary>
        public IReadOnlyList<string>? InjectedModules => _gpu.InjectedModules;

        /// <summary>
        /// The LIVE device diagnostics: whether this window's device is running on a software rasterizer, and why
        /// it was lost if it has been. Read through to the device on every access rather than captured, because a
        /// device loss happens at an arbitrary moment long after creation and a cached value would always say the
        /// device was fine. A null member means the backend does not report that fact, which is what every
        /// backend on the Veldrid path answers for both.
        /// <para>
        /// The same value as <c>GpuDeviceContext.Diagnostics</c>, here because a windowed game holds an
        /// <see cref="AppWindow"/> and not the context, and without this it cannot reach either fact for its own
        /// overlay or its own bug report, nor hand them to the telemetry session header.
        /// </para>
        /// </summary>
        public GpuDeviceDiagnostics Diagnostics => _gpu.Diagnostics;

        /// <summary>
        /// The LIVE soak counters of this window's device: how long it has spent waiting for the GPU to go idle,
        /// how often a frame boundary blocked on a uniform ring segment the GPU was still reading, and how many
        /// device-level buffer writes were queued against an in-flight segment. All cumulative since the device
        /// was created, and read through on every access because they move every frame.
        /// <para>
        /// Feed them to a telemetry recording with <see cref="GpuTelemetryChannels.AppendTo"/>, which appends
        /// nothing at all on a backend that keeps no counters, so a capture never carries zeros that would read as
        /// a clean soak on a device that never looked.
        /// </para>
        /// </summary>
        public GpuDeviceCounters Counters => _gpu.Counters;
    }
}
