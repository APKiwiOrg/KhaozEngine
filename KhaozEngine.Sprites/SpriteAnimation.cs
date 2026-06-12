using System;
using System.Collections.Generic;

namespace KhaozEngine.Sprites;

/// <summary>
/// An ordered list of <see cref="SpriteFrame"/>s plus a per-frame duration and a loop flag.
/// Immutable data; playback state lives in <see cref="SpriteAnimationPlayer"/>.
/// </summary>
public sealed class SpriteAnimation
{
    /// <summary>The frames, in playback order.</summary>
    public IReadOnlyList<SpriteFrame> Frames { get; }

    /// <summary>How long each frame is shown, in seconds.</summary>
    public float FrameDuration { get; }

    /// <summary>Whether playback wraps to the start after the last frame.</summary>
    public bool Loop { get; }

    /// <summary>Number of frames.</summary>
    public int FrameCount => Frames.Count;

    /// <summary>Total time for one pass through all frames, in seconds.</summary>
    public float TotalDuration => FrameDuration * Frames.Count;

    /// <summary>Creates an animation from frames and an explicit per-frame duration (seconds).</summary>
    public SpriteAnimation(IReadOnlyList<SpriteFrame> frames, float frameDuration, bool loop = true)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0) throw new ArgumentException("Animation needs at least one frame.", nameof(frames));
        if (frameDuration <= 0f) throw new ArgumentOutOfRangeException(nameof(frameDuration), "Frame duration must be positive.");
        Frames = frames;
        FrameDuration = frameDuration;
        Loop = loop;
    }

    /// <summary>Creates an animation from frames and a frame rate (frames per second).</summary>
    public static SpriteAnimation FromFps(IReadOnlyList<SpriteFrame> frames, float fps, bool loop = true)
    {
        if (fps <= 0f) throw new ArgumentOutOfRangeException(nameof(fps), "FPS must be positive.");
        return new SpriteAnimation(frames, 1f / fps, loop);
    }
}
