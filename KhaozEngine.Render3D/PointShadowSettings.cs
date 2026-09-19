using System;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>
/// How a receiver filters a point light's shadow map, which is a choice between a crisp edge and a believable one.
/// </summary>
public enum PointShadowFilter : byte
{
    /// <summary>Four taps at half-texel offsets inside the cube face's own atlas cell, averaged. The cheapest
    /// thing that is not a single tap, and the edge it draws is one atlas texel wide wherever it stands: a
    /// straight hard line that reads as a stencil rather than as light. This is what shipped first, and it is what
    /// a low-end profile should keep.</summary>
    Hard = 0,

    /// <summary>A contact-hardening filter: a short blocker search decides how far the occluder is in front of the
    /// receiver, and the kernel is widened in proportion, so the shadow stays crisp where it touches what casts it
    /// and spreads the further the receiver stands behind it. Every tap runs the cube-face select for itself, so
    /// the kernel crosses a face boundary without a seam. Costs about fifteen atlas fetches against Hard's
    /// four.</summary>
    Soft = 1,
}

/// <summary>
/// The point-light shadow budget: whether omnidirectional maps are built at all, how big each cube face is, how
/// many lights can carry one at once, and how much rebuilding one frame is allowed to do.
/// <para>
/// The atlas is ONE R32Float texture of six face columns. Its configured row floor is
/// <see cref="ResolvedMaxLights"/>, and keyed static requests expand it at the existing frame boundary. The first
/// request renders one frame unshadowed and carries its map from the next. A game that never asks allocates
/// nothing. <see cref="AtlasBytes"/> reports the configured floor before static expansion.
/// </para>
/// <para>
/// Rides <see cref="ShadowSettings.PointShadows"/>, and the three <see cref="ShadowSettings.ForDetail"/> profiles
/// seed it: Low turns it off, Default is the values below (256 by 8, about 27 MiB), High is 384 by 12 (about
/// 91 MiB). Those row counts are a floor. Keyed static requests expand the live atlas as needed, with a stable
/// dynamic reserve, and the live face resolution falls before any static row is discarded.
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

    /// <summary>Conservative maximum atlas width or height when a backend does not expose a tighter device limit.
    /// The six-column layout and the minimum face resolution derive every other public capacity limit from this
    /// one value.</summary>
    public const int MaxAtlasExtent = 16384;

    /// <summary>Smallest light budget a menu value resolves to. Zero rows would be an atlas with no rows, so the
    /// floor is one rather than none: turn the feature off with <see cref="Enabled"/> instead.</summary>
    public const int MinLights = 1;

    /// <summary>Largest supported row count at <see cref="MinFaceResolution"/> within
    /// <see cref="MaxAtlasExtent"/>. This is independent of receiver light-record capacity.</summary>
    public const int MaxLights = MaxAtlasExtent / MinFaceResolution;

    /// <summary>Whether point lights may carry shadow maps at all. <c>false</c> renders every point light
    /// unshadowed, whatever each one requested, and releases the atlas.</summary>
    public bool Enabled = true;

    /// <summary>Pixels per axis of ONE cube face. A row of the atlas is six of these wide and one tall. Clamped
    /// into <see cref="MinFaceResolution"/>..<see cref="MaxFaceResolution"/> by
    /// <see cref="ResolvedFaceResolution"/>.</summary>
    public int FaceResolution = 256;

    /// <summary>The configured atlas row floor and dynamic effect budget. Keyed static requests expand the live
    /// row count beyond it. Dynamic rows use its remaining capacity and never displace a keyed static row. Clamped
    /// into <see cref="MinLights"/>..<see cref="MaxLights"/> by <see cref="ResolvedMaxLights"/>.</summary>
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

    /// <summary>Largest emitter size a menu value resolves to, in metres. A metre of apparent flame is already a
    /// bonfire, and past it the blocker search is casting about so far that it finds occluders which have nothing
    /// to do with the edge being drawn.</summary>
    public const float MaxLightSizeMetres = 1f;

    /// <summary>Smallest penumbra ceiling a menu value resolves to, in atlas texels. Below one texel the filter
    /// can never reach past the tap it started on, which is a soft filter rendering hard at the price of the
    /// blocker search.</summary>
    public const float PenumbraTexelsFloor = 1f;

    /// <summary>Largest penumbra ceiling a menu value resolves to, in atlas texels. The filter takes a fixed nine
    /// taps however wide it spreads them, so past this they are far enough apart to read as nine separate shadows
    /// rather than as one soft one.</summary>
    public const float PenumbraTexelsCeiling = 16f;

    /// <summary>Which filter a receiver uses. <see cref="PointShadowFilter.Soft"/> is the default and is what
    /// makes a doorway wedge read as light rather than as a stencil.</summary>
    public PointShadowFilter Filter = PointShadowFilter.Soft;

    /// <summary>How big the emitter looks, in metres, which is the ONLY thing that decides how far a
    /// <see cref="PointShadowFilter.Soft"/> penumbra spreads: the shadow widens as
    /// <c>LightSizeMetres * (receiverDistance - blockerDistance) / blockerDistance</c>. A candle at a couple of
    /// centimetres throws an almost hard edge, and a metre-wide fire throws a very soft one. Clamped into
    /// 0..<see cref="MaxLightSizeMetres"/> by <see cref="ResolvedLightSizeMetres"/>, and read by no other
    /// filter.</summary>
    public float LightSizeMetres = 0.15f;

    /// <summary>The ceiling on that spread, in ATLAS TEXELS of the light's own cube face, which is what keeps the
    /// filter's nine taps close enough together to still describe one edge. Raising it buys a softer far shadow
    /// and spends the taps over a wider area, where a very wide kernel starts to band. Clamped into
    /// <see cref="PenumbraTexelsFloor"/>..<see cref="PenumbraTexelsCeiling"/> by
    /// <see cref="ResolvedMaxPenumbraTexels"/>.</summary>
    public float MaxPenumbraTexels = 6f;

    /// <summary>The face resolution actually used, clamped into
    /// <see cref="MinFaceResolution"/>..<see cref="MaxFaceResolution"/>.</summary>
    public int ResolvedFaceResolution => Math.Clamp(FaceResolution, MinFaceResolution, MaxFaceResolution);

    /// <summary>The configured row floor and dynamic effect budget, clamped into
    /// <see cref="MinLights"/>..<see cref="MaxLights"/>. The live atlas may carry more rows for keyed statics.</summary>
    public int ResolvedMaxLights => Math.Clamp(MaxShadowedLights, MinLights, MaxLights);

    /// <summary>Largest face resolution that keeps <paramref name="rows"/> inside the conservative atlas extent.
    /// Rows are preserved and resolution falls first.</summary>
    internal int ResolveFaceResolution(int rows)
    {
        int faceByHeight = MaxAtlasExtent / Math.Max(1, rows);
        int faceByWidth = MaxAtlasExtent / PointShadowMath.FaceCount;
        return Math.Max(MinFaceResolution,
            Math.Min(ResolvedFaceResolution, Math.Min(faceByHeight, faceByWidth)));
    }

    /// <summary>The constant bias actually used, clamped into 0..<see cref="MaxBias"/>. THE FLOOR IS THE POINT: a
    /// negative bias does not make shadows tighter, it subtracts from the stored distance and turns the compare
    /// the other way, so every receiver reads as occluded by itself and the light leaks through what it lights.
    /// NaN resolves to zero, because <see cref="Math.Clamp(float, float, float)"/> is undefined on it and a NaN
    /// bias poisons the whole compare.</summary>
    public float ResolvedBias => ResolveBias(Bias);

    /// <summary>The slope-scaled bias actually used, clamped into 0..<see cref="MaxBias"/> on the same rule as
    /// <see cref="ResolvedBias"/>.</summary>
    public float ResolvedSlopeBias => ResolveBias(SlopeBias);

    /// <summary>The emitter size actually used, clamped into 0..<see cref="MaxLightSizeMetres"/> on
    /// <see cref="ResolvedBias"/>'s rule: NaN resolves to zero rather than being handed to
    /// <see cref="Math.Clamp(float, float, float)"/>, which is undefined on it, and a NaN here would poison every
    /// tap offset in the filter rather than one compare.</summary>
    public float ResolvedLightSizeMetres =>
        float.IsNaN(LightSizeMetres) ? 0f : Math.Clamp(LightSizeMetres, 0f, MaxLightSizeMetres);

    /// <summary>The penumbra ceiling actually used, clamped into
    /// <see cref="PenumbraTexelsFloor"/>..<see cref="PenumbraTexelsCeiling"/>. NaN resolves to the FLOOR rather
    /// than to zero: zero is not a value this knob has, and the floor is the conservative answer.</summary>
    public float ResolvedMaxPenumbraTexels =>
        float.IsNaN(MaxPenumbraTexels)
            ? PenumbraTexelsFloor
            : Math.Clamp(MaxPenumbraTexels, PenumbraTexelsFloor, PenumbraTexelsCeiling);

    static float ResolveBias(float value) =>
        float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, MaxBias);

    /// <summary>What the configured atlas floor costs in GPU memory: six face columns by
    /// <see cref="ResolvedMaxLights"/> rows at the largest face resolution that fits
    /// <see cref="MaxAtlasExtent"/>, with 4 colour bytes plus 5 depth-stencil bytes per texel. Keyed static
    /// expansion is visible through <see cref="Scene3D.ResolvedPointShadows"/> instead.</summary>
    public long AtlasBytes
    {
        get
        {
            long res = ResolveFaceResolution(ResolvedMaxLights);
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
        Filter = Filter,
        LightSizeMetres = LightSizeMetres,
        MaxPenumbraTexels = MaxPenumbraTexels,
    };
}

public sealed partial class ShadowSettings
{
    /// <summary>The point-light shadow budget. Its own object rather than more fields here, because it is a
    /// separate feature with a separate atlas brought up at its own frame boundary: the key light's settings
    /// above decide the cascaded directional map and nothing in this one touches it.</summary>
    public PointShadowSettings PointShadows = new();
}
