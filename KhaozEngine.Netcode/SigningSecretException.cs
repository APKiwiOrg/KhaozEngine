using System;

namespace KhaozEngine.Netcode;

/// <summary>A structured refusal of a configured signing key, from <see cref="SigningSecret.Decode"/> and
/// <see cref="SigningSecret.Load"/>. Existing <see cref="InvalidOperationException"/> catches remain valid. No
/// identifier, and not the message, carries any part of the raw value or of the decoded key.</summary>
public sealed class SigningSecretException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason.</summary>
    /// <param name="reason">The rule that refused the value.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    public SigningSecretException(SigningSecretRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>The rule that refused the value.</summary>
    public SigningSecretRefusal Reason { get; }

    /// <summary>Where the value came from, the caller's <c>sourceName</c> or the variable <see cref="SigningSecret.Load"/>
    /// read, on every refusal. Never the value itself.</summary>
    public string? SourceName { get; init; }

    /// <summary>The decoded length in bytes, for <see cref="SigningSecretRefusal.DecodedKeyTooShort"/>. No decoded byte
    /// is carried.</summary>
    public long? ActualBytes { get; init; }

    /// <summary>The shortest accepted key in bytes, <see cref="SigningSecret.MinimumBytes"/>, for
    /// <see cref="SigningSecretRefusal.DecodedKeyTooShort"/>.</summary>
    public long? MinimumBytes { get; init; }
}
