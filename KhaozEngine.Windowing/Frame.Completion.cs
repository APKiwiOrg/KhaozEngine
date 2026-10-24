using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing;

public sealed partial class Frame
{
    GpuFrameSubmission? _submission;
    IGpuDevice? _submissionDevice;

    /// <summary>The shared submission owner for every scene recorded in this window frame.</summary>
    public GpuFrameSubmission Submission => _submission
        ?? throw new InvalidOperationException("The window has not begun its first frame.");

    internal void BeginSubmission(IGpuDevice device)
    {
        if (_submission is null) { _submission = new(device); _submissionDevice = device; }
        else if (!ReferenceEquals(device, _submissionDevice))
            throw new InvalidOperationException("A frame submission cannot change devices.");
        _submission.Begin();
    }

    internal bool HasSubmission => _submission is not null;

    internal void DisposeSubmission() => _submission?.Dispose();
}
