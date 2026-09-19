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

    /// <summary>
    /// A map rebuilt EVERY frame. For an effect light (a fireball, a muzzle flash, a thrown torch) that moves or
    /// lives a moment, where a cache would be stale the frame after it was taken.
    /// <para>
    /// It keeps NOTHING across a frame. A dynamic light carries no key, so the scene identifies it by its place in
    /// the light queue, and that place belongs to a different light as soon as one of them expires. So a dynamic
    /// light past <see cref="PointShadowSettings.MaxDynamicLightsPerFrame"/> on a given frame renders unshadowed
    /// for that frame rather than sampling the row it had before. Queue the ones that matter first, or raise the
    /// budget.
    /// </para>
    /// </summary>
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
    readonly float _nearRadius;

    /// <summary>
    /// How far from the bulb, in METRES, a caster is treated as part of the light's own fixture and left out of
    /// this light's map. Zero (the default) leaves the pass exactly as it was.
    /// <para>
    /// A LIGHT PLACED INSIDE CLOSED GEOMETRY IS FULLY SHADOWED BY ITSELF WITHOUT THIS, and every lamp model is
    /// closed geometry: a wall lantern's flame sits inside its own glass and iron, so the nearest surface in
    /// every direction is the fixture a few centimetres away, every receiver past it compares as occluded, and
    /// the light reaches nothing. Set this past the fixture's farthest part from the flame and short of the
    /// nearest surface that must still block (the wall the lantern hangs on), and the fixture stops writing
    /// while the wall keeps writing.
    /// </para>
    /// <para>
    /// It is not a second near plane: the pass keeps its own 5 cm projection near plane whatever this says. A
    /// negative or non-finite value is clamped to zero rather than refused, because a request is presentation
    /// and a light is never worth throwing over.
    /// </para>
    /// </summary>
    public float NearRadius
    {
        get => _nearRadius;
        init => _nearRadius = float.IsFinite(value) && value > 0f ? value : 0f;
    }

    /// <summary>No shadow map, which is the default value of the struct as well, so an unset request costs
    /// nothing and renders exactly as a point light always did.</summary>
    public static LightShadow None => default;

    /// <summary>A cached map for a placed light, keyed by the caller's own stable <paramref name="key"/>.</summary>
    public static LightShadow Static(long key) => new(LightShadowMode.Static, key);

    /// <summary>A cached map for a placed light that sits inside its own fixture, which is every lamp model:
    /// <paramref name="nearRadius"/> metres of geometry around the bulb is left out of the map. See
    /// <see cref="NearRadius"/> for how to size it.</summary>
    public static LightShadow Static(long key, float nearRadius) =>
        new(LightShadowMode.Static, key) { NearRadius = nearRadius };

    /// <summary>A map rebuilt every frame, for a light that moves.</summary>
    public static LightShadow Dynamic => new(LightShadowMode.Dynamic, 0L);

    /// <summary>A map rebuilt every frame for a moving light carried inside its own fixture (a lantern in a
    /// hand, a torch on a cart), leaving <paramref name="nearRadius"/> metres of geometry around the bulb out of
    /// it. See <see cref="NearRadius"/> for how to size it.</summary>
    public static LightShadow DynamicWithNearRadius(float nearRadius) =>
        new(LightShadowMode.Dynamic, 0L) { NearRadius = nearRadius };

    /// <summary>This same request with a near radius of <paramref name="metres"/>, for a caller holding a
    /// <see cref="LightShadow"/> it did not build.</summary>
    public LightShadow WithNearRadius(float metres) => this with { NearRadius = metres };

    /// <summary>Whether this request asks for a map at all.</summary>
    public bool Requested => Mode != LightShadowMode.None;
}
