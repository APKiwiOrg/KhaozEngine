#nullable enable

namespace KhaozEngine.Updates;

/// <summary>Lifecycle of an <see cref="UpdateService"/> check/download/apply cycle.</summary>
public enum UpdateState
{
    /// <summary>No update activity. Initial state and the resting state after a no-op or silent failure.</summary>
    Idle = 0,

    /// <summary>Fetching remote version info and manifest, computing the diff.</summary>
    Checking = 1,

    /// <summary>
    /// Hashing the install directory against the signed manifest during
    /// <see cref="UpdateService.VerifyAndRepairAsync"/>. Distinct from <see cref="Checking"/> because it is a
    /// long, local, progress-reporting pass over every installed file rather than a quick feed probe, and
    /// because a feed check must not start while it runs.
    /// </summary>
    Verifying = 2,

    /// <summary>A newer build exists and files need downloading.</summary>
    UpdateAvailable = 3,

    /// <summary>Downloading changed files into staging.</summary>
    Downloading = 4,

    /// <summary>All files staged and verified; ready to hand off to the updater shim.</summary>
    ReadyToApply = 5,

    /// <summary>Writing the apply config, launching the shim, exiting the process.</summary>
    Applying = 6,

    /// <summary>An error occurred during download or apply. <see cref="UpdateService.ErrorMessage"/> is set; retryable.</summary>
    Failed = 7,

    /// <summary>
    /// A newer update was advertised, but its manifest could not be authenticated. The update is refused.
    /// <see cref="UpdateService.ErrorMessage"/> contains a player-presentable explanation.
    /// </summary>
    Untrusted = 8
}
