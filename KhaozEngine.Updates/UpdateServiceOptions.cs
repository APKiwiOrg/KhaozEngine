using System;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>
/// Configuration for an <see cref="UpdateService"/>. The game supplies its identity (version, paths,
/// shim name) and a transport (<see cref="Source"/>); the process-control hooks default to real
/// <c>Process.Start</c> / <c>Environment.Exit</c> but are injectable so the whole state machine is
/// headless-testable.
/// </summary>
public sealed class UpdateServiceOptions
{
    /// <summary>Transport used to query versions, fetch manifests, and download files.</summary>
    public required IUpdateSource Source { get; init; }

    /// <summary>
    /// Trusted RSA public keys (SubjectPublicKeyInfo PEM) for manifest signatures. At least one is
    /// REQUIRED; constructing <see cref="UpdateService"/> with none throws. A list so keys can be
    /// rotated (ship the new key beside the old, switch the signer, drop the old key later).
    /// </summary>
    public required System.Collections.Generic.IReadOnlyList<string> TrustedPublicKeys { get; init; }

    /// <summary>The currently installed version (e.g. from the game's build metadata).</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>Absolute install directory whose files form the local manifest. Defaults to the app base dir.</summary>
    public string InstallDir { get; init; } = AppContext.BaseDirectory;

    /// <summary>Writable per-user directory for the local manifest, staging, and apply markers.</summary>
    public required string AppDataDir { get; init; }

    /// <summary>Update channel / runtime id. Defaults to the current OS/arch.</summary>
    public string Platform { get; init; } = UpdatePlatform.ResolveRuntimeId();

    /// <summary>
    /// Updater shim executable name without extension (e.g. <c>"MyGameUpdater"</c>). <c>.exe</c> is
    /// appended on Windows. Must sit next to the game in the install directory.
    /// </summary>
    public string UpdaterExecutableName { get; init; } = string.Empty;

    /// <summary>Retries per file on download/hash-mismatch before failing.</summary>
    public int MaxDownloadRetries { get; init; } = 2;

    /// <summary>Per-file download size cap (hostile/oversized payload guard). Default 4 GiB.</summary>
    public long MaxFileBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Total download size cap across all changed files. Default 16 GiB.</summary>
    public long MaxTotalDownloadBytes { get; init; } = 16L * 1024 * 1024 * 1024;

    /// <summary>Cap on the manifest and signature download size. Default 64 MiB.</summary>
    public long MaxManifestBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Whether <see cref="UpdateService.Dispose"/> disposes <see cref="Source"/> if it is disposable.</summary>
    public bool DisposeSource { get; init; } = true;

    /// <summary>Invoked just before the process exits to apply an update (flush state, save, etc.).</summary>
    public Action? OnBeforeForcedExit { get; init; }

    /// <summary>
    /// Launches the updater shim: <c>(updaterPath, applyConfigPath) =&gt; started</c>. Defaults to
    /// <c>Process.Start(updaterPath, "--apply \"&lt;path&gt;\"")</c>. Override in tests to avoid spawning.
    /// </summary>
    public Func<string, string, bool>? LaunchUpdater { get; init; }

    /// <summary>Terminates the process after the shim is launched. Defaults to <c>Environment.Exit(0)</c>.</summary>
    public Action? ExitProcess { get; init; }
}
