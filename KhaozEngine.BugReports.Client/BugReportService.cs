using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Windowing.Actions;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// In-game bug reports for a <see cref="GameApp"/>: the trigger key, the rolling clip, the built-in sections, the
/// confirmation screen and the toasts, as one overlay above every screen the game shows.
/// <para>The constructor adds the overlay, which creates <see cref="Recorder"/> from the window and starts it when
/// <see cref="BugReportServiceOptions.RecordingEnabled"/> is set and the device can record. The seven built-in
/// sections (<c>build</c>, <c>platform</c>, <c>graphics</c>, <c>session</c>, <c>input</c>, <c>performance</c> and
/// <c>log</c>) are registered ahead of any <see cref="AddSection"/>, and <see cref="RecentLog"/> is attached to
/// <see cref="Log.Manager"/> when one is configured.</para>
/// <para>The platform facts that never change, the admitted <c>KE_</c> variables among them, are read once here. The
/// previous crash scan runs once on the thread pool over the directory of
/// <see cref="BugReportServiceOptions.SessionLogPath"/>, and is skipped when no session log is set. Until it finishes
/// the <c>session</c> section reports no previous crash, so the trigger frame never touches the disk.</para>
/// <para>Construct, call and dispose it on the frame thread. <see cref="RetryQueuedAsync"/> completes off it, and
/// <see cref="ReportCaptured"/> and <see cref="ReportSubmitted"/> are raised on it.</para>
/// </summary>
public sealed class BugReportService : IDisposable
{
    /// <summary>The action id of the report trigger in <see cref="TriggerControls"/>.</summary>
    public const string TriggerActionId = BugReportFlow.TriggerActionId;

    const float FontPixelHeight = 18f;

    /// <summary>The longest <see cref="Dispose"/> waits for sends still building their bundle.</summary>
    internal static readonly TimeSpan ShutdownBuildWait = TimeSpan.FromSeconds(3);

    readonly BugReportServiceOptions _options;
    readonly TimeProvider _time;
    readonly CancellationTokenSource _shutdown = new();
    readonly BugReportSectionSet _sections = new();
    readonly ReportRedactor _redactor = new();
    readonly FrameStats _frameStats = new();
    readonly FrameTimeRing _frameTimes = new();
    readonly PlatformInvariants _invariants;
    readonly DateTimeOffset _processStart;
    readonly BuildFacts _build;
    readonly ServiceOverlay _overlay;
    readonly Func<IGameOverlay, bool> _removeOverlay;
    readonly BugReportFlow _flow;
    readonly DetachableLogSink _logLink;
    PreviousCrash? _previousCrash;
    Func<PassTimings?> _passes = static () => null;
    GameOverlayContext? _context;
    DpiFont? _font;
    Texture2D? _white;
    ToastView? _toasts;
    bool _disposed;

    /// <summary>Adds bug reports to <paramref name="app"/>. Call from the frame thread.</summary>
    /// <param name="app">The game. Its overlay set receives the report overlay.</param>
    /// <param name="options">Who the game is, the trigger key, the categories and the clip.</param>
    /// <param name="sink">Where finished reports go, such as <see cref="HttpRelaySink"/> or <see cref="LocalFolderSink"/>.</param>
    /// <param name="queue">Keeps every report on disk until the sink accepts or rejects it.</param>
    /// <param name="currentSubject">
    /// Returns the signed-in account, or null while signed out. Polled on the frame thread every frame, and also called on
    /// the thread that calls <see cref="RetryQueuedAsync"/>, so it must be safe to call from both. A call that throws
    /// is logged once per distinct failure and keeps the last known account.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">An option is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public BugReportService(GameApp app, BugReportServiceOptions options, IBugReportSink sink, BugReportOfflineQueue queue,
        Func<string?> currentSubject)
        : this(options, currentSubject, new BugReportSubmitter(sink, queue), TimeProvider.System, Required(app).RemoveOverlay,
            service => service.AttachTo(app))
    {
    }

    /// <summary>The headless seam: no window, no graphics section, and the caller's capture and screens.</summary>
    internal BugReportService(BugReportServiceOptions options, FrameHistoryRecorder recorder, IBugReportCapture capture,
        BugReportSubmitter submitter, Func<string?> currentSubject, Func<BugReportScreenModel, BugReportScreen> screenFactory,
        Func<IGameOverlay, bool> removeOverlay, TimeProvider time)
        : this(options, currentSubject, submitter, time, removeOverlay,
            _ => new Attachment(recorder, capture, screenFactory, static _ => null, Window: null))
    {
    }

    BugReportService(BugReportServiceOptions options, Func<string?> currentSubject, BugReportSubmitter submitter,
        TimeProvider time, Func<IGameOverlay, bool> removeOverlay, Func<BugReportService, Attachment> attach)
    {
        BugReportServiceOptions.Validate(options);
        ArgumentNullException.ThrowIfNull(currentSubject);
        ArgumentNullException.ThrowIfNull(submitter);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(removeOverlay);
        _options = options;
        _time = time;
        _removeOverlay = removeOverlay;
        _overlay = new ServiceOverlay(this);
        _logLink = new DetachableLogSink(RecentLog);
        _invariants = ClientFacts.CaptureInvariants(_redactor);
        _processStart = ProcessStart(time);
        _build = ClientFacts.Build(null, options.GameId, options.GameDisplayName);

        Attachment attached = attach(this);
        Recorder = attached.Recorder;
        try
        {
            _flow = new BugReportFlow(options, attached.Capture, _sections, submitter, _redactor, currentSubject,
                attached.ScreenFactory, attached.TextureFactory, time, _shutdown.Token);
            AddBuiltInSections(attached.Window);
            if (options.RecordingEnabled && Recorder.IsAvailable) Recorder.Start();
        }
        catch
        {
            removeOverlay(_overlay);
            Recorder.Dispose();
            throw;
        }
        if (Log.Manager is { } manager) manager.AddSink(_logLink);
        CrashScan = StartCrashScan(options.SessionLogPath);
    }

    /// <summary>The rolling clip and screenshot recorder. <see cref="FrameHistoryRecorder.Suspend"/> and
    /// <see cref="FrameHistoryRecorder.Resume"/> keep a pause menu out of the clip.</summary>
    public FrameHistoryRecorder Recorder { get; }

    /// <summary>The trigger binding, for a controls screen to show and rebind. Rebinds save through
    /// <see cref="BugReportServiceOptions.SaveTriggerBindings"/>.</summary>
    public ActionMapController TriggerControls => _flow.TriggerControls;

    /// <summary>The recent log lines the <c>log</c> section writes.</summary>
    public RecentLogSink RecentLog { get; } = new();

    /// <summary>True from the trigger frame until the confirmation screen closes.</summary>
    public bool IsOpen => _flow.State != BugReportFlowState.Idle;

    /// <summary>Raised in the trigger frame with the new report id, for example to ask the server for its snapshot.</summary>
    public event Action<string>? ReportCaptured
    {
        add => _flow.ReportCaptured += value;
        remove => _flow.ReportCaptured -= value;
    }

    /// <summary>Raised on the frame thread with each submission's result.</summary>
    public event Action<BugReportSubmitResult>? ReportSubmitted
    {
        add => _flow.ReportSubmitted += value;
        remove => _flow.ReportSubmitted -= value;
    }

    /// <summary>The overlay <see cref="GameApp"/> runs. Tests drive it directly.</summary>
    internal IGameOverlay Overlay => _overlay;

    internal BugReportFlow Flow => _flow;

    /// <summary>The previous crash scan, completed at once when there is no session log. Tests await it.</summary>
    internal Task CrashScan { get; }

    /// <summary>Registers a game section after the built-in ones. See <see cref="BugReportSectionSet.Add"/>.</summary>
    public void AddSection(IBugReportSection section) => _sections.Add(section);

    /// <summary>Opens a report from a menu on the next frame. False while a report is captured or open.</summary>
    public bool Begin() => _flow.Begin();

    /// <summary>Starts or stops the rolling clip, such as from a settings toggle.</summary>
    public void SetRecordingEnabled(bool enabled)
    {
        if (enabled) Recorder.Start();
        else Recorder.Stop();
    }

    /// <summary>
    /// Sets where the <c>performance</c> section reads per-pass timings. The source returns null while there is no meter.
    /// </summary>
    public void SetPassTimingsSource(Func<PassTimings?> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _passes = source;
    }

    /// <summary>
    /// Sends the queued reports for the signed-in account. Returns how many the sink accepted. It reads the account
    /// through the <c>currentSubject</c> delegate on the calling thread, then sends on the thread pool.
    /// </summary>
    public Task<int> RetryQueuedAsync(CancellationToken cancellationToken) => _flow.RetryQueuedAsync(cancellationToken);

    /// <summary>
    /// Removes the overlay, tells every send still building its bundle to stop waiting for its screenshot, waits up to
    /// 3 s for each to write its bundle to the offline queue,
    /// closes a report that is still open and disposes its thumbnails, then cancels every send and flush, disposes the
    /// recorder, detaches <see cref="RecentLog"/> and disposes the font. Each step after the cancel runs even when an
    /// earlier one throws. A send cut off here stays in the offline queue, without its screenshot when that was still
    /// pending. A report is lost only when its build is still running after the 3 s wait and the process ends first,
    /// or when the process is killed. Call from the frame thread.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The overlay goes first, so no later frame can thaw the disposed recorder.
        _removeOverlay(_overlay);
        // A send still waiting for its screenshot stops waiting, so the bounded wait below covers the build alone.
        _flow.SignalShutdown();
        if (!_flow.Builds.WaitAll(ShutdownBuildWait))
            Log.Warn("A bug report was still being built at shutdown. It reaches the offline queue only if the process runs on.");
        // A report still open is dropped with its screen and thumbnail textures, before the recorder goes.
        _flow.CloseActiveReport();
        // The source stays undisposed, because a send still finishing registers on its token.
        _shutdown.Cancel();
        try
        {
            Recorder.Dispose();
        }
        finally
        {
            _logLink.Detach();
            try
            {
                _font?.Dispose();
            }
            finally
            {
                _white?.Dispose();
            }
        }
    }

    static GameApp Required(GameApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app;
    }

    /// <summary>When the process started, read once. Falls back to now where the platform does not say.</summary>
    static DateTimeOffset ProcessStart(TimeProvider time)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return time.GetUtcNow();
        }
    }

    /// <summary>
    /// Adds the overlay to <paramref name="app"/>. Its <see cref="IGameOverlay.Attach"/> runs inside
    /// <see cref="GameApp.AddOverlay"/> on this thread, so the window and the recorder exist when this returns.
    /// </summary>
    Attachment AttachTo(GameApp app)
    {
        app.AddOverlay(_overlay);
        FrameHistoryRecorder recorder = _overlay.Recorder
            ?? throw new InvalidOperationException("GameApp.AddOverlay did not attach the report overlay.");
        return new Attachment(recorder, new RecorderCapture(recorder), CreateScreen, CreateTexture, _context!.Window);
    }

    /// <summary>The crash directory is the session log's. The scan runs once, off the frame thread.</summary>
    Task StartCrashScan(string? sessionLogPath)
    {
        if (string.IsNullOrWhiteSpace(sessionLogPath)) return Task.CompletedTask;
        DateTimeOffset start = _processStart;
        return Task.Run(() =>
        {
            try
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(sessionLogPath));
                if (directory is null) return;
                (string? file, DateTimeOffset? at) = ClientFacts.FindPreviousCrash(directory, start);
                Volatile.Write(ref _previousCrash, new PreviousCrash(file, at));
            }
            catch (Exception exception)
            {
                Log.Warn("The previous crash scan failed.", exception);
            }
        });
    }

    void AddBuiltInSections(AppWindow? window)
    {
        _sections.Add(new BuildSection(() => _build));
        _sections.Add(new PlatformSection(() => ClientFacts.Platform(_invariants, TriggerFrame(), _context?.Ui)));
        if (window is not null) _sections.Add(new GraphicsSection(() => ClientFacts.Graphics(window, TriggerFrame())));
        _sections.Add(new SessionSection(SessionFacts));
        _sections.Add(new InputSection(() => new InputFacts(ClientFacts.Devices(TriggerFrame()),
            ClientFacts.DescribeBindings(_flow.TriggerControls.Map, TriggerActionId))));
        _sections.Add(new PerformanceSection(_frameStats, _frameTimes, () => _passes()));
        _sections.Add(new LogSection(RecentLog));
    }

    /// <summary>The input the flow stored for the frame being captured. Sections never call a live window getter.</summary>
    InputState TriggerFrame() =>
        _flow.LastFrameInput ?? throw new InvalidOperationException("No frame input has been stored yet.");

    SessionFacts SessionFacts()
    {
        PreviousCrash? crash = Volatile.Read(ref _previousCrash);
        return new SessionFacts(_time.GetUtcNow() - _processStart, _options.SessionLogPath, crash?.File, crash?.At);
    }

    BugReportScreen CreateScreen(BugReportScreenModel model)
    {
        GameOverlayContext context = _context!;
        SpriteFont font = EnsureFont(context);
        return new BugReportScreen(model, new BugReportScreenResources(font, font, _white, context.Ui));
    }

    Texture2D? CreateTexture(ImageRgba image) => _context?.Surface.CreateTexture(image.Pixels, image.Width, image.Height);

    /// <summary>Builds the font and the white texture on first use, then returns the font for the current DPI.</summary>
    SpriteFont EnsureFont(GameOverlayContext context)
    {
        _font ??= context.Surface.LoadDefaultDpiFont(FontPixelHeight);
        _white ??= context.Surface.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
        return _font.For(context.Ui.DpiScale);
    }

    bool OnUpdate(InputState input, float realDt)
    {
        _frameStats.Sample(realDt);
        _frameTimes.Add(realDt * 1000f);
        return _flow.Update(input, realDt, _context?.Ui);
    }

    void OnDrawUi(SpriteBatch batch)
    {
        if (_context is not { } context) return;
        if (_flow.Screens.Screens.Count > 0) _flow.Screens.Draw(batch);
        if (_flow.Toasts.Active.Count == 0) return;
        SpriteFont font = EnsureFont(context);
        _toasts ??= new ToastView(_flow.Toasts, font);
        _toasts.Measurer = font;
        _toasts.Bounds = context.Ui.DesignBounds;
        _toasts.Draw(batch, _white!, font);
    }

    sealed record PreviousCrash(string? File, DateTimeOffset? At);

    /// <summary>What attaching produced: the recorder, the capture over it, and the screen and texture factories.</summary>
    sealed record Attachment(FrameHistoryRecorder Recorder, IBugReportCapture Capture,
        Func<BugReportScreenModel, BugReportScreen> ScreenFactory, Func<ImageRgba, Texture2D?> TextureFactory, AppWindow? Window);

    /// <summary>The overlay <see cref="GameApp"/> holds. Attach creates the recorder from the window.</summary>
    sealed class ServiceOverlay(BugReportService owner) : IGameOverlay
    {
        internal FrameHistoryRecorder? Recorder { get; private set; }

        public void Attach(GameOverlayContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            owner._context = context;
            Recorder = new FrameHistoryRecorder(context.Window, owner._options.Recording);
        }

        public bool Update(InputState input, float realDt) => owner.OnUpdate(input, realDt);

        public void DrawUi(SpriteBatch batch) => owner.OnDrawUi(batch);
    }

    /// <summary>
    /// Forwards to <see cref="RecentLogSink"/> until detached. A log manager cannot drop a sink, so shutdown detaches
    /// this link instead.
    /// </summary>
    sealed class DetachableLogSink(RecentLogSink target) : ILogSink
    {
        volatile bool _detached;

        internal void Detach() => _detached = true;

        public void Emit(in LogEntry entry)
        {
            if (!_detached) target.Emit(entry);
        }

        public void Flush() { }

        public void Dispose() { }
    }
}
