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
/// captures every section raw and raises <see cref="ReportCaptured"/>. The next frame opens
/// <see cref="BugReportScreen"/>. Send finishes the sections, builds the bundle and submits it on the thread pool,
/// then shows a toast. A sign in flushes the offline queue.
/// <para>Every member is frame thread only, except that <see cref="RetryQueuedAsync"/> completes off it. Background
/// work only posts closures, which <see cref="Update"/> runs at the start of the next frame, so the screen, the
/// toasts, textures, <see cref="ReportSubmitted"/> and every toast stay on the frame thread.</para>
/// </summary>
internal sealed class BugReportFlow
{
    /// <summary>The action id of the report trigger in <see cref="TriggerControls"/>.</summary>
    internal const string TriggerActionId = "ReportBug";

    /// <summary>The longest the thumbnails or a send wait for the trigger frame's screenshot.</summary>
    internal static readonly TimeSpan ScreenshotWait = TimeSpan.FromSeconds(5);

    const int StripFrames = 4;

    readonly BugReportServiceOptions _options;
    readonly IBugReportCapture _capture;
    readonly BugReportSectionSet _sections;
    readonly BugReportSubmitter _submitter;
    readonly ReportRedactor _redactor;
    readonly Func<string?> _currentSubject;
    readonly Func<BugReportScreenModel, BugReportScreen> _screenFactory;
    readonly Func<ImageRgba, Texture2D?> _textureFactory;
    readonly TimeProvider _time;
    readonly IReadOnlyList<string> _categories;
    readonly ConcurrentQueue<Action> _posted = new();
    string? _subject;
    bool _beginPending;
    bool _holdNextFrame;
    ActiveReport? _report;

    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">An option is invalid. See <see cref="BugReportServiceOptions.Validate"/>.</exception>
    internal BugReportFlow(BugReportServiceOptions options, IBugReportCapture capture, BugReportSectionSet sections,
        BugReportSubmitter submitter, ReportRedactor redactor, Func<string?> currentSubject,
        Func<BugReportScreenModel, BugReportScreen> screenFactory, Func<ImageRgba, Texture2D?> textureFactory,
        TimeProvider time)
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
        _categories = options.Categories is { } categories ? [.. categories] : BugReportDraft.DefaultCategories;

        var map = new ActionMap();
        map.AddAction(InputAction.Button(TriggerActionId), InputSource.FromKey(options.DefaultKey));
        TriggerControls = new ActionMapController(map, options.TriggerBindingsJson, options.SaveTriggerBindings);
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
    /// </summary>
    internal bool Update(InputState input, float realDt, IDesignViewport? viewport)
    {
        ArgumentNullException.ThrowIfNull(input);
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
                OpenScreen(_report!);
                break;
            case BugReportFlowState.Open:
                UpdateScreen(_report!, input, realDt, viewport);
                break;
        }

        Toasts.Update(realDt);
        return hold;
    }

    /// <summary>Sends the queued reports for the signed-in account. Returns how many the sink accepted.</summary>
    internal async Task<int> RetryQueuedAsync(CancellationToken cancellationToken)
    {
        string? subject = _currentSubject();
        if (string.IsNullOrWhiteSpace(subject)) return 0;
        return await FlushAsync(subject, cancellationToken).ConfigureAwait(false);
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

    void PollSubject()
    {
        string? subject = _currentSubject();
        if (string.Equals(subject, _subject, StringComparison.Ordinal)) return;
        _subject = subject;
        if (string.IsNullOrWhiteSpace(subject)) return;
        PendingWork = Task.Run(() => FlushAsync(subject, CancellationToken.None));
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
        ReportCaptured?.Invoke(id);
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
        BugReportScreen screen = _screenFactory(model);
        report.Screen = screen;
        Screens.Add(screen);
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
        _capture.Thaw();
        Screens.Remove(screen);
        report.Close();
        _report = null;
        State = BugReportFlowState.Idle;
        _holdNextFrame = true;
        if (result == BugReportScreenResult.Send)
        {
            string? subject = _subject;
            PendingWork = Task.Run(() => SendAsync(report, choices, subject));
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

    async Task<ReportImage?> WaitForScreenshotAsync(Task<ReportImage> screenshot, bool logFailure)
    {
        try
        {
            return await screenshot.WaitAsync(ScreenshotWait, _time).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (logFailure) Log.Warn("The bug report screenshot was not captured.", exception);
            return null;
        }
    }

    async Task SendAsync(ActiveReport report, ReportChoices choices, string? subject)
    {
        byte[] bundle;
        try
        {
            ReportImage? screenshot = choices.IncludeScreenshot
                ? await WaitForScreenshotAsync(report.Screenshot, logFailure: false).ConfigureAwait(false)
                : null;
            FinishStarting?.Invoke();
            IReadOnlyList<CapturedSection> sections =
                BugReportSectionSet.Finish(report.Raw, _redactor, BugReportSectionSet.DefaultFinishBudget);
            var draft = new BugReportDraft(report.Id, _options.GameId, BugReportKind.Bug, report.CapturedAt, sections,
                screenshot, report.Clip)
            {
                Description = choices.Description,
                Category = choices.Category,
                IncludeScreenshot = screenshot is not null,
                IncludeClip = choices.IncludeClip,
            };
            bundle = BugReportBundleWriter.Write(draft);
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
            result = await _submitter.SubmitAsync(new BugReportSubmission(report.Id, subject, bundle), CancellationToken.None)
                .ConfigureAwait(false);
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
            ReportSubmitted?.Invoke(result);
        });
    }

    async Task<int> FlushAsync(string subject, CancellationToken cancellationToken)
    {
        int sent;
        try
        {
            sent = await _submitter.FlushAsync(subject, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warn("Saved bug reports could not be sent.", exception);
            return 0;
        }
        if (sent > 0) Post(() => ShowToast(BugReportStrings.Format(BugReportStrings.ToastFlushed, sent), ToastKind.Standard));
        return sent;
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

        internal List<Texture2D> Textures { get; } = new();

        /// <summary>Forgets the screen and disposes the thumbnails. Frame thread only.</summary>
        internal void Close()
        {
            Screen = null;
            foreach (Texture2D texture in Textures) texture.Dispose();
            Textures.Clear();
        }
    }
}
