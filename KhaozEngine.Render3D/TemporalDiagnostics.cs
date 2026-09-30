using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>
/// The last rendered frame's temporal state, read via <see cref="Scene3D.LastTemporalDiagnostics"/>. A value snapshot
/// in the shape of <see cref="ShadowPassDiagnostics"/>: always on and allocation-free to read. Every field but the
/// counts is written once per frame, on the frame's first render. The counts, <see cref="CountsFrameIndex"/> and the
/// three pixel counts after it, are sampled only on <see cref="Scene3D.RequestTemporalCounts"/> and land at the
/// <see cref="Scene3D.PrepareFrame"/> after the frame they were sampled on. They stay until the next request lands, so
/// from the next render on they name an earlier frame than <see cref="FrameIndex"/>. Before the first render the whole
/// value is <c>default</c>, so <see cref="UpscaleRatio"/> and <see cref="CountsFrameIndex"/> read 0 there rather than
/// the 1 and -1 a rendered frame starts from. Read it on the render thread after the frame renders. Two readings are
/// equal when every property is.
/// </summary>
public readonly struct TemporalDiagnostics : IEquatable<TemporalDiagnostics>
{
    /// <summary>The frame's index, advanced once per <see cref="Scene3D.Begin"/>.</summary>
    public long FrameIndex { get; }

    /// <summary>Where the frame index falls in the jitter sequence, from 0, reported whether or not the jitter is
    /// applied, see <see cref="JitterPixels"/>.</summary>
    public int JitterPhase { get; }

    /// <summary>The sub-pixel jitter the frame rasterised with, in internal pixels, x right and y down, each in
    /// [-0.5, 0.5). Zero while temporal rendering is inactive.</summary>
    public Vector2 JitterPixels { get; }

    /// <summary>Rigid draws the frame submitted with a motion key while temporal rendering was active,
    /// <see cref="RigidInstanceDraw.ShadowOnly"/> draws excluded. Counted at submission, before culling, so a keyed
    /// draw the camera never sees still counts, and only for draws made before the frame's first render. Zero while
    /// temporal rendering is off.</summary>
    public int KeyedRigid { get; }

    /// <summary>Skinned draws the frame submitted with a motion key while temporal rendering was active. Counted at
    /// submission, before culling, and only for draws made before the frame's first render. Zero while temporal
    /// rendering is off.</summary>
    public int KeyedSkinned { get; }

    /// <summary>Keyed draws counted above whose key was already counted this frame in the same map, so a rigid and a
    /// skinned draw that share a key never collide. Zero while temporal rendering is off.</summary>
    public int KeyCollisions { get; }

    /// <summary>Whether the frame had a previous frame to reproject from.</summary>
    public bool HistoryValid { get; }

    /// <summary>Why the history was last reset. <see cref="TemporalResetReason.FirstFrame"/> while temporal rendering
    /// is off and on the first frame it is on.</summary>
    public TemporalResetReason LastReset { get; }

    /// <summary>Width of the internal target the frame's first render rendered at, under every anti-aliasing mode. The
    /// four sizes read zero, and <see cref="UpscaleRatio"/> 1, on a frame whose first render had no display area, such
    /// as a minimised window, rather than the last display size beside whatever internal target such a render
    /// allocates: a pixel or two where the internal size follows the viewport, the unchanged fixed size under
    /// <see cref="RenderScale.FixedInternal"/>.</summary>
    public int InternalWidth { get; }

    /// <summary>Height of the internal target the frame's first render rendered at.</summary>
    public int InternalHeight { get; }

    /// <summary>Width of the target the frame's first render presented to.</summary>
    public int DisplayWidth { get; }

    /// <summary>Height of the target the frame's first render presented to.</summary>
    public int DisplayHeight { get; }

    /// <summary>The <see cref="TemporalSettings.Upscale"/> setting at the frame's first render, reported under every
    /// anti-aliasing mode, and whether or not an explicit <see cref="TemporalSettings.UpscaleRatio"/> overrides
    /// it.</summary>
    public TemporalUpscale Preset { get; }

    /// <summary>The internal to display width ratio of a frame the temporal resolve ran on, which shows an explicit
    /// <see cref="TemporalSettings.UpscaleRatio"/> and the render size caps, and 1 on any other frame, such as one
    /// under another anti-aliasing mode, whatever its internal size.</summary>
    public float UpscaleRatio { get; }

    /// <summary>The frame the counts below were sampled on, minus 1 from the first render until the first
    /// <see cref="Scene3D.RequestTemporalCounts"/> is sampled and read back.</summary>
    public long CountsFrameIndex { get; }

    /// <summary>Estimated display pixels whose history was rejected by depth, by an off-screen reprojection, or on a
    /// still surface because a moving surface the camera follows left it beside its edge, or on it where a surface
    /// moving otherwise on screen shows now, as the ground an avatar uncovers (the band and the followed mark of
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23), from a 32 by 18 grid of 16 samples a cell. A followed surface
    /// that stops keeps its own history.</summary>
    public int DisoccludedPixels { get; }

    /// <summary>Estimated display pixels the reactive estimate marked, on the same grid.</summary>
    public int ReactivePixels { get; }

    /// <summary>Estimated display pixels whose history the variance clip moved by more than 1/1024 in luma-weighted
    /// YCoCg, a quarter of an 8-bit display step, on the same grid. A smaller move, such as the half-float rounding the
    /// clip trims on flat content, does not count.</summary>
    public int ClippedPixels { get; }

    internal TemporalDiagnostics(long frameIndex, int jitterPhase, Vector2 jitterPixels, int keyedRigid,
        int keyedSkinned, int keyCollisions, bool historyValid, TemporalResetReason lastReset, int internalWidth = 0,
        int internalHeight = 0, int displayWidth = 0, int displayHeight = 0,
        TemporalUpscale preset = TemporalUpscale.Native, float upscaleRatio = 1f, long countsFrameIndex = -1,
        int disoccludedPixels = 0, int reactivePixels = 0, int clippedPixels = 0)
    {
        FrameIndex = frameIndex;
        JitterPhase = jitterPhase;
        JitterPixels = jitterPixels;
        KeyedRigid = keyedRigid;
        KeyedSkinned = keyedSkinned;
        KeyCollisions = keyCollisions;
        HistoryValid = historyValid;
        LastReset = lastReset;
        InternalWidth = internalWidth;
        InternalHeight = internalHeight;
        DisplayWidth = displayWidth;
        DisplayHeight = displayHeight;
        Preset = preset;
        UpscaleRatio = upscaleRatio;
        CountsFrameIndex = countsFrameIndex;
        DisoccludedPixels = disoccludedPixels;
        ReactivePixels = reactivePixels;
        ClippedPixels = clippedPixels;
    }

    /// <summary>This reading with the history marked invalid for <paramref name="reason"/>.</summary>
    internal TemporalDiagnostics WithHistoryReset(TemporalResetReason reason) => new(FrameIndex, JitterPhase, JitterPixels,
        KeyedRigid, KeyedSkinned, KeyCollisions, historyValid: false, reason, InternalWidth, InternalHeight, DisplayWidth,
        DisplayHeight, Preset, UpscaleRatio, CountsFrameIndex, DisoccludedPixels, ReactivePixels, ClippedPixels);

    /// <summary>This reading with the frame's sizes, preset and ratio and the latest counts in place of its own.</summary>
    internal TemporalDiagnostics WithSizesAndCounts(int internalWidth, int internalHeight, int displayWidth,
        int displayHeight, TemporalUpscale preset, float upscaleRatio, long countsFrameIndex, int disoccludedPixels,
        int reactivePixels, int clippedPixels) => new(FrameIndex, JitterPhase, JitterPixels, KeyedRigid, KeyedSkinned,
        KeyCollisions, HistoryValid, LastReset, internalWidth, internalHeight, displayWidth, displayHeight, preset,
        upscaleRatio, countsFrameIndex, disoccludedPixels, reactivePixels, clippedPixels);

    /// <inheritdoc/>
    public bool Equals(TemporalDiagnostics other) =>
        FrameIndex == other.FrameIndex && JitterPhase == other.JitterPhase && JitterPixels.Equals(other.JitterPixels)
        && KeyedRigid == other.KeyedRigid && KeyedSkinned == other.KeyedSkinned && KeyCollisions == other.KeyCollisions
        && HistoryValid == other.HistoryValid && LastReset == other.LastReset && InternalWidth == other.InternalWidth
        && InternalHeight == other.InternalHeight && DisplayWidth == other.DisplayWidth
        && DisplayHeight == other.DisplayHeight && Preset == other.Preset && UpscaleRatio.Equals(other.UpscaleRatio)
        && CountsFrameIndex == other.CountsFrameIndex && DisoccludedPixels == other.DisoccludedPixels
        && ReactivePixels == other.ReactivePixels && ClippedPixels == other.ClippedPixels;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TemporalDiagnostics other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FrameIndex);
        hash.Add(JitterPhase);
        hash.Add(JitterPixels);
        hash.Add(KeyedRigid);
        hash.Add(KeyedSkinned);
        hash.Add(KeyCollisions);
        hash.Add(HistoryValid);
        hash.Add(LastReset);
        hash.Add(InternalWidth);
        hash.Add(InternalHeight);
        hash.Add(DisplayWidth);
        hash.Add(DisplayHeight);
        hash.Add(Preset);
        hash.Add(UpscaleRatio);
        hash.Add(CountsFrameIndex);
        hash.Add(DisoccludedPixels);
        hash.Add(ReactivePixels);
        hash.Add(ClippedPixels);
        return hash.ToHashCode();
    }

    /// <summary>Whether two readings are equal in every property.</summary>
    public static bool operator ==(TemporalDiagnostics left, TemporalDiagnostics right) => left.Equals(right);

    /// <summary>Whether two readings differ in any property.</summary>
    public static bool operator !=(TemporalDiagnostics left, TemporalDiagnostics right) => !left.Equals(right);
}
