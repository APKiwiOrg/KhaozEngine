namespace KhaozEngine.Netcode;

/// <summary>Rules that refuse a configured signing key, carried by <see cref="SigningSecretException.Reason"/>. No
/// member's identifiers carry any part of the raw value or of the decoded key.</summary>
public enum SigningSecretRefusal
{
    /// <summary>Refuses a set value that is not valid standard base64 once trimmed, the url-safe alphabet
    /// included.</summary>
    NotStandardBase64,

    /// <summary>Refuses a set value that decodes to fewer than <see cref="SigningSecret.MinimumBytes"/> bytes.
    /// <see cref="SigningSecretException.ActualBytes"/> and <see cref="SigningSecretException.MinimumBytes"/> carry
    /// the two lengths.</summary>
    DecodedKeyTooShort,
}
