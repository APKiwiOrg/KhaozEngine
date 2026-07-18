namespace KhaozEngine.Persistence;

/// <summary>
/// Per-call overrides for <see cref="GameStorage.Save{T}(string, T, SaveWriteOptions?)"/>.
/// </summary>
public sealed class SaveWriteOptions
{
    /// <summary>
    /// Overrides whether this write is encoded. Null (default) follows <see cref="GameStorage"/>'s
    /// default: encode when <see cref="GameStorageOptions.Encoder"/> is configured, plaintext otherwise.
    /// Set to false to force plaintext for this call (for example a save meant to be hand-edited) even
    /// when an encoder is configured. Set to true to force encoding.
    /// </summary>
    public bool? Encode { get; init; }

    /// <summary>Short human-readable summary stamped into the encoded envelope's <see cref="SaveMetadata.Summary"/> for this write. Ignored for a plaintext write.</summary>
    public string? Summary { get; init; }
}
