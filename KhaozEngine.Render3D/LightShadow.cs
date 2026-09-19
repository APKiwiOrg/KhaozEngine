using System;
using System.Numerics;

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
    /// for that frame rather than sampling the row it had before. The nearest effects win the dynamic budget.
    /// </para>
    /// </summary>
    Dynamic = 2,
}

/// <summary>
/// The shadow REQUEST a caller attaches to one <see cref="Scene3D.AddLight(System.Numerics.Vector3,KhaozEngine.Primitives.Color,float,float,LightShadow)"/>.
/// It is a request rather than an instruction. Every keyed static request reserves a persistent atlas row,
/// independent of camera distance, while dynamic effects use the stable reserve and per-frame rebuild budget in
/// <see cref="PointShadowSettings"/>. A dynamic request past that budget falls back to unshadowed rather than
/// being dropped. A static set beyond the supported atlas capacity is reported through
/// <see cref="Scene3D.ResolvedPointShadows"/> rather than camera-trimmed.
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

    /// <summary>
    /// The low corner of this light's world-space, axis-aligned EXCLUSION BOX, in the same absolute space the
    /// consumer queues its geometry in. A caster fragment inside the box is left out of this light's map.
    /// <para>
    /// It is the shape <see cref="NearRadius"/> cannot be. A sphere works for a lamp standing in the open, and a
    /// WALL-MOUNTED fixture is the case it fails: the lantern needs the radius past its own plate and arm while
    /// the wall it hangs on is nearer than that, so every radius either leaves the fixture casting or stops the
    /// wall casting. The box is the fixture's own bounds, which clears all of it and leaves the wall a centimetre
    /// outside still writing.
    /// </para>
    /// <para>
    /// Set it through <see cref="WithExclusionBox"/> or <see cref="Static(long,Vector3,Vector3)"/> rather than by
    /// hand: those order the corners per axis and refuse anything that is not a box. Both clearances may be set at
    /// once and either one excludes.
    /// </para>
    /// </summary>
    public Vector3 ExclusionMin { get; init; }

    /// <summary>The high corner of the exclusion box. See <see cref="ExclusionMin"/>.</summary>
    public Vector3 ExclusionMax { get; init; }

    /// <summary>Whether this request carries a real exclusion box: a finite one whose high corner is strictly past
    /// its low corner on all three axes. Anything else (the unset default, a flat box, a corner that is not a
    /// number) contains nothing and is the same as asking for none.</summary>
    public bool HasExclusionBox =>
        AxisSpans(ExclusionMin.X, ExclusionMax.X)
        && AxisSpans(ExclusionMin.Y, ExclusionMax.Y)
        && AxisSpans(ExclusionMin.Z, ExclusionMax.Z);

    static bool AxisSpans(float min, float max) => float.IsFinite(min) && float.IsFinite(max) && max > min;

    /// <summary>
    /// This same request with the world-space exclusion box <paramref name="min"/> to <paramref name="max"/>. The
    /// corners are ORDERED per axis, so a caller may hand over the two corners of its own bounds in whichever
    /// order it holds them.
    /// <para>
    /// A corner that is not a number, or a pair that does not span a real volume, leaves the request EXACTLY as it
    /// was: a request is presentation, and a light is never worth throwing over. So a bad call adds no box, and it
    /// does not take away a good box the request already carried either.
    /// </para>
    /// </summary>
    public LightShadow WithExclusionBox(Vector3 min, Vector3 max)
    {
        var low = new Vector3(MathF.Min(min.X, max.X), MathF.Min(min.Y, max.Y), MathF.Min(min.Z, max.Z));
        var high = new Vector3(MathF.Max(min.X, max.X), MathF.Max(min.Y, max.Y), MathF.Max(min.Z, max.Z));
        LightShadow boxed = this with { ExclusionMin = low, ExclusionMax = high };
        return boxed.HasExclusionBox ? boxed : this;
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

    /// <summary>A cached map for a placed light mounted ON something, which is every wall lantern: the geometry
    /// inside the world-space box <paramref name="exclusionMin"/> to <paramref name="exclusionMax"/> is left out
    /// of the map, and the surface it is mounted on, outside the box, still casts. See
    /// <see cref="ExclusionMin"/>.</summary>
    public static LightShadow Static(long key, Vector3 exclusionMin, Vector3 exclusionMax) =>
        new LightShadow(LightShadowMode.Static, key).WithExclusionBox(exclusionMin, exclusionMax);

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
