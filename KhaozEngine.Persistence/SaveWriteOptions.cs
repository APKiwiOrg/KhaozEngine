namespace KhaozEngine.Persistence;

/// <summary>
/// Per-call overrides for <see cref="GameStorage.Save{T}(string, T, SaveWriteOptions?)"/>.
/// </summary>
public sealed class SaveWriteOptions
{
    /// <summary>
    /// Overrides whether this write is encoded. Null (default) follows the <see cref="SaveEncoding"/> the
    /// <see cref="GameStorage"/> was built with: encode under <see cref="SaveEncoding.Encoded"/>, plaintext
    /// under <see cref="SaveEncoding.Plaintext"/>. Set to false to force plaintext for this call (for example
    /// a save meant to be hand-edited) even under <see cref="SaveEncoding.Encoded"/>. Set to true to force
    /// encoding, which throws under <see cref="SaveEncoding.Plaintext"/>.
    /// </summary>
    public bool? Encode { get; init; }

    /// <summary>Short human-readable summary stamped into the encoded envelope's <see cref="SaveMetadata.Summary"/> for this write. Ignored for a plaintext write.</summary>
    public string? Summary { get; init; }
}
