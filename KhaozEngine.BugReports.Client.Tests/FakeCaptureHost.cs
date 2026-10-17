using System;
using System.Collections.Generic;
using KhaozEngine.BugReports.Client.Internal;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary>Records the taps a recorder registers and detaches each one on removal, as the window does.</summary>
internal sealed class FakeCaptureHost : IFrameCaptureHost
{
    public bool SupportsClipCapture { get; init; } = true;

    public bool SupportsSwapchainCopy { get; init; } = true;

    internal List<IFrameCaptureTap> Taps { get; } = new();

    internal List<IFrameCaptureTap> Detached { get; } = new();

    internal List<IDisposable> Retired { get; } = new();

    /// <summary>When set, <see cref="RemoveCaptureTap"/> throws it, as a window torn down underneath might.</summary>
    internal Exception? RemoveFailure { get; set; }

    public void AddCaptureTap(IFrameCaptureTap tap)
    {
        ArgumentNullException.ThrowIfNull(tap);
        if (!Taps.Contains(tap)) Taps.Add(tap);
    }

    public void RemoveCaptureTap(IFrameCaptureTap tap)
    {
        ArgumentNullException.ThrowIfNull(tap);
        if (RemoveFailure is { } failure) throw failure;
        if (!Taps.Remove(tap)) return;
        Detached.Add(tap);
        tap.Detach(Retired.Add);
    }

    internal int DetachCount(IFrameCaptureTap tap) => Detached.FindAll(t => ReferenceEquals(t, tap)).Count;
}
