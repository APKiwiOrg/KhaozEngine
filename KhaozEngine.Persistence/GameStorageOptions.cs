using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Persistence;

/// <summary>
/// Optional configuration for <see cref="GameStorage"/>. Every field is optional. A null options
/// object (or an unset field) means the default. The save posture is not configured here: it is the
/// required <see cref="SaveEncoding"/> constructor argument, so it cannot be left out by accident.
/// </summary>
public sealed class GameStorageOptions
{
    /// <summary>Logger passed to the internal <see cref="PersistenceQueue"/> and any settings manager. Defaults to the ambient log.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Total write attempts per payload for the internal queue (>= 1). Defaults to 3.</summary>
    public int MaxWriteAttempts { get; set; } = 3;

    /// <summary>Backoff between write attempts. Defaults to 50 ms (capped at 1 s by the queue).</summary>
    public TimeSpan? RetryDelay { get; set; }

    /// <summary>How <see cref="GameStorage.Load{T}"/> reacts to save data that decodes but fails its integrity check. Defaults to <see cref="TamperPolicy.Strict"/>.</summary>
    public TamperPolicy TamperPolicy { get; set; } = TamperPolicy.Strict;

    /// <summary>Whether <see cref="GameStorage.Load{T}"/> accepts a plaintext save under <see cref="SaveEncoding.Encoded"/> (a legacy or deliberately hand-edited save). Ignored under <see cref="SaveEncoding.Plaintext"/>. Defaults to true.</summary>
    public bool AcceptLegacyPlaintext { get; set; } = true;

    /// <summary>Number of numbered backups the internal <see cref="PersistenceQueue"/> keeps per target path, rotated on each write. Defaults to 2.</summary>
    public int BackupGenerations { get; set; } = 2;

    /// <summary>Game or build version stamped into every encoded save's <see cref="SaveMetadata.GameVersion"/>. Null (default) omits it.</summary>
    public string? GameVersion { get; set; }
}
