using System;

namespace KhaozEngine.Commerce;

/// <summary>A verified account identity. The wallet is agnostic to how it was authenticated.</summary>
public readonly struct AccountId : IEquatable<AccountId>
{
    /// <summary>The opaque, non-empty account key.</summary>
    public string Value { get; }

    /// <summary>Wraps a non-empty account key.</summary>
    /// <exception cref="ArgumentException">The value is null, empty, or whitespace.</exception>
    public AccountId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An account id must be a non-empty string.", nameof(value));
        Value = value;
    }

    public bool Equals(AccountId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is AccountId other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);
    public override string ToString() => Value ?? string.Empty;
}
