using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Sprites;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class SpriteAnimationPlayerTests
{
    // Frames carry a (Texture2D?, Rectangle); timing logic never touches the texture, so headless
    // tests build frames with a null texture and a marker rectangle (x = frame index).
    private static SpriteAnimation Anim(int frameCount, float frameDuration, bool loop)
    {
        var frames = Enumerable.Range(0, frameCount)
            .Select(i => new SpriteFrame(null, new Rectangle(i, 0, 1, 1)))
            .ToList();
        return new SpriteAnimation(frames, frameDuration, loop);
    }

    [Fact]
    public void New_player_starts_on_first_frame()
    {
        var player = new SpriteAnimationPlayer(Anim(3, 0.1f, loop: true));
        Assert.Equal(0, player.CurrentFrameIndex);
        Assert.False(player.IsFinished);
    }

    [Fact]
    public void Update_below_frame_duration_holds_current_frame()
    {
        var player = new SpriteAnimationPlayer(Anim(3, 0.1f, loop: true));
        player.Update(0.05f);
        Assert.Equal(0, player.CurrentFrameIndex);
    }

    [Fact]
    public void Update_at_frame_duration_advances_one_frame()
    {
        var player = new SpriteAnimationPlayer(Anim(3, 0.1f, loop: true));
        player.Update(0.1f);
        Assert.Equal(1, player.CurrentFrameIndex);
    }

    [Fact]
    public void Looping_wraps_back_to_start()
    {
        var player = new SpriteAnimationPlayer(Anim(3, 0.1f, loop: true));
        player.Update(0.3f); // 3 frames -> wraps to 0
        Assert.Equal(0, player.CurrentFrameIndex);
        Assert.False(player.IsFinished);
    }

    [Fact]
    public void Looping_handles_large_delta_with_modulo()
    {
        var player = new SpriteAnimationPlayer(Anim(3, 0.1f, loop: true));
        player.Update(0.7f); // 7 frames over a 3-frame loop -> index 1
        Assert.Equal(1, player.CurrentFrameIndex);
    }

    [Fact]
    public void Non_looping_clamps_on_last_frame_and_finishes()
    {
        var player = new SpriteAnimationPlayer(Anim(3, 0.1f, loop: false));
        player.Update(0.5f); // would be frame 5, clamps at 2
        Assert.Equal(2, player.CurrentFrameIndex);
        Assert.True(player.IsFinished);
    }

    [Fact]
    public void Reset_returns_to_first_frame_and_clears_finished()
    {
        var player = new SpriteAnimationPlayer(Anim(3, 0.1f, loop: false));
        player.Update(1f);
        Assert.True(player.IsFinished);

        player.Reset();
        Assert.Equal(0, player.CurrentFrameIndex);
        Assert.False(player.IsFinished);
    }

    [Fact]
    public void CurrentFrame_tracks_index()
    {
        var player = new SpriteAnimationPlayer(Anim(3, 0.1f, loop: true));
        player.Update(0.2f);
        Assert.Equal(2, player.CurrentFrameIndex);
        Assert.Equal(new Rectangle(2, 0, 1, 1), player.CurrentFrame.Source);
    }

    [Fact]
    public void Update_with_GameTime_advances_like_float_delta()
    {
        var player = new SpriteAnimationPlayer(Anim(3, 0.1f, loop: true));
        player.Update(new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(0.1)));
        Assert.Equal(1, player.CurrentFrameIndex);
    }

    [Fact]
    public void FromFps_sets_frame_duration_to_inverse_of_fps()
    {
        var anim = SpriteAnimation.FromFps(
            new List<SpriteFrame> { new(null, Rectangle.Empty), new(null, Rectangle.Empty) },
            fps: 10f, loop: true);
        Assert.Equal(0.1f, anim.FrameDuration, precision: 5);
    }

    [Fact]
    public void Play_rebinds_animation_resetting_phase_by_default()
    {
        var player = new SpriteAnimationPlayer(Anim(3, 0.1f, loop: true));
        player.Update(0.2f);
        Assert.Equal(2, player.CurrentFrameIndex);

        player.Play(Anim(4, 0.1f, loop: true));
        Assert.Equal(0, player.CurrentFrameIndex);
    }

    [Fact]
    public void Play_can_preserve_phase_across_a_swap()
    {
        var player = new SpriteAnimationPlayer(Anim(4, 0.1f, loop: true));
        player.Update(0.2f);
        Assert.Equal(2, player.CurrentFrameIndex);

        player.Play(Anim(4, 0.1f, loop: true), preservePhase: true);
        Assert.Equal(2, player.CurrentFrameIndex);
    }

    [Fact]
    public void SpriteAnimation_rejects_empty_frame_list()
    {
        Assert.Throws<ArgumentException>(() => new SpriteAnimation(new List<SpriteFrame>(), 0.1f, true));
    }

    [Fact]
    public void SpriteAnimation_rejects_non_positive_frame_duration()
    {
        var frames = new List<SpriteFrame> { new(null, Rectangle.Empty) };
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpriteAnimation(frames, 0f, true));
    }
}
