using System;

namespace KhaozEngine.Render3D;

/// <summary>
/// A stable identity for one moving draw, carried across frames so the scene can remember where the draw was last
/// frame (TEMPORAL-FOUNDATIONS-DESIGN section 3). While temporal rendering is active, <see cref="Scene3D"/> keeps the
/// previous world transform per key, and the previous bone palette for a skinned draw, which is how temporal
/// effects tell an object's own motion from the camera's.
/// <para>
/// <see cref="None"/> is the default and marks a static draw. Terrain, tile ground and placed props need no key and
/// get motion from the camera alone. Give a key only to a draw that moves, and keep it for the object's whole life.
/// An id the game already owns is the natural source (a session id, an entity id), through <see cref="From"/>. A body
/// drawn in several parts derives one key per part with <see cref="Combine"/>.
/// </para>
/// <para>
/// Two draws of the same kind that share a key in one frame collide. A rigid and a skinned draw never collide with each
/// other, and a shadow-only rigid draw is never recorded. The last one wins and
/// <see cref="Scene3D.LastTemporalDiagnostics"/> counts it, so a collision reads as wrong motion on one of the two and
/// never as a failure.
/// </para>
/// </summary>
public readonly struct MotionKey : IEquatable<MotionKey>
{
    // 2^64 / phi, odd: the splitmix64 increment. Also the value Combine returns in the one case its mixer lands on 0.
    const ulong Golden = 0x9E3779B97F4A7C15UL;

    MotionKey(ulong value) => Value = value;

    /// <summary>The 64-bit id. Zero is <see cref="None"/>.</summary>
    public ulong Value { get; }

    /// <summary>No key: the draw is static and gets camera-only motion. The default value.</summary>
    public static MotionKey None => default;

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Value == 0;

    /// <summary>A key over an id the caller already owns. The id is kept as it is, so equal ids give equal keys. Id 0
    /// is <see cref="None"/>.</summary>
    public static MotionKey From(ulong id) => new(id);

    /// <summary>
    /// A key for part <paramref name="part"/> of the body keyed by <paramref name="key"/>, such as a sword in a hand or
    /// one rigid segment of an avatar. The result is stable across runs, processes and platforms (a fixed 64-bit
    /// mixer, never <see cref="HashCode"/>, whose seed changes per process). It differs for every part of one key,
    /// because the mixer is a bijection over distinct inputs, and it is well mixed, so for ids from ordinary sources
    /// (serials, entity ids) parts of different bodies do not fall onto each other or onto small ids passed to
    /// <see cref="From"/>. That is a practical property, not a guarantee: the mixer's input is linear in the key and
    /// the part, so two keys a multiple of the golden-ratio constant apart give the same key for two different parts.
    /// <see cref="None"/> stays <see cref="None"/>, and any other key never yields <see cref="None"/>. Order matters:
    /// combining 1 then 2 is a different key from 2 then 1.
    /// </summary>
    public static MotionKey Combine(MotionKey key, uint part)
    {
        if (key.IsNone) return None;
        ulong mixed = Mix(unchecked(key.Value + ((ulong)part + 1UL) * Golden));
        // The mixer maps only 0 to 0, and for one key at most one part reaches it. Golden stands in for that one.
        return new MotionKey(mixed != 0 ? mixed : Golden);
    }

    // The splitmix64 finalizer (Stafford's mix 13): a bijection on 64-bit values with full avalanche. Adding
    // (part + 1) times an odd constant first makes every part of one key a distinct input.
    static ulong Mix(ulong z)
    {
        unchecked
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <inheritdoc />
    public bool Equals(MotionKey other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MotionKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>Equal when the values are equal.</summary>
    public static bool operator ==(MotionKey left, MotionKey right) => left.Value == right.Value;

    /// <summary>Different when the values differ.</summary>
    public static bool operator !=(MotionKey left, MotionKey right) => left.Value != right.Value;

    /// <inheritdoc />
    public override string ToString() => IsNone ? "MotionKey.None" : $"MotionKey(0x{Value:X16})";
}
