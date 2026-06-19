using System;

namespace KhaozEngine.Ecs;

/// <summary>
/// Thrown by <see cref="WorldSerializer.Load(string)"/> when a save's <c>FormatVersion</c> is newer than
/// this build can read. A future save is never silently mis-deserialized.
/// </summary>
public sealed class UnsupportedSaveVersionException : Exception
{
    public int FoundVersion { get; }
    public int MaxSupportedVersion { get; }
    public UnsupportedSaveVersionException(int found, int maxSupported)
        : base($"Save FormatVersion {found} is newer than supported version {maxSupported}.")
    { FoundVersion = found; MaxSupportedVersion = maxSupported; }
}
