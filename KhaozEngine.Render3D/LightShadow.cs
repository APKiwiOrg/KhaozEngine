namespace KhaozEngine.Render3D;

/// <summary>
/// How a point light's omnidirectional shadow map is kept up to date, if at all.
/// </summary>
public enum LightShadowMode : byte
{
    /// <summary>No shadow map. The light adds its term everywhere inside its radius, which is what every point
    /// light did before point shadows existed and is the byte-identical path.</summary>
    None = 0,

    /// <summary>A CACHED map behind a caller-supplied key. It is rendered once and re-rendered only when the light
    /// moves, its radius changes, or the rigid casters inside its radius change. For a placed light (a wall
    /// lantern, a lamp post, a forge) whose surroundings almost never move.</summary>
    Static = 1,

    /// <summary>A map rebuilt EVERY frame. For an effect light (a fireball, a muzzle flash, a thrown torch) that
    /// moves or lives a moment, where a cache would be stale the frame after it was taken.</summary>
    Dynamic = 2,
}

/// <summary>
/// The shadow REQUEST a caller attaches to one <see cref="Scene3D.AddLight(System.Numerics.Vector3,KhaozEngine.Primitives.Color,float,float,LightShadow)"/>.
/// It is a request rather than an instruction: the scene budgets how many lights can carry a map in one frame
/// (<see cref="PointShadowSettings.MaxShadowedLights"/> and the per-frame rebuild budgets), and a light past the
/// budget falls back to unshadowed rather than being dropped.
/// <para>
/// <see cref="Key"/> is the caller's own identity for a <see cref="LightShadowMode.Static"/> light and nothing
/// else reads it: the slot cache keys a cached map on it, so the same key across frames is the same map. Two
/// lights sharing one key are one cache entry, which is a caller bug rather than something the engine can detect.
/// A <see cref="LightShadowMode.Dynamic"/> light carries no key because it is rebuilt every frame regardless.
/// </para>
/// </summary>
/// <param name="Mode">Whether a map is requested, and whether it is cached or rebuilt every frame.</param>
/// <param name="Key">The caller's stable identity for a cached map. Zero for every other mode.</param>
public readonly record struct LightShadow(LightShadowMode Mode, long Key)
{
    /// <summary>No shadow map, which is the default value of the struct as well, so an unset request costs
    /// nothing and renders exactly as a point light always did.</summary>
    public static LightShadow None => default;

    /// <summary>A cached map for a placed light, keyed by the caller's own stable <paramref name="key"/>.</summary>
    public static LightShadow Static(long key) => new(LightShadowMode.Static, key);

    /// <summary>A map rebuilt every frame, for a light that moves.</summary>
    public static LightShadow Dynamic => new(LightShadowMode.Dynamic, 0L);

    /// <summary>Whether this request asks for a map at all.</summary>
    public bool Requested => Mode != LightShadowMode.None;
}
