using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Sprites;

/// <summary>
/// A lightweight clock that plays a <see cref="SpriteAnimation"/>: advances by a time delta and
/// exposes the current frame. Feed it a <c>float</c> seconds delta (e.g. from
/// <c>KhaozEngine.Time.GameClock.ScaledDeltaSeconds</c>) or a <see cref="GameTime"/>.
/// </summary>
public sealed class SpriteAnimationPlayer
{
    private float _elapsed;

    /// <summary>The animation currently playing.</summary>
    public SpriteAnimation Animation { get; private set; }

    /// <summary>Index of the current frame within <see cref="Animation"/>.</summary>
    public int CurrentFrameIndex { get; private set; }

    /// <summary>True once a non-looping animation has reached and held its last frame.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>The current frame.</summary>
    public SpriteFrame CurrentFrame => Animation.Frames[CurrentFrameIndex];

    /// <summary>Creates a player positioned on the first frame of <paramref name="animation"/>.</summary>
    public SpriteAnimationPlayer(SpriteAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        Animation = animation;
    }

    /// <summary>Advances playback by <paramref name="deltaSeconds"/>.</summary>
    public void Update(float deltaSeconds)
    {
        if (IsFinished || deltaSeconds <= 0f)
            return;

        _elapsed += deltaSeconds;
        // Tolerance so a delta that is an exact multiple of the frame duration advances predictably
        // instead of dropping a frame to float-accumulation noise (e.g. 0.7f is just under 7*0.1f).
        float threshold = Animation.FrameDuration - Animation.FrameDuration * 1e-3f;
        while (_elapsed >= threshold)
        {
            _elapsed -= Animation.FrameDuration;
            int next = CurrentFrameIndex + 1;
            if (next < Animation.FrameCount)
            {
                CurrentFrameIndex = next;
            }
            else if (Animation.Loop)
            {
                CurrentFrameIndex = next % Animation.FrameCount;
            }
            else
            {
                CurrentFrameIndex = Animation.FrameCount - 1;
                IsFinished = true;
                _elapsed = 0f;
                break;
            }
        }
    }

    /// <summary>Advances playback by the elapsed time in <paramref name="gameTime"/>.</summary>
    public void Update(GameTime gameTime) =>
        Update((float)gameTime.ElapsedGameTime.TotalSeconds);

    /// <summary>Rewinds to the first frame and clears the finished flag.</summary>
    public void Reset()
    {
        CurrentFrameIndex = 0;
        _elapsed = 0f;
        IsFinished = false;
    }

    /// <summary>
    /// Switches to <paramref name="animation"/>. By default this resets to frame 0; pass
    /// <paramref name="preservePhase"/> to carry the current frame index and sub-frame time across
    /// the swap (keeps a walk cycle smooth when only the facing direction changes). The preserved
    /// index is clamped to the new animation's frame count.
    /// </summary>
    public void Play(SpriteAnimation animation, bool preservePhase = false)
    {
        ArgumentNullException.ThrowIfNull(animation);
        if (preservePhase)
        {
            Animation = animation;
            CurrentFrameIndex = Math.Min(CurrentFrameIndex, animation.FrameCount - 1);
            IsFinished = false;
        }
        else
        {
            Animation = animation;
            Reset();
        }
    }
}
