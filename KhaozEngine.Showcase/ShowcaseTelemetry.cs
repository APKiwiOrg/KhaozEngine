using System;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>
    /// The showcase's env-gated telemetry capture lever. Set <c>KE_TELEMETRY_PATH=&lt;file&gt;</c> and the run
    /// streams a <see cref="TelemetryRecorder"/> session to that file. Leave it unset or blank and nothing is
    /// armed, so an ordinary showcase boot writes nothing and costs nothing.
    /// <para>
    /// WHY THE TESTBED NEEDS ONE. A GPU backend's rollout gates are stated against FIELD numbers, and the
    /// showcase is the engine's own windowed testbed, so those numbers have to be obtainable here rather than
    /// only inside a consuming game. Before this, <c>TelemetryRecorder</c> was armed by Ruinborne and by nothing
    /// in this repo, which put every engine-side field reading behind a game checkout.
    /// </para>
    /// <para>
    /// THE ARMING SHAPE IS RUINBORNE'S, deliberately: own the recorder, own a <see cref="FrameStats"/> and meter
    /// it every frame, resolve the session header ONCE at start so it describes the run being recorded, then
    /// append raw numeric channels on a throttle. What is dropped is the in-game arm/confirm UX and the F3
    /// chord, which are player-facing concerns a testbed has no use for, and the timestamped filename, because a
    /// gate capture is named by the caller that will read it.
    /// </para>
    /// <para>
    /// IT IS THE SHOWCASE'S OWN LEVER AND WIDENS NO ENGINE API. Everything it reads is already public:
    /// <see cref="AppWindow.BackendSelection"/> and its siblings for the header, and
    /// <see cref="AppWindow.Counters"/> through <see cref="GpuTelemetryChannels"/> for the per-sample GPU
    /// numbers. Nothing here belongs at the <c>GameAppOptions</c> level, since a game that wants a capture arms
    /// the same public recorder its own way, exactly as Ruinborne does.
    /// </para>
    /// <para>
    /// ONE RECORDING PER PROCESS, opened at load and closed on dispose. There is no start/stop chord on purpose:
    /// a gate capture is bounded by <c>KE_MAX_FRAMES</c>, and a hand-timed window is precisely the variable a
    /// backend A/B must not carry.
    /// </para>
    /// <para>
    /// THE CAPTURE CARRIES ITS OWN BUILD LINE. The engine writes the recording assembly's informational version
    /// into the header, and SourceLink stamps the commit onto it, so a session says which engine version AND
    /// which commit produced it without anything being pinned by hand alongside the file.
    /// </para>
    /// </summary>
    public sealed class ShowcaseTelemetry : IDisposable
    {
        /// <summary>The environment variable that names the capture file. Blank or unset means do not record.</summary>
        public const string PathVariable = "KE_TELEMETRY_PATH";

        /// <summary>
        /// Seconds between sample rows, matching Ruinborne's capture cadence so files off the two are read the
        /// same way. The GPU counters are CUMULATIVE, so the cadence never loses a count: a window's total is the
        /// last row minus the first, whatever rate the rows came at.
        /// </summary>
        public const float SampleSeconds = 0.1f;

        readonly TelemetryRecorder _recorder = new();
        readonly FrameStats _frameStats = new();
        readonly List<TelemetryChannel> _channels = new(9 + GpuTelemetryChannels.ChannelCount);
        float _sampleTimer;
        long _frames;

        /// <summary>True once <see cref="Start"/> opened a file, false when the lever was never pulled.</summary>
        public bool IsRecording => _recorder.IsRecording;

        /// <summary>The file being written, or null when nothing is armed.</summary>
        public string? CurrentPath => _recorder.CurrentPath;

        /// <summary>
        /// The capture path from the environment, or null when the lever is not pulled. Blank is treated as
        /// unset, so an exported-but-empty variable does not create a file nobody asked for.
        /// </summary>
        public static string? ResolvePath()
        {
            string? path = Environment.GetEnvironmentVariable(PathVariable);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        /// <summary>
        /// Arm the recorder against <see cref="ResolvePath"/> if the lever is pulled, resolving the session
        /// header from <paramref name="window"/>. No-op when the variable is unset. A failure to open is logged
        /// and swallowed: a capture must never take the showcase down with it.
        /// </summary>
        /// <returns>True when a recording was opened.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
        public bool Start(AppWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);

            string? path = ResolvePath();
            if (path is null) return false;

            try
            {
                _recorder.Start(path, SessionInfo(window));
                _sampleTimer = 0f;   // take the first row on the next frame
                Log.Get("Showcase").Info($"Telemetry capture -> {path}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Get("Showcase").Warn($"Telemetry capture failed to start (non-fatal): {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>The session header for a live <paramref name="window"/>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
        public static TelemetrySessionInfo SessionInfo(AppWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            return SessionInfo(window.BackendSelection, window.AdapterDescription, window.InjectedModules,
                window.ThreadingCaps);
        }

        /// <summary>
        /// The session header from plain values, so the mapping is assertable with no window and no device. The
        /// app identity is the showcase's own, and the GPU block comes from the engine's one-call bridge rather
        /// than being re-derived here.
        /// </summary>
        public static TelemetrySessionInfo SessionInfo(
            GpuBackendSelection selection,
            string? adapterDescription,
            IReadOnlyList<string>? injectedModules,
            GpuThreadingCaps? threadingCaps)
        {
            var info = new TelemetrySessionInfo
            {
                AppName = "KhaozEngine Showcase",
                AppVersion = AppVersion,
            };
            info.WithGpu(selection, adapterDescription, injectedModules, threadingCaps);
            return info;
        }

        /// <summary>
        /// The showcase head's informational version, which SourceLink stamps with the commit. Resolved once.
        /// </summary>
        public static string AppVersion { get; } =
            typeof(ShowcaseTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ShowcaseTelemetry).Assembly.GetName().Version?.ToString()
            ?? "unknown";

        /// <summary>
        /// Meter this frame and append a sample row when the throttle is due. Call once per frame with the RAW
        /// frame delta (not the time-scaled one), so a capture measures the machine rather than the simulation.
        /// No-op when nothing is armed.
        /// </summary>
        /// <param name="rawDt">The unscaled frame delta in seconds.</param>
        /// <param name="elapsedRealSeconds">Unscaled seconds since the run started, written as the row's <c>t</c>.</param>
        /// <param name="window">The live window, read for this frame's GPU counters.</param>
        /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
        public void Sample(float rawDt, float elapsedRealSeconds, AppWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            if (!_recorder.IsRecording) return;

            // Metered every frame even between rows, so the fps window is the real one rather than a 10 Hz
            // subsample of it.
            _frameStats.Sample(rawDt);
            _frames++;

            _sampleTimer -= rawDt;
            if (_sampleTimer > 0f) return;
            _sampleTimer = SampleSeconds;

            _channels.Clear();

            // Loop frames since the capture opened. It is the denominator that makes a capture SELF-VERIFYING:
            // GpuDeviceCounters.FramesBegun would serve, but a backend with no counters (the Veldrid Metal
            // incumbent is one) writes no GPU columns at all, and then nothing in the file says whether the rows
            // describe the frames the run was asked for. Divide it by the elapsed t and a wrong reading has
            // nowhere to hide.
            _channels.Add(new TelemetryChannel("frames", _frames));

            _channels.Add(new TelemetryChannel("fps", _frameStats.Fps));
            _channels.Add(new TelemetryChannel("frameMsAvg", _frameStats.FrameMsAvg));
            _channels.Add(new TelemetryChannel("frameMsMin", _frameStats.FrameMsMin));
            _channels.Add(new TelemetryChannel("frameMsMax", _frameStats.FrameMsMax));
            _channels.Add(new TelemetryChannel("managedMB", _frameStats.ManagedBytes / (1024d * 1024d)));

            // The backend the frame actually ran on, per row rather than only in the header: a capture read out
            // of order, or truncated by a crash, still says what it was measuring.
            _channels.Add(new TelemetryChannel("gpuBackend", (int)window.BackendSelection.Backend));
            _channels.Add(new TelemetryChannel("gpuBackendSource", (int)window.BackendSelection.Source));

            // The soak counters. A device that counted nothing appends nothing (see GpuTelemetryChannels), so
            // absent columns and columns of zeros stay opposite facts.
            GpuTelemetryChannels.AppendTo(_channels, window.Counters);

            _recorder.Sample(elapsedRealSeconds, _channels);
        }

        /// <summary>Flush and close any recording. Safe when nothing was armed.</summary>
        public void Dispose() => _recorder.Dispose();
    }
}
