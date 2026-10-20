using System;
using System.Collections.Generic;

namespace KhaozEngine.BugReports.Client.Internal;

/// <summary>
/// The rolling clip ring. It decides which display frames become clip frames and holds the encoded frames of the
/// last <see cref="FrameHistoryOptions.Duration"/>, bounded by frame count and by
/// <see cref="FrameHistoryOptions.MaxBytes"/>. Times are passed in, so the ring reads no clock. The frame thread
/// calls every member except <see cref="Append"/>, which the encode worker calls. One lock guards all state.
/// </summary>
internal sealed class FrameHistory
{
    readonly object _gate = new();
    readonly Queue<ClipFrameRecord> _frames = new();
    readonly TimeSpan _duration;
    readonly int _maxBytes;
    readonly int _maxFrames;
    TimeSpan _nextDue = TimeSpan.Zero;
    long _totalBytes;
    int _generation;
    bool _suspended;
    bool _frozen;

    internal FrameHistory(FrameHistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FramesPerSecond);
        _duration = options.Duration;
        _maxBytes = options.MaxBytes;
        // The count bound backs up the time bound: an average spacing of exactly Interval fits one frame more than
        // Duration times the rate into the time window, and the bundle caps the frames a clip may name.
        _maxFrames = (int)Math.Max(1, Math.Ceiling(options.Duration.TotalSeconds * options.FramesPerSecond));
        Interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / options.FramesPerSecond);
    }

    /// <summary>The target spacing between clip frames, one second over the frame rate.</summary>
    internal TimeSpan Interval { get; }

    /// <summary>Bumped by <see cref="Clear"/>. A frame stamped with an older generation is dropped on append.</summary>
    internal int Generation { get { lock (_gate) return _generation; } }

    internal int Count { get { lock (_gate) return _frames.Count; } }

    internal long TotalBytes { get { lock (_gate) return _totalBytes; } }

    internal bool IsSuspended { get { lock (_gate) return _suspended; } }

    internal bool IsFrozen { get { lock (_gate) return _frozen; } }

    /// <summary>
    /// True when the frame at <paramref name="now"/> should be captured. The schedule advances by one interval per
    /// claim, so the average rate holds at any display rate rather than drifting to the next whole frame. A claim
    /// that is a full interval or more late restarts the schedule from <paramref name="now"/>.
    /// </summary>
    internal bool TryClaimTick(TimeSpan now)
    {
        lock (_gate)
        {
            if (_suspended || _frozen || now < _nextDue) return false;
            _nextDue = now - _nextDue >= Interval ? now + Interval : _nextDue + Interval;
            return true;
        }
    }

    /// <summary>Adds an encoded frame, then drops the frames that fall outside the time, count and byte bounds.</summary>
    internal void Append(ClipFrameRecord frame)
    {
        lock (_gate)
        {
            if (_frozen || frame.Generation < _generation) return;

            _frames.Enqueue(frame);
            _totalBytes += frame.Jpeg.Length;

            TimeSpan cutoff = frame.CapturedAt - _duration;
            while (_frames.Count > 0 && (_frames.Peek().CapturedAt <= cutoff || _frames.Count > _maxFrames || _totalBytes > _maxBytes))
                _totalBytes -= _frames.Dequeue().Jpeg.Length;
        }
    }

    /// <summary>Stops claiming ticks and keeps the frames held, for a paused game.</summary>
    internal void Suspend() { lock (_gate) _suspended = true; }

    internal void Resume() { lock (_gate) _suspended = false; }

    /// <summary>Stops the ring until <see cref="Thaw"/> and returns the frames held, offsets from the first frame.</summary>
    internal ReportClip Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
            if (_frames.Count == 0) return new ReportClip(Array.Empty<ReportClipFrame>());

            var frames = new ReportClipFrame[_frames.Count];
            TimeSpan first = _frames.Peek().CapturedAt;
            int i = 0;
            foreach (ClipFrameRecord f in _frames)
                frames[i++] = new ReportClipFrame(f.CapturedAt - first, f.RenderMilliseconds, f.Width, f.Height, f.Jpeg);
            return new ReportClip(frames);
        }
    }

    /// <summary>Lets the ring run again. The frames from before the freeze stay until they age out.</summary>
    internal void Thaw() { lock (_gate) _frozen = false; }

    /// <summary>Drops every frame, bumps <see cref="Generation"/> and restarts the tick schedule.</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
            _totalBytes = 0;
            _generation++;
            _nextDue = TimeSpan.Zero;
        }
    }
}
