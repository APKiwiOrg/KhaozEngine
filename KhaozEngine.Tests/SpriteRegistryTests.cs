using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Sprites;
using Xunit;

namespace KhaozEngine.Tests;

public class SpriteRegistryTests
{
    // Same encoding as DirectionalAnimatedSpriteTests: each frame's source rectangle holds
    // (x = direction row, y = frame col) so the current frame reveals the animation phase
    // without needing a GraphicsDevice.
    private static DirectionalAnimatedSprite MakeSprite(int framesPerDir = 3)
    {
        var dict = new Dictionary<Direction8, SpriteAnimation>();
        foreach (Direction8 dir in Enum.GetValues<Direction8>())
        {
            var frames = Enumerable.Range(0, framesPerDir)
                .Select(col => new SpriteFrame(null, new Microsoft.Xna.Framework.Rectangle((int)dir, col, 1, 1)))
                .ToList();
            dict[dir] = new SpriteAnimation(frames, 0.1f, loop: true);
        }
        return new DirectionalAnimatedSprite(dict);
    }

    [Fact]
    public void Get_returns_the_added_instance()
    {
        var registry = new SpriteRegistry();
        var sprite = MakeSprite();
        registry.Add("player", sprite);
        Assert.Same(sprite, registry.Get("player"));
    }

    [Fact]
    public void Get_returns_null_for_unknown_key()
    {
        var registry = new SpriteRegistry();
        Assert.Null(registry.Get("nope"));
    }

    [Fact]
    public void Update_advances_every_registered_sprite()
    {
        var registry = new SpriteRegistry();
        var a = MakeSprite();
        var b = MakeSprite();
        registry.Add("a", a);
        registry.Add("b", b);

        registry.Update(0.2f); // two 0.1s frames

        Assert.Equal(2, a.CurrentFrame.Source.Y); // both advanced to frame column 2
        Assert.Equal(2, b.CurrentFrame.Source.Y);
    }

    [Fact]
    public void Count_reflects_added_sprites()
    {
        var registry = new SpriteRegistry();
        Assert.Equal(0, registry.Count);
        registry.Add("a", MakeSprite());
        registry.Add("b", MakeSprite());
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Contains_reports_membership()
    {
        var registry = new SpriteRegistry();
        registry.Add("a", MakeSprite());
        Assert.True(registry.Contains("a"));
        Assert.False(registry.Contains("b"));
    }

    [Fact]
    public void Add_rejects_duplicate_key()
    {
        var registry = new SpriteRegistry();
        registry.Add("a", MakeSprite());
        Assert.Throws<ArgumentException>(() => registry.Add("a", MakeSprite()));
    }

    [Fact]
    public void Add_rejects_null_sprite()
    {
        var registry = new SpriteRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Add("a", null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Add_rejects_null_or_empty_key(string? key)
    {
        var registry = new SpriteRegistry();
        Assert.Throws<ArgumentException>(() => registry.Add(key!, MakeSprite()));
    }

    [Fact]
    public void Get_rejects_null_or_empty_key()
    {
        var registry = new SpriteRegistry();
        Assert.Throws<ArgumentException>(() => { registry.Get(""); });
    }
}
