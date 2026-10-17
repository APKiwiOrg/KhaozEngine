using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.App;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Windowing.Actions;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// The headless state machine behind the report trigger. The trigger frame freezes the clip, arms the screenshot,
/// captures every section raw and raises <see cref="ReportCaptured"/>. <see cref="BugReportScreen"/> opens on the
/// first later frame that finds the screenshot taken or failed, and on the third at the latest. Send finishes the sections, builds the bundle and submits it on the thread pool,
/// then shows a toast. A sign in flushes the offline queue.
/// <para>Every member is frame thread only, except that <see cref="RetryQueuedAsync"/> completes off it. Background
/// work only posts closures, which <see cref="Update"/> runs at the start of the next frame, so the screen, the
/// toasts, textures, <see cref="ReportSubmitted"/> and every toast stay on the frame thread.</para>
/// <para>Every send and flush receives the shutdown token. A send canceled by it keeps its bundle in the offline
/// queue for the next session.</para>
/// </summary>
internal sealed class BugReportFlow
{
    /// <summary>The action id of the report trigger in <see cref="TriggerControls"/>.</summary>
    internal const string TriggerActionId = "ReportBug";

    /// <summary>The longest the thumbnails or a send wait for the trigger frame's screenshot.</summary>
    internal static readonly TimeSpan ScreenshotWait = TimeSpan.FromSeconds(5);

    const int StripFrames = 4;

    /// <summary>The frame after the trigger that opens the screen at the latest, while the screenshot is pending.</summary>
    internal const int ScreenOpenFrames = 3;

    readonly BugReportServiceOptions _options;
    readonly IBugReportCapture _capture;
    readonly BugReportSectionSet _sections;
    readonly BugReportSubmitter _submitter;
    readonly ReportRedactor _redactor;
    readonly Func<string?> _currentSubject;
    readonly Func<BugReportScreenModel, BugReportScreen> _screenFactory;
    readonly Func<ImageRgba, Texture2D?> _textureFactory;
    readonly TimeProvider _time;
    readonly CancellationToken _shutdown;
    readonly IReadOnlyList<string> _categories;
    readonly ConcurrentQueue<Action> _posted = new();
    // Canceled when shutdown begins, before the bounded wait for builds, so a send stops waiting for its screenshot.
    readonly CancellationTokenSource _stopping = new();
    readonly GameDelegateFaults _subjectFaults = new("currentSubject");
    readonly GameDelegateFaults _saveFaults = new("SaveTriggerBindings");
    string? _subject;
    bool _beginPending;
    bool _holdNextFrame;
    ActiveReport? _report;

    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">An option is invalid. See <see cref="BugReportServiceOptions.Validate"/>.</exception>
    internal BugReportFlow(BugReportServiceOptions options, IBugReportCapture capture, BugReportSectionSet sections,
        BugReportSubmitter submitter, ReportRedactor redactor, Func<string?> currentSubject,
        Func<BugReportScreenModel, BugReportScreen> screenFactory, Func<ImageRgba, Texture2D?> textureFactory,
        TimeProvider time, CancellationToken shutdown)
    {
        BugReportServiceOptions.Validate(options);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(submitter);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(currentSubject);
        ArgumentNullException.ThrowIfNull(screenFactory);
        ArgumentNullException.ThrowIfNull(textureFactory);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _capture = capture;
        _sections = sections;
        _submitter = submitter;
        _redactor = redactor;
        _currentSubject = currentSubject;
        _screenFactory = screenFactory;
        _textureFactory = textureFactory;
        _time = time;
        _shutdown = shutdown;
        _categories = options.Categories is { } categories ? [.. categories] : BugReportDraft.DefaultCategories;

        var map = new ActionMap();
        map.AddAction(InputAction.Button(TriggerActionId), InputSource.FromKey(options.DefaultKey));
        // A save that throws leaves the rebinding in memory. The player keeps it for this session.
        TriggerControls = new ActionMapController(map, persistedJson: null,
            options.SaveTriggerBindings is { } save ? json => SaveBindings(save, json) : null);
        if (!string.IsNullOrWhiteSpace(options.TriggerBindingsJson)
            && ActionMapSerializer.Load(map, options.TriggerBindingsJson).AppliedCount == 0)
        {
            Log.Warn("The saved bug report trigger bindings did not apply. The default key stays bound.");
        }
    }

    /// <summary>The trigger binding. A rebind captured here saves through the options' callback.</summary>
    internal ActionMapController TriggerControls { get; }

    internal BugReportFlowState State { get; private set; }

    /// <summary>The flow's own screen stack. It holds the confirmation screen while a report is open.</summary>
    internal ScreenStack Screens { get; } = new();

    /// <summary>The report toasts.</summary>
    internal ToastStack Toasts { get; } = new();

    /// <summary>The latest send, flush or thumbnail task, for tests to await before one more <see cref="Update"/>.</summary>
    internal Task? PendingWork { get; private set; }

    /// <summary>The sends still building their bundle, which shutdown waits on so each reaches the offline queue.</summary>
    internal ReportBuildTracker Builds { get; } = new();

    /// <summary>The input of the frame being updated, stored before any capture so sections can read it.</summary>
    internal InputState? LastFrameInput { get; private set; }

    /// <summary>Runs on the send's thread just before the sections are finished. A test seam.</summary>
    internal Action? FinishStarting { get; set; }

    /// <summary>Raised in the trigger frame with the new report id.</summary>
    internal event Action<string>? ReportCaptured;

    /// <summary>Raised on the frame thread with each submission's result.</summary>
    internal event Action<BugReportSubmitResult>? ReportSubmitted;

    /// <summary>Asks the next <see cref="Update"/> to capture a report, so a menu can close itself first.</summary>
    /// <returns>False while a report is captured or open.</returns>
    internal bool Begin()
    {
        if (State != BugReportFlowState.Idle) return false;
        _beginPending = true;
        return true;
    }

    /// <summary>
    /// Runs one frame. Returns true from the trigger frame until the frame after the screen closes, inclusive, so
    /// the caller holds the game's input for those frames.
    /// <para>A throwing <c>currentSubject</c> or <c>SaveTriggerBindings</c> is caught, logged once until it succeeds
    /// again and never stops reports. Any other exception that escapes, a fault in the flow itself, thaws the clip,
    /// closes the open report and returns to idle before it is rethrown, so the overlay host that removes a throwing
    /// overlay never leaves the clip frozen.</para>
    /// </summary>
    internal bool Update(InputState input, float realDt, IDesignViewport? viewport)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            return UpdateFrame(input, realDt, viewport);
        }
        catch
        {
            Abandon();
            throw;
        }
    }

    bool UpdateFrame(InputState input, float realDt, IDesignViewport? viewport)
    {
        LastFrameInput = input;
        RunPosted();
        PollSubject();

        bool rebinding = TriggerControls.IsRebinding;
        TriggerControls.Update(input);

        bool hold = _holdNextFrame || State != BugReportFlowState.Idle;
        _holdNextFrame = false;
        switch (State)
        {
            case BugReportFlowState.Idle:
                // A key a rebind captures this frame is the new binding, not a report.
                if (_beginPending || (!rebinding && TriggerControls.WasPressed(TriggerActionId)))
                {
                    _beginPending = false;
                    Capture();
                    hold = true;
                }
                break;
            case BugReportFlowState.Captured:
                // The trigger frame's screenshot is usually taken by the next frame. Waiting for it keeps the
                // thumbnail ready, and the cap keeps a stalled capture from delaying the screen.
                ActiveReport captured = _report!;
                if (captured.Screenshot.IsCompleted || ++captured.FramesWaited >= ScreenOpenFrames) OpenScreen(captured);
                break;
            case BugReportFlowState.Open:
                UpdateScreen(_report!, input, realDt, viewport);
                break;
        }

        Toasts.Update(realDt);
        return hold;
    }

    /// <summary>
    /// Sends the queued reports for the signed-in account on the thread pool, because the queue scans and reads the
    /// disk before its first await. Returns how many the sink accepted. Either <paramref name="cancellationToken"/>
    /// or the shutdown token cancels it.
    /// </summary>
    internal Task<int> RetryQueuedAsync(CancellationToken cancellationToken)
    {
        // A subject that cannot be read keeps the last one the frame thread knew.
        string? subject = TryReadSubject(out string? read) ? read : Volatile.Read(ref _subject);
        if (string.IsNullOrWhiteSpace(subject)) return Task.FromResult(0);
        return Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown);
            return await FlushAsync(subject, linked.Token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>
    /// Closes a captured or open report at shutdown without sending it: thaws the clip, removes the screen and
    /// disposes the thumbnails. Never throws. Frame thread only.
    /// </summary>
    internal void CloseActiveReport() => Abandon();

    /// <summary>
    /// Tells every send still building its bundle that shutdown has begun. A send waiting for the trigger frame's
    /// screenshot goes on at once without it, so it reaches the offline queue inside the shutdown wait.
    /// </summary>
    internal void SignalShutdown() => _stopping.Cancel();

    /// <summary>Thaws the clip and closes the report a failed frame left captured or open. Never throws.</summary>
    void Abandon()
    {
        ActiveReport? report = _report;
        bool frozen = State != BugReportFlowState.Idle;
        _report = null;
        State = BugReportFlowState.Idle;
        _beginPending = false;
        if (frozen) Contain(_capture.Thaw);
        if (report?.Screen is { } screen) Contain(() => Screens.Remove(screen));
        if (report is not null) Contain(report.Close);
    }

    static void Contain(Action step)
    {
        try
        {
            step();
        }
        catch (Exception exception)
        {
            Log.Warn("A bug report could not be closed after a failed frame.", exception);
        }
    }

    void RunPosted()
    {
        while (_posted.TryDequeue(out Action? work))
        {
            try
            {
                work();
            }
            catch (Exception exception)
            {
                Log.Warn("A bug report step failed on the frame thread.", exception);
            }
        }
    }

    void Post(Action work) => _posted.Enqueue(work);

    /// <summary>A throwing <c>currentSubject</c> reads as no change: the last known subject stays and no flush starts.</summary>
    void PollSubject()
    {
        if (!TryReadSubject(out string? subject)) return;
        if (string.Equals(subject, _subject, StringComparison.Ordinal)) return;
        Volatile.Write(ref _subject, subject);
        StartFlush();
    }

    /// <summary>Flushes the queue for the polled subject on the thread pool. Does nothing while signed out.</summary>
    void StartFlush()
    {
        string? subject = _subject;
        if (string.IsNullOrWhiteSpace(subject)) return;
        PendingWork = Task.Run(() => FlushAsync(subject, _shutdown));
    }

    /// <summary>Calls the game's <c>currentSubject</c>. False, with the failure logged once, when it throws.</summary>
    bool TryReadSubject(out string? subject)
    {
        try
        {
            subject = _currentSubject();
        }
        catch (Exception exception)
        {
            _subjectFaults.Report(exception);
            subject = null;
            return false;
        }
        _subjectFaults.Clear();
        return true;
    }

    void SaveBindings(Action<string> save, string json)
    {
        try
        {
            save(json);
        }
        catch (Exception exception)
        {
            _saveFaults.Report(exception);
            return;
        }
        _saveFaults.Clear();
    }

    void Capture()
    {
        string id = ReportIds.NewId();
        DateTimeOffset capturedAt = _time.GetUtcNow();
        ReportClip? clip = FreezeClip();
        Task<ReportImage> screenshot = ArmScreenshot();
        IReadOnlyList<RawSection> raw = _sections.CaptureAll(new BugReportContext(id, capturedAt, _subject));
        _report = new ActiveReport(id, capturedAt, raw, clip, screenshot);
        State = BugReportFlowState.Captured;
        try
        {
            ReportCaptured?.Invoke(id);
        }
        catch (Exception exception)
        {
            // A game hook must not take the overlay down with the clip still frozen.
            Log.Warn("A ReportCaptured handler failed for bug report " + id + ".", exception);
        }
    }

    ReportClip? FreezeClip()
    {
        try
        {
            return _capture.FreezeClip();
        }
        catch (Exception exception)
        {
            Log.Warn("The bug report clip could not be frozen.", exception);
            return null;
        }
    }

    Task<ReportImage> ArmScreenshot()
    {
        try
        {
            return _capture.CaptureScreenshotAsync();
        }
        catch (Exception exception)
        {
            return Task.FromException<ReportImage>(exception);
        }
    }

    void OpenScreen(ActiveReport report)
    {
        bool screenshotAvailable = !report.Screenshot.IsFaulted && !report.Screenshot.IsCanceled;
        var model = new BugReportScreenModel(_categories, signedIn: !string.IsNullOrWhiteSpace(_subject),
            screenshotAvailable, clipAvailable: report.Clip is { Frames.Count: > 0 });
        BugReportScreen screen;
        try
        {
            screen = _screenFactory(model);
            Screens.Add(screen);
        }
        catch (Exception exception)
        {
            Log.Warn("The bug report screen could not be opened.", exception);
            _report = null;
            State = BugReportFlowState.Idle;
            _holdNextFrame = true;
            // The clip thaws before the toast, so a failing string lookup cannot leave it frozen.
            _capture.Thaw();
            ShowToast(BugReportStrings.Resolve(BugReportStrings.ToastFailed), ToastKind.Danger);
            return;
        }
        report.Screen = screen;
        State = BugReportFlowState.Open;
        PendingWork = Task.Run(() => DecodeThumbnailsAsync(report));
    }

    void UpdateScreen(ActiveReport report, InputState input, float realDt, IDesignViewport? viewport)
    {
        BugReportScreen screen = report.Screen!;
        Screens.Update(realDt, input, viewport);
        BugReportScreenResult result = screen.Result;
        if (result == BugReportScreenResult.Open) return;

        BugReportScreenModel model = screen.Model;
        var choices = new ReportChoices(model.Description, model.Category, model.IncludeScreenshot, model.IncludeClip);
        // The state goes first, so a throwing Thaw, UnloadContent or texture Dispose cannot strand an open report.
        _report = null;
        State = BugReportFlowState.Idle;
        _holdNextFrame = true;
        if (result == BugReportScreenResult.Send)
        {
            string? subject = _subject;
            TaskCompletionSource queued = Builds.Start();
            PendingWork = Task.Run(async () =>
            {
                try
                {
                    await SendAsync(report, choices, subject, queued).ConfigureAwait(false);
                }
                finally
                {
                    queued.TrySetResult();
                }
            });
        }
        try
        {
            _capture.Thaw();
        }
        finally
        {
            try
            {
                Screens.Remove(screen);
            }
            finally
            {
                report.Close();
            }
        }
    }

    async Task DecodeThumbnailsAsync(ActiveReport report)
    {
        List<ImageRgba> strip = DecodeStrip(report.Clip);
        ReportImage? screenshot = await WaitForScreenshotAsync(report.Screenshot, logFailure: true).ConfigureAwait(false);
        ImageRgba? thumbnail = null;
        if (screenshot is not null)
        {
            try
            {
                thumbnail = ReportThumbnails.FromScreenshot(screenshot);
            }
            catch (Exception exception)
            {
                Log.Warn("The bug report screenshot thumbnail could not be decoded.", exception);
            }
        }
        Post(() => ShowThumbnails(report, screenshotLost: screenshot is null, thumbnail, strip));
    }

    static List<ImageRgba> DecodeStrip(ReportClip? clip)
    {
        var strip = new List<ImageRgba>(StripFrames);
        if (clip is null) return strip;
        Exception? firstFault = null;
        foreach (int index in ReportThumbnails.StripIndices(clip.Frames.Count, StripFrames))
        {
            try
            {
                strip.Add(ReportThumbnails.FromClipFrame(clip.Frames[index]));
            }
            catch (Exception exception)
            {
                firstFault ??= exception;
            }
        }
        if (firstFault is not null) Log.Warn("A bug report clip thumbnail could not be decoded.", firstFault);
        return strip;
    }

    void ShowThumbnails(ActiveReport report, bool screenshotLost, ImageRgba? thumbnail, List<ImageRgba> strip)
    {
        // A screen closed before its thumbnails arrived needs none.
        if (!ReferenceEquals(_report, report) || report.Screen is not { } screen) return;
        if (screenshotLost) screen.Model.ScreenshotAvailable = false;

        Texture2D? shot = thumbnail is { } image ? CreateTexture(report, image) : null;
        var frames = new List<Texture2D>(strip.Count);
        foreach (ImageRgba frame in strip)
            if (CreateTexture(report, frame) is { } texture) frames.Add(texture);
        screen.SetThumbnails(shot, frames);
    }

    Texture2D? CreateTexture(ActiveReport report, ImageRgba image)
    {
        Texture2D? texture = _textureFactory(image);
        if (texture is not null) report.Textures.Add(texture);
        return texture;
    }

    /// <summary>
    /// The screenshot, or null when it failed, took longer than <see cref="ScreenshotWait"/> or
    /// <paramref name="stop"/> fired first. A screenshot already taken is returned even after <paramref name="stop"/>.
    /// </summary>
    async Task<ReportImage?> WaitForScreenshotAsync(Task<ReportImage> screenshot, bool logFailure,
        CancellationToken stop = default)
    {
        try
        {
            return await screenshot.WaitAsync(ScreenshotWait, _time, stop).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (logFailure) Log.Warn("The bug report screenshot was not captured.", exception);
            return null;
        }
    }

    /// <summary>Builds and submits one report. <paramref name="queued"/> completes once the bundle is in the queue.</summary>
    async Task SendAsync(ActiveReport report, ReportChoices choices, string? subject, TaskCompletionSource queued)
    {
        byte[] bundle;
        try
        {
            ReportImage? screenshot = choices.IncludeScreenshot
                ? await WaitForScreenshotAsync(report.Screenshot, logFailure: false, _stopping.Token).ConfigureAwait(false)
                : null;
            FinishStarting?.Invoke();
            IReadOnlyList<CapturedSection> sections =
                BugReportSectionSet.Finish(report.Raw, _redactor, BugReportSectionSet.DefaultFinishBudget);
            bundle = ReportBundleBuilder.Write(new ReportBundleBuilder.Request(report.Id, _options.GameId, report.CapturedAt,
                sections, choices.Description, choices.Category, screenshot, choices.IncludeClip ? report.Clip : null));
        }
        catch (Exception exception)
        {
            Log.Warn("Bug report " + report.Id + " could not be built.", exception);
            Post(() => ShowToast(BugReportStrings.Resolve(BugReportStrings.ToastFailed), ToastKind.Danger));
            return;
        }

        BugReportSubmitResult result;
        try
        {
            result = await SubmitQueuedFirstAsync(new BugReportSubmission(report.Id, subject, bundle), queued).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            Log.Info("Bug report " + report.Id + " was canceled by shutdown. The offline queue keeps it for the next session.");
            return;
        }
        catch (Exception exception)
        {
            Log.Warn("Bug report " + report.Id + " could not be submitted.", exception);
            Post(() => ShowToast(BugReportStrings.Resolve(BugReportStrings.ToastFailed), ToastKind.Danger));
            return;
        }
        Post(() =>
        {
            ShowResult(result);
            // A player who signed in while this send was queueing signed out would otherwise wait for the next sign in.
            if (result is { Status: BugReportSubmitStatus.Queued, Error: BugReportSubmitter.SignedOutError }) StartFlush();
            ReportSubmitted?.Invoke(result);
        });
    }

    /// <summary>
    /// Submits with a token the shutdown token cancels only after the call returns. The submitter writes the bundle to
    /// the queue before its first await, so a send that shutdown cancels, even before it started, stays queued, and
    /// <paramref name="queued"/> completes as soon as the call returns.
    /// </summary>
    async Task<BugReportSubmitResult> SubmitQueuedFirstAsync(BugReportSubmission submission, TaskCompletionSource queued)
    {
        using var send = new CancellationTokenSource();
        Task<BugReportSubmitResult> submit;
        try
        {
            submit = _submitter.SubmitAsync(submission, send.Token);
        }
        finally
        {
            queued.TrySetResult();
        }
        using CancellationTokenRegistration stop = _shutdown.Register(send.Cancel);
        return await submit.ConfigureAwait(false);
    }

    async Task<int> FlushAsync(string subject, CancellationToken cancellationToken)
    {
        BugReportFlushResult flushed;
        try
        {
            flushed = await _submitter.FlushAsync(subject, cancellationToken).ConfigureAwait(false);
        }
        // As in SinkCall, only a cancellation that the token asked for propagates. A failure racing it is still a failure.
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warn("Saved bug reports could not be sent.", exception);
            return 0;
        }
        if (flushed.Sent > 0)
            Post(() => ShowToast(BugReportStrings.Format(BugReportStrings.ToastFlushed, flushed.Sent), ToastKind.Standard));
        if (flushed.Rejected > 0)
            Post(() => ShowToast(BugReportStrings.Format(BugReportStrings.ToastFlushRejected, flushed.Rejected), ToastKind.Danger));
        return flushed.Sent;
    }

    void ShowResult(BugReportSubmitResult result)
    {
        switch (result.Status)
        {
            case BugReportSubmitStatus.Sent:
                ShowToast(BugReportStrings.Format(BugReportStrings.ToastSent, result.Reference ?? ""), ToastKind.Standard);
                break;
            case BugReportSubmitStatus.Rejected:
                ShowToast(BugReportStrings.Format(BugReportStrings.ToastRejected, result.Error ?? ""), ToastKind.Danger);
                break;
            case BugReportSubmitStatus.Queued when result.Error == BugReportSubmitter.SignedOutError:
                ShowToast(BugReportStrings.Resolve(BugReportStrings.ToastQueuedSignedOut), ToastKind.Standard);
                break;
            case BugReportSubmitStatus.Queued when result.Error == BugReportSubmitter.QueueFailedError:
                // Nothing was kept, so the report is lost.
                ShowToast(BugReportStrings.Resolve(BugReportStrings.ToastFailed), ToastKind.Danger);
                break;
            default:
                ShowToast(BugReportStrings.Resolve(BugReportStrings.ToastQueued), ToastKind.Warning);
                break;
        }
    }

    // The text is already resolved through BugReportStrings, which falls back to English when no catalog has the key.
    void ShowToast(string text, ToastKind kind) => Toasts.Show(LocalizedText.Raw(text), kind);

    /// <summary>The player's choices, copied off the screen model on the frame thread for the send.</summary>
    sealed record ReportChoices(string Description, string Category, bool IncludeScreenshot, bool IncludeClip);

    /// <summary>What one report captured, plus its screen and textures while it is open.</summary>
    sealed class ActiveReport(string id, DateTimeOffset capturedAt, IReadOnlyList<RawSection> raw, ReportClip? clip,
        Task<ReportImage> screenshot)
    {
        internal string Id { get; } = id;

        internal DateTimeOffset CapturedAt { get; } = capturedAt;

        internal IReadOnlyList<RawSection> Raw { get; } = raw;

        internal ReportClip? Clip { get; } = clip;

        internal Task<ReportImage> Screenshot { get; } = screenshot;

        internal BugReportScreen? Screen { get; set; }

        /// <summary>Frames run since the trigger frame while the screen waits for the screenshot.</summary>
        internal int FramesWaited { get; set; }

        internal List<Texture2D> Textures { get; } = new();

        /// <summary>Forgets the screen and disposes the thumbnails. Frame thread only.</summary>
        internal void Close()
        {
            Screen = null;
            try
            {
                foreach (Texture2D texture in Textures) texture.Dispose();
            }
            finally
            {
                Textures.Clear();
            }
        }
    }
}
