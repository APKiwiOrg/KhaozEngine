using System;

namespace KhaozEngine.App;

/// <summary>
/// A localization key: the typed handle a <see cref="LocalizedText"/> resolves against an
/// <see cref="IStringCatalog"/>. There is deliberately NO implicit conversion from <see cref="string"/> -
/// authoring a <see cref="StringId"/> is an explicit act (a constants class today, a generator later), so a
/// bare string literal can never slip into a player-facing sink.
/// </summary>
public readonly struct StringId : IEquatable<StringId>
{
    /// <summary>The catalog lookup key.</summary>
    public string Key { get; }

    /// <summary>Creates a key. The key must be non-empty.</summary>
    /// <exception cref="ArgumentException">The key is null or empty.</exception>
    public StringId(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Key = key;
    }

    /// <summary>Factory equivalent to the constructor, for a fluent call site.</summary>
    public static StringId Of(string key) => new(key);

    /// <inheritdoc />
    public bool Equals(StringId other) => string.Equals(Key, other.Key, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StringId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Key is null ? 0 : StringComparer.Ordinal.GetHashCode(Key);

    /// <summary>The raw key (for logging/debug).</summary>
    public override string ToString() => Key ?? "";

    public static bool operator ==(StringId a, StringId b) => a.Equals(b);
    public static bool operator !=(StringId a, StringId b) => !a.Equals(b);
}
