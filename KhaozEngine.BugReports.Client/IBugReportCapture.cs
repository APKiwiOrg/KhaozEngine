using System;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Client;

/// <summary>The media a report captures in its trigger frame: the frozen clip and the trigger frame's screenshot.</summary>
internal interface IBugReportCapture
{
    /// <summary>True when a clip can be frozen: the recorder is available and recording.</summary>
    bool ClipAvailable { get; }

    /// <summary>Freezes the ring and returns its clip, or null when nothing records. <see cref="Thaw"/> ends the freeze.</summary>
    ReportClip? FreezeClip();

    /// <summary>Lets the ring run again after <see cref="FreezeClip"/>. Harmless when nothing is frozen.</summary>
    void Thaw();

    /// <summary>Arms a screenshot of the current frame. The task completes off the frame thread.</summary>
    Task<ReportImage> CaptureScreenshotAsync();
}

/// <summary>Adapts a <see cref="FrameHistoryRecorder"/> to <see cref="IBugReportCapture"/>. Frame thread only.</summary>
internal sealed class RecorderCapture : IBugReportCapture
{
    readonly FrameHistoryRecorder _recorder;

    internal RecorderCapture(FrameHistoryRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        _recorder = recorder;
    }

    public bool ClipAvailable => _recorder.IsAvailable && _recorder.IsRecording;

    public ReportClip? FreezeClip() => ClipAvailable ? _recorder.Freeze() : null;

    public void Thaw() => _recorder.Thaw();

    public Task<ReportImage> CaptureScreenshotAsync() => _recorder.CaptureScreenshotAsync();
}
