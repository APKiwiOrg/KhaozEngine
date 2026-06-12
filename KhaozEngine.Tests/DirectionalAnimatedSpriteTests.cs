using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Sprites;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class DirectionalAnimatedSpriteTests
{
    // One animation per direction; each frame's source rectangle encodes (x = direction row, y = frame col)
    // so tests can assert which direction/frame is current without a GraphicsDevice.
    private static IReadOnlyDictionary<Direction8, SpriteAnimation> EightDirections(int framesPerDir = 3)
    {
        var dict = new Dictionary<Direction8, SpriteAnimation>();
        foreach (Direction8 dir in Enum.GetValues<Direction8>())
        {
            var frames = Enumerable.Range(0, framesPerDir)
                .Select(col => new SpriteFrame(null, new Rectangle((int)dir, col, 1, 1)))
                .ToList();
            dict[dir] = new SpriteAnimation(frames, 0.1f, loop: true);
        }
        return dict;
    }

    [Fact]
    public void Defaults_to_south_and_first_frame()
    {
        var sprite = new DirectionalAnimatedSprite(EightDirections());
        Assert.Equal(Direction8.S, sprite.CurrentDirection);
        Assert.Equal(new Rectangle((int)Direction8.S, 0, 1, 1), sprite.CurrentFrame.Source);
    }

    [Fact]
    public void SetDirection_selects_that_directions_animation()
    {
        var sprite = new DirectionalAnimatedSprite(EightDirections());
        sprite.SetDirection(Direction8.E);
        Assert.Equal(Direction8.E, sprite.CurrentDirection);
        Assert.Equal((int)Direction8.E, sprite.CurrentFrame.Source.X);
    }

    [Fact]
    public void SetFacing_maps_vector_to_direction()
    {
        var sprite = new DirectionalAnimatedSprite(EightDirections());
        sprite.SetFacing(new Vector2(0f, -1f));
        Assert.Equal(Direction8.N, sprite.CurrentDirection);
    }

    [Fact]
    public void Update_advances_current_direction_animation()
    {
        var sprite = new DirectionalAnimatedSprite(EightDirections());
        sprite.Update(0.2f);
        Assert.Equal(2, sprite.CurrentFrame.Source.Y); // frame column 2
    }

    [Fact]
    public void Changing_direction_preserves_animation_phase()
    {
        var sprite = new DirectionalAnimatedSprite(EightDirections());
        sprite.Update(0.2f); // frame column 2 in South
        sprite.SetDirection(Direction8.W);

        Assert.Equal(Direction8.W, sprite.CurrentDirection);
        Assert.Equal((int)Direction8.W, sprite.CurrentFrame.Source.X); // now West row
        Assert.Equal(2, sprite.CurrentFrame.Source.Y);                 // same phase (column 2)
    }

    [Fact]
    public void Update_with_facing_and_gametime_sets_direction_and_advances()
    {
        var sprite = new DirectionalAnimatedSprite(EightDirections());
        sprite.Update(new Vector2(1f, 0f), new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(0.1)));
        Assert.Equal(Direction8.E, sprite.CurrentDirection);
        Assert.Equal(1, sprite.CurrentFrame.Source.Y); // advanced one frame
    }

    [Fact]
    public void Constructor_requires_all_eight_directions()
    {
        var partial = EightDirections().Where(kv => kv.Key != Direction8.NW)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Throws<ArgumentException>(() => new DirectionalAnimatedSprite(partial));
    }
}
