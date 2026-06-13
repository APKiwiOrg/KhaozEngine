using System;
using System.Collections.Generic;

namespace KhaozEngine.Sprites;

/// <summary>
/// A keyed store of <see cref="DirectionalAnimatedSprite"/> instances with a single bulk
/// <see cref="Update(float)"/> that advances every registered sprite's animation clock once per
/// frame. The registry takes already-built sprites: how they are loaded (embedded resources,
/// content pipeline, generated) stays game-side, since resource names are game-specific.
/// </summary>
public sealed class SpriteRegistry
{
    private readonly Dictionary<string, DirectionalAnimatedSprite> _sprites = new();

    /// <summary>Number of registered sprites.</summary>
    public int Count => _sprites.Count;

    /// <summary>
    /// Registers <paramref name="sprite"/> under <paramref name="key"/>. The key must be non-empty
    /// and not already registered.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is null/empty or already registered.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="sprite"/> is null.</exception>
    public void Add(string key, DirectionalAnimatedSprite sprite)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key must be non-empty.", nameof(key));
        ArgumentNullException.ThrowIfNull(sprite);
        if (_sprites.ContainsKey(key))
            throw new ArgumentException($"A sprite is already registered under key '{key}'.", nameof(key));
        _sprites.Add(key, sprite);
    }

    /// <summary>Returns the sprite registered under <paramref name="key"/>, or null if none is.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is null or empty.</exception>
    public DirectionalAnimatedSprite? Get(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key must be non-empty.", nameof(key));
        return _sprites.TryGetValue(key, out var sprite) ? sprite : null;
    }

    /// <summary>True if a sprite is registered under <paramref name="key"/>.</summary>
    public bool Contains(string key) => !string.IsNullOrEmpty(key) && _sprites.ContainsKey(key);

    /// <summary>Advances every registered sprite's animation by <paramref name="deltaSeconds"/>.</summary>
    public void Update(float deltaSeconds)
    {
        foreach (var sprite in _sprites.Values)
            sprite.Update(deltaSeconds);
    }
}
