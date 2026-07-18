namespace KhaozEngine.Persistence;

/// <summary>
/// Governs how <see cref="GameStorage"/> reacts to save data that decodes but fails its integrity
/// check (a verified HMAC mismatch, see <see cref="SaveDecodeVerdict.TamperMismatch"/>).
/// </summary>
public enum TamperPolicy
{
    /// <summary>Reject a save whose HMAC does not verify rather than trusting its recovered JSON.</summary>
    Strict,

    /// <summary>Recover and use the JSON from a save whose HMAC does not verify instead of rejecting it.</summary>
    Lenient,
}
