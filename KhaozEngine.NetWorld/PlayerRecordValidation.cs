using System;

namespace KhaozEngine.NetWorld;

/// <summary>The game's verdict on a loaded player game blob, from <see cref="PlayerGameStateValidate"/>.</summary>
public readonly struct PlayerGameStateVerdict
{
    private PlayerGameStateVerdict(bool isValid, string? reason) { IsValid = isValid; Reason = reason; }
    /// <summary>True when the blob may be applied.</summary>
    public bool IsValid { get; }
    /// <summary>Why the blob was rejected (null when valid).</summary>
    public string? Reason { get; }
    /// <summary>An accepting verdict.</summary>
    public static PlayerGameStateVerdict Valid() => new(true, null);
    /// <summary>A rejecting verdict with a human-readable reason (recorded on the quarantine event).</summary>
    public static PlayerGameStateVerdict Invalid(string reason) => new(false, string.IsNullOrEmpty(reason) ? "invalid" : reason);
}

/// <summary>Validates a loaded game blob before it is applied at load-on-join, on the server thread.
/// A rejecting verdict quarantines the WHOLE record (position and blob).</summary>
public delegate PlayerGameStateVerdict PlayerGameStateValidate(in PlayerPersistenceContext context, ReadOnlySpan<byte> blob);
