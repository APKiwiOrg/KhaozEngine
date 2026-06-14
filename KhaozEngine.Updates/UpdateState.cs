#nullable enable

namespace KhaozEngine.Updates;

/// <summary>Lifecycle of an <see cref="UpdateService"/> check/download/apply cycle.</summary>
public enum UpdateState
{
    /// <summary>No update activity. Initial state and the resting state after a no-op or silent failure.</summary>
    Idle,

    /// <summary>Fetching remote version info and manifest, computing the diff.</summary>
    Checking,

    /// <summary>A newer build exists and files need downloading.</summary>
    UpdateAvailable,

    /// <summary>Downloading changed files into staging.</summary>
    Downloading,

    /// <summary>All files staged and verified; ready to hand off to the updater shim.</summary>
    ReadyToApply,

    /// <summary>Writing the apply config, launching the shim, exiting the process.</summary>
    Applying,

    /// <summary>An error occurred during download or apply. <see cref="UpdateService.ErrorMessage"/> is set; retryable.</summary>
    Failed
}
