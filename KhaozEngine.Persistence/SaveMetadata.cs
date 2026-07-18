using System;

namespace KhaozEngine.Persistence;

/// <summary>
/// Optional metadata carried alongside an encoded save. In the v2 envelope it is serialized into the
/// meta segment and covered by the same HMAC as the payload, so a verified HMAC vouches for it too.
/// All members are optional so callers can supply as much or as little as they have.
/// </summary>
public sealed record SaveMetadata
{
    /// <summary>UTC timestamp of when the save was written.</summary>
    public DateTime SavedAtUtc { get; init; }

    /// <summary>Game or build version that produced the save, or null when not recorded.</summary>
    public string? GameVersion { get; init; }

    /// <summary>Short human-readable summary of the save (for example a level or progress label), or null.</summary>
    public string? Summary { get; init; }
}
