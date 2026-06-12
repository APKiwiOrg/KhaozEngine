using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.Sprites;

/// <summary>
/// A sprite that holds one <see cref="SpriteAnimation"/> per <see cref="Direction8"/> and plays the
/// one matching its current facing. Update it with a facing vector and a time delta, then draw it at
/// a world position. Origin defaults to the centre of the current frame. Switching facing preserves
/// the animation phase, so a walk cycle stays smooth as the direction turns.
/// </summary>
public sealed class DirectionalAnimatedSprite
{
    private readonly IReadOnlyDictionary<Direction8, SpriteAnimation> _animations;
    private readonly SpriteAnimationPlayer _player;

    /// <summary>The facing currently being played.</summary>
    public Direction8 CurrentDirection { get; private set; }

    /// <summary>The frame currently shown for the current facing.</summary>
    public SpriteFrame CurrentFrame => _player.CurrentFrame;

    /// <summary>Index of the current frame within the current direction's animation.</summary>
    public int CurrentFrameIndex => _player.CurrentFrameIndex;

    /// <summary>
    /// Composes a directional sprite from one animation per direction. All eight
    /// <see cref="Direction8"/> values must be present.
    /// </summary>
    public DirectionalAnimatedSprite(IReadOnlyDictionary<Direction8, SpriteAnimation> animations, Direction8 initial = Direction8.S)
    {
        ArgumentNullException.ThrowIfNull(animations);
        var missing = Enum.GetValues<Direction8>().Where(d => !animations.ContainsKey(d)).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"Missing animations for directions: {string.Join(", ", missing)}", nameof(animations));

        _animations = animations;
        CurrentDirection = initial;
        _player = new SpriteAnimationPlayer(animations[initial]);
    }

    /// <summary>Sets the facing to <paramref name="direction"/>, preserving animation phase if it changed.</summary>
    public void SetDirection(Direction8 direction)
    {
        if (direction == CurrentDirection)
            return;
        CurrentDirection = direction;
        _player.Play(_animations[direction], preservePhase: true);
    }

    /// <summary>Sets the facing from a movement/aim vector via <see cref="Direction8Extensions.FromVector"/>.</summary>
    public void SetFacing(Vector2 facing) => SetDirection(Direction8Extensions.FromVector(facing, CurrentDirection));

    /// <summary>Advances the current animation by <paramref name="deltaSeconds"/>.</summary>
    public void Update(float deltaSeconds) => _player.Update(deltaSeconds);

    /// <summary>Advances the current animation by the elapsed time in <paramref name="gameTime"/>.</summary>
    public void Update(GameTime gameTime) => _player.Update(gameTime);

    /// <summary>Sets the facing from <paramref name="facing"/>, then advances the animation.</summary>
    public void Update(Vector2 facing, GameTime gameTime)
    {
        SetFacing(facing);
        _player.Update(gameTime);
    }

    /// <summary>Sets the facing from <paramref name="facing"/>, then advances by <paramref name="deltaSeconds"/>.</summary>
    public void Update(Vector2 facing, float deltaSeconds)
    {
        SetFacing(facing);
        _player.Update(deltaSeconds);
    }

    /// <summary>
    /// Draws the current frame at <paramref name="position"/> via <paramref name="spriteBatch"/>
    /// (which must be within Begin/End). Origin defaults to the centre of the frame, so
    /// <paramref name="position"/> is where the sprite is centred.
    /// </summary>
    public void Draw(
        SpriteBatch spriteBatch,
        Vector2 position,
        float scale = 1f,
        float rotation = 0f,
        Color? tint = null,
        Vector2? origin = null,
        SpriteEffects effects = SpriteEffects.None,
        float layerDepth = 0f)
    {
        SpriteFrame frame = _player.CurrentFrame;
        Vector2 frameOrigin = origin ?? new Vector2(frame.Source.Width / 2f, frame.Source.Height / 2f);
        spriteBatch.Draw(frame.Texture, position, frame.Source, tint ?? Color.White, rotation, frameOrigin, scale, effects, layerDepth);
    }
}
