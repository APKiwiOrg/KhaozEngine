using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Which anti-aliasing technique the 3D scene applies. A single-choice selector like the AA dropdown most games
    /// ship. Each mode trades cost against what it smooths:
    /// <list type="bullet">
    /// <item><see cref="None"/> - no AA (the default; existing behaviour, no extra cost).</item>
    /// <item><see cref="Fxaa"/> - a cheap fullscreen post pass that softens high-contrast edges (geometry AND shaded
    ///   interiors). One extra pass; good default when a GPU can't afford supersampling.</item>
    /// <item><see cref="Msaa"/> - hardware multisampling of the geometry pass (<see cref="AntiAliasing.MsaaSamples"/>
    ///   taps). Anti-aliases geometry EDGES only, not shaded texture interiors, so it does NOT fix high-frequency
    ///   albedo shimmer (use <see cref="Ssaa"/>/<see cref="Fxaa"/> for that). The right cheap tool for foliage / edge
    ///   crawl.</item>
    /// <item><see cref="Ssaa"/> - supersample the whole image at <see cref="AntiAliasing.SsaaFactor"/> per axis, then
    ///   downsample. Anti-aliases geometry AND shaded interiors (the strongest, and the only one that kills
    ///   high-frequency terrain/foliage shimmer), at ~factor^2 the fragment cost.</item>
    /// </list>
    /// Extend by adding modes (future TAA / SMAA) - unknown/unsupported modes resolve to a safe fallback rather than
    /// throwing (see <see cref="AntiAliasing.ResolveFor"/>).
    /// </summary>
    public enum AntiAliasingMode
    {
        /// <summary>No anti-aliasing (default).</summary>
        None,
        /// <summary>Fast approximate AA: one cheap fullscreen post pass.</summary>
        Fxaa,
        /// <summary>Hardware multisample AA of the geometry pass (edges only).</summary>
        Msaa,
        /// <summary>Supersample AA: render larger, downsample (edges AND shaded interiors).</summary>
        Ssaa,
    }

    /// <summary>
    /// The anti-aliasing selection: a <see cref="AntiAliasingMode"/> plus the parameter that mode needs
    /// (<see cref="MsaaSamples"/> for <see cref="AntiAliasingMode.Msaa"/>, <see cref="SsaaFactor"/> for
    /// <see cref="AntiAliasingMode.Ssaa"/>). Build one with the factories (<see cref="Off"/> / <see cref="Fxaa"/> /
    /// <see cref="Msaa"/> / <see cref="Ssaa"/>) and assign it to <see cref="RenderQuality.AntiAliasing"/>. Immutable
    /// value; <see cref="ResolveFor"/> clamps a request to what the device can actually do (never throws).
    /// </summary>
    public readonly struct AntiAliasing : IEquatable<AntiAliasing>
    {
        /// <summary>The selected technique.</summary>
        public AntiAliasingMode Mode { get; }
        /// <summary>MSAA sample count (2 / 4 / 8) when <see cref="Mode"/> is <see cref="AntiAliasingMode.Msaa"/>;
        /// ignored otherwise. Clamped to the device maximum (and to a power of two) by <see cref="ResolveFor"/>.</summary>
        public int MsaaSamples { get; }
        /// <summary>Supersample factor per axis (e.g. 2 / 3 / 4) when <see cref="Mode"/> is
        /// <see cref="AntiAliasingMode.Ssaa"/>; ignored otherwise. Forces <see cref="RenderScale.MatchViewport"/> and
        /// drives <see cref="PixelPostProcessSettings.Supersample"/>. Clamped to at least 1.</summary>
        public float SsaaFactor { get; }

        AntiAliasing(AntiAliasingMode mode, int msaaSamples, float ssaaFactor)
        {
            Mode = mode; MsaaSamples = msaaSamples; SsaaFactor = ssaaFactor;
        }

        /// <summary>No anti-aliasing (the default). Existing render behaviour, no extra cost.</summary>
        public static AntiAliasing Off => new(AntiAliasingMode.None, 1, 1f);
        /// <summary>Fast approximate AA (one cheap fullscreen post pass).</summary>
        public static AntiAliasing Fxaa => new(AntiAliasingMode.Fxaa, 1, 1f);
        /// <summary>Hardware multisample AA with <paramref name="samples"/> taps (clamped to the device max at
        /// resolve). 2 / 4 / 8 are the usual choices.</summary>
        public static AntiAliasing Msaa(int samples) => new(AntiAliasingMode.Msaa, Math.Max(1, samples), 1f);
        /// <summary>Supersample AA at <paramref name="factor"/> per axis (e.g. 2 / 3 / 4). Costs ~factor^2 in
        /// fragment shading; the strongest AA and the only one that removes high-frequency shimmer.</summary>
        public static AntiAliasing Ssaa(float factor) => new(AntiAliasingMode.Ssaa, 1, MathF.Max(1f, factor));

        /// <summary>
        /// Return a copy clamped to what <paramref name="caps"/> supports, so an unsupported request degrades
        /// gracefully instead of throwing or failing device creation:
        /// <list type="bullet">
        /// <item><see cref="AntiAliasingMode.Msaa"/> is clamped DOWN to the largest supported power-of-two that is
        ///   &lt;= <see cref="GpuCapabilities.MaxMsaaSampleCount"/>; if the device supports no MSAA (max &lt;= 1) it
        ///   falls back to <see cref="Fxaa"/> (a cheap AA that always works).</item>
        /// <item><see cref="AntiAliasingMode.Ssaa"/> keeps its factor (clamped to at least 1; the target size is
        ///   separately capped by <see cref="PixelPostProcessSettings.MaxRenderWidth"/>/<c>Height</c>).</item>
        /// <item><see cref="AntiAliasingMode.None"/> / <see cref="AntiAliasingMode.Fxaa"/> are unchanged (always
        ///   available).</item>
        /// </list>
        /// Pure; safe to call every frame.
        /// </summary>
        public AntiAliasing ResolveFor(in GpuCapabilities caps)
        {
            switch (Mode)
            {
                case AntiAliasingMode.Msaa:
                    int max = Math.Max(1, caps.MaxMsaaSampleCount);
                    if (max <= 1) return Fxaa;                       // device can't MSAA: degrade to a cheap AA that works
                    int want = Math.Clamp(MsaaSamples, 1, max);
                    return Msaa(LargestPowerOfTwoAtMost(want));
                case AntiAliasingMode.Ssaa:
                    return Ssaa(SsaaFactor);                          // factor already >= 1; target size capped elsewhere
                default:
                    return this;                                     // None / Fxaa always available
            }
        }

        static int LargestPowerOfTwoAtMost(int n)
        {
            int p = 1;
            while (p * 2 <= n) p *= 2;
            return p;
        }

        public bool Equals(AntiAliasing other) =>
            Mode == other.Mode && MsaaSamples == other.MsaaSamples && SsaaFactor.Equals(other.SsaaFactor);
        public override bool Equals(object? obj) => obj is AntiAliasing a && Equals(a);
        public override int GetHashCode() => HashCode.Combine(Mode, MsaaSamples, SsaaFactor);
        public static bool operator ==(AntiAliasing a, AntiAliasing b) => a.Equals(b);
        public static bool operator !=(AntiAliasing a, AntiAliasing b) => !a.Equals(b);
        public override string ToString() => Mode switch
        {
            AntiAliasingMode.Msaa => $"MSAA x{MsaaSamples}",
            AntiAliasingMode.Ssaa => $"SSAA x{SsaaFactor:0.##}",
            AntiAliasingMode.Fxaa => "FXAA",
            _ => "None",
        };
    }

    /// <summary>
    /// Graphics-quality knobs for the 3D scene, grouped so a game's options menu maps cleanly onto them. Today it
    /// carries the <see cref="AntiAliasing"/> selection; it is the extension point for further quality settings
    /// (anisotropy, shadow/texture quality, future TAA) without churning <see cref="PixelPostProcessSettings"/>.
    /// Reachable as <see cref="PixelPostProcessSettings.Quality"/>. Defaults are the historical no-cost behaviour
    /// (<see cref="AntiAliasing.Off"/>), so existing scenes are unchanged until a game opts in.
    /// </summary>
    public sealed class RenderQuality
    {
        /// <summary>The anti-aliasing technique. Default <see cref="AntiAliasing.Off"/> (no AA, no cost). Set it from
        /// a menu, e.g. <c>Post.Quality.AntiAliasing = AntiAliasing.Ssaa(3f)</c>. Validate a menu choice against the
        /// device with <see cref="AntiAliasing.ResolveFor"/>.</summary>
        public AntiAliasing AntiAliasing = AntiAliasing.Off;
    }
}
