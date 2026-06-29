using System;

namespace KhaozEngine.WorldStore;

/// <summary>One entry exposed by <see cref="IEnumerableWorldStore.EnumerateAsync"/>: the key, when it was last
/// written, and its stored size in bytes when the backend can report it cheaply (null otherwise).</summary>
public readonly record struct WorldStoreEntry(string Key, DateTimeOffset UpdatedAt, long? Size);
