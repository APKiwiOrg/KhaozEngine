using System;

namespace KhaozEngine.Persistence;

/// <summary>The terminal outcome of one submitted persistence payload.</summary>
public enum PersistenceWriteOutcome
{
    /// <summary>The payload completed its atomic write.</summary>
    Saved,
    /// <summary>A newer pending payload replaced this payload before writing.</summary>
    Superseded,
    /// <summary>The payload exhausted its write attempts.</summary>
    Failed
}

/// <summary>
/// The outcome and target path for one submitted payload. Error is the final write exception for
/// Failed and null for Saved or Superseded. Saved does not imply a newer pending payload was written.
/// </summary>
public sealed record PersistenceWriteResult(PersistenceWriteOutcome Outcome, string Path, Exception? Error);
