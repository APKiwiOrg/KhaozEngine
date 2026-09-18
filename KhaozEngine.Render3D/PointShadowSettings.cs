using System;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>
/// The point-light shadow budget: whether omnidirectional maps are built at all, how big each cube face is, how
/// many lights can carry one at once, and how much rebuilding one frame is allowed to do.
/// <para>
/// The atlas is ONE R32Float texture of six face columns by <see cref="ResolvedMaxLights"/> rows, allocated
/// LAZILY on the first frame that carries a request, so a game that never asks for a point shadow pays no memory
/// for these numbers. <see cref="AtlasBytes"/> is what it would cost if it did.
/// </para>
/// <para>
/// Rides <see cref="ShadowSettings.PointShadows"/>, and the three <see cref="ShadowSettings.ForDetail"/> profiles
/// seed it: Low turns it off, Default is the values below, High doubles the face and the light budget.
/// </para>
/// </summary>
public sealed class PointShadowSettings
{
    /// <summary>Smallest face resolution a menu value resolves to. Below this a face carries too few texels for
    /// the four-tap compare to mean anything.</summary>
    public const int MinFaceResolution = 64;

    /// <summary>Largest face resolution a menu value resolves to. A row is six of these wide, so the cap is what
    /// keeps the atlas inside an ordinary maximum texture dimension.</summary>
    public const int MaxFaceResolution = 1024;

    /// <summary>Smallest light budget a menu value resolves to. Zero rows would be an atlas with no rows, so the
    /// floor is one rather than none: turn the feature off with <see cref="Enabled"/> instead.</summary>
    public const int MinLights = 1;

    /// <summary>Largest light budget. The frame UBO carries one shadow slot per POINT LIGHT slot, so the budget
    /// can never exceed the fixed point-light array size.</summary>
    public const int MaxLights = ModelRenderer.MaxPointLights;

    /// <summary>Whether point lights may carry shadow maps at all. <c>false</c> renders every point light
    /// unshadowed, whatever each one requested, and releases the atlas.</summary>
    public bool Enabled = true;

    /// <summary>Pixels per axis of ONE cube face. A row of the atlas is six of these wide and one tall. Clamped
    /// into <see cref="MinFaceResolution"/>..<see cref="MaxFaceResolution"/> by
    /// <see cref="ResolvedFaceResolution"/>.</summary>
    public int FaceResolution = 256;

    /// <summary>How many lights may carry a map in one frame, which is also the atlas row count. Requests past it
    /// fall back to unshadowed, nearest to the eye first. Clamped into
    /// <see cref="MinLights"/>..<see cref="MaxLights"/> by <see cref="ResolvedMaxLights"/>.</summary>
    public int MaxShadowedLights = 8;

    /// <summary>How many CACHED (<see cref="LightShadowMode.Static"/>) maps may be re-rendered on one frame. A
    /// cached map is rebuilt only when something under it changed, so this caps the cost of a frame in which
    /// several of them changed at once.</summary>
    public int MaxStaticRebuildsPerFrame = 2;

    /// <summary>How many <see cref="LightShadowMode.Dynamic"/> maps may be rendered on one frame. Every one of
    /// them costs six faces every frame, so this is the hard ceiling on the per-frame pass.</summary>
    public int MaxDynamicLightsPerFrame = 4;

    /// <summary>Largest bias a menu value resolves to, for both knobs. The atlas stores distance over radius, so
    /// this is a quarter of the light's whole reach: past it a receiver is lit by a caster a quarter of the
    /// radius in front of it and the shadow has stopped being a shadow.</summary>
    public const float MaxBias = 0.25f;

    /// <summary>Constant depth bias added to the stored distance before the compare, in radius-normalized units
    /// (the atlas stores distance over radius). Lifts a receiver off its own stored distance. Clamped into
    /// 0..<see cref="MaxBias"/> by <see cref="ResolvedBias"/>, which is what the uniform must be fed.</summary>
    public float Bias = 0.01f;

    /// <summary>Slope-scaled bias, in the same radius-normalized units, added as
    /// <c>SlopeBias * (1 - ndl)</c> off the unbanded grazing angle. Largest where the light grazes the surface,
    /// which is where acne is worst. Clamped into 0..<see cref="MaxBias"/> by <see cref="ResolvedSlopeBias"/>,
    /// which is what the uniform must be fed.</summary>
    public float SlopeBias = 0.02f;

    /// <summary>The face resolution actually used, clamped into
    /// <see cref="MinFaceResolution"/>..<see cref="MaxFaceResolution"/>.</summary>
    public int ResolvedFaceResolution => Math.Clamp(FaceResolution, MinFaceResolution, MaxFaceResolution);

    /// <summary>The light budget actually used, clamped into <see cref="MinLights"/>..<see cref="MaxLights"/>.
    /// Also the atlas row count.</summary>
    public int ResolvedMaxLights => Math.Clamp(MaxShadowedLights, MinLights, MaxLights);

    /// <summary>The constant bias actually used, clamped into 0..<see cref="MaxBias"/>. THE FLOOR IS THE POINT: a
    /// negative bias does not make shadows tighter, it subtracts from the stored distance and turns the compare
    /// the other way, so every receiver reads as occluded by itself and the light leaks through what it lights.
    /// NaN resolves to zero, because <see cref="Math.Clamp(float, float, float)"/> is undefined on it and a NaN
    /// bias poisons the whole compare.</summary>
    public float ResolvedBias => ResolveBias(Bias);

    /// <summary>The slope-scaled bias actually used, clamped into 0..<see cref="MaxBias"/> on the same rule as
    /// <see cref="ResolvedBias"/>.</summary>
    public float ResolvedSlopeBias => ResolveBias(SlopeBias);

    static float ResolveBias(float value) =>
        float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, MaxBias);

    /// <summary>What the atlas would cost in GPU memory at the resolved layout: six face columns by
    /// <see cref="ResolvedMaxLights"/> rows of <see cref="ResolvedFaceResolution"/> square, at 4 bytes a texel of
    /// R32Float colour plus 5 of D32FloatS8UInt depth. Reported for a settings screen, and it is what the atlas
    /// costs ONCE ALLOCATED rather than what a game not using point shadows is paying.</summary>
    public long AtlasBytes
    {
        get
        {
            long res = ResolvedFaceResolution;
            return 6L * res * ResolvedMaxLights * res * 9L;
        }
    }

    /// <summary>A deep copy, so a caller can hand a profile out without the caller's own settings object being
    /// aliased into a live scene.</summary>
    public PointShadowSettings Clone() => new()
    {
        Enabled = Enabled,
        FaceResolution = FaceResolution,
        MaxShadowedLights = MaxShadowedLights,
        MaxStaticRebuildsPerFrame = MaxStaticRebuildsPerFrame,
        MaxDynamicLightsPerFrame = MaxDynamicLightsPerFrame,
        Bias = Bias,
        SlopeBias = SlopeBias,
    };
}

public sealed partial class ShadowSettings
{
    /// <summary>The point-light shadow budget. Its own object rather than more fields here, because it is a
    /// separate feature with a separate atlas and a separate lazy allocation: the key light's settings above
    /// decide the cascaded directional map and nothing in this one touches it.</summary>
    public PointShadowSettings PointShadows = new();
}
