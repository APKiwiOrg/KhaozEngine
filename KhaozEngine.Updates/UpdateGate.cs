namespace KhaozEngine.Updates;

/// <summary>The terminal decision of <see cref="UpdateService.EnsureUpToDateAsync"/>: the one value a startup
/// caller branches on before connecting.</summary>
public enum UpdateGateOutcome
{
    /// <summary>The current build is the newest signed build (or no newer build was offered). Proceed to connect.</summary>
    UpToDate,
    /// <summary>A newer build was found and the apply/relaunch was launched: the process is exiting into the new
    /// version. In production this method does not return (the process exits); a caller that sees this (e.g. a test
    /// with a non-exiting <c>ExitProcess</c> hook) should NOT continue startup.</summary>
    Updating,
    /// <summary>The feed could not be reached within the timeout (down / slow / offline). Non-fatal: proceed to
    /// connect on the current build; the connect-time version handshake is the backstop.</summary>
    FeedUnreachable,
    /// <summary>An update was found but could not be downloaded or applied. Non-fatal: proceed on the current build
    /// (the handshake is the backstop). <see cref="UpdateGateResult.Error"/> carries the detail.</summary>
    Failed,
}

/// <summary>The phase a <see cref="UpdateGateProgress"/> report describes.</summary>
public enum UpdateGatePhase
{
    /// <summary>Contacting the feed and computing the download plan.</summary>
    Checking,
    /// <summary>Downloading the changed files into staging (drives a "Downloading update..." screen).</summary>
    Downloading,
    /// <summary>Handing off to the updater shim and relaunching.</summary>
    Applying,
}

/// <summary>A progress tick from <see cref="UpdateService.EnsureUpToDateAsync"/>, for a startup update screen.
/// During <see cref="UpdateGatePhase.Downloading"/>, <see cref="BytesDownloaded"/>/<see cref="TotalBytes"/> and
/// <see cref="FilesDownloaded"/>/<see cref="TotalFiles"/> drive a progress bar.</summary>
public readonly struct UpdateGateProgress
{
    public UpdateGatePhase Phase { get; }
    public long BytesDownloaded { get; }
    public long TotalBytes { get; }
    public int FilesDownloaded { get; }
    public int TotalFiles { get; }

    public UpdateGateProgress(UpdateGatePhase phase, long bytesDownloaded, long totalBytes, int filesDownloaded, int totalFiles)
    {
        Phase = phase;
        BytesDownloaded = bytesDownloaded;
        TotalBytes = totalBytes;
        FilesDownloaded = filesDownloaded;
        TotalFiles = totalFiles;
    }
}

/// <summary>The result of the startup update gate: the <see cref="Outcome"/> to branch on, plus the remote
/// version it saw (when known) and an error detail for <see cref="UpdateGateOutcome.Failed"/>.</summary>
public readonly struct UpdateGateResult
{
    public UpdateGateOutcome Outcome { get; }
    /// <summary>The newest version the feed reported, when a check completed; otherwise null.</summary>
    public string? RemoteVersion { get; }
    /// <summary>Failure detail for <see cref="UpdateGateOutcome.Failed"/>; otherwise null.</summary>
    public string? Error { get; }

    public UpdateGateResult(UpdateGateOutcome outcome, string? remoteVersion, string? error)
    {
        Outcome = outcome;
        RemoteVersion = remoteVersion;
        Error = error;
    }
}
