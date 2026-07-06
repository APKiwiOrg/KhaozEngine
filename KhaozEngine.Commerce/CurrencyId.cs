using System;

namespace KhaozEngine.Commerce;

/// <summary>A currency identifier. The wallet is agnostic to how currencies are defined.</summary>
public readonly struct CurrencyId : IEquatable<CurrencyId>
{
    /// <summary>The opaque, non-empty currency key.</summary>
    public string Value { get; }

    /// <summary>Wraps a non-empty currency key.</summary>
    /// <exception cref="ArgumentException">The value is null, empty, or whitespace.</exception>
    public CurrencyId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A currency id must be a non-empty string.", nameof(value));
        Value = value;
    }

    public bool Equals(CurrencyId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is CurrencyId other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);
    public override string ToString() => Value ?? string.Empty;
}
