namespace KhaozEngine.Updates;

/// <summary>
/// Read-only view of an in-flight update, consumed by UI (e.g. the Gui overlay) so the presenter never
/// needs the concrete <see cref="UpdateService"/> (which requires full options to construct). Implemented
/// by <see cref="UpdateService"/>; mirror it with a stub in tests.
/// </summary>
public interface IUpdateStatus
{
    /// <summary>Current lifecycle state.</summary>
    UpdateState State { get; }
    /// <summary>Newer version offered by the feed, or null before a check completes.</summary>
    string? RemoteVersion { get; }
    /// <summary>Files staged so far this download.</summary>
    int FilesDownloaded { get; }
    /// <summary>Total files this download must fetch.</summary>
    int TotalFilesToDownload { get; }
    /// <summary>Bytes staged so far this download.</summary>
    long BytesDownloaded { get; }
    /// <summary>Total bytes this download must fetch.</summary>
    long TotalDownloadBytes { get; }
    /// <summary>Last error message when <see cref="State"/> is <see cref="UpdateState.Failed"/>.</summary>
    string? ErrorMessage { get; }
    /// <summary>True when the offered update is marked required.</summary>
    bool IsRequired { get; }

    /// <summary>
    /// True once this session has spent its failed-apply budget, so a presenter must stop offering a retry
    /// that keeps failing (see <see cref="UpdateService.ApplyAttemptsExhausted"/> and
    /// <see cref="UpdateServiceOptions.MaxApplyAttemptsPerSession"/>). Defaults to false so an existing
    /// implementation of this interface keeps compiling and keeps offering the retry it always did.
    /// </summary>
    bool ApplyAttemptsExhausted => false;
}
