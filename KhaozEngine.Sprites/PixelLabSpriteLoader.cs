using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.Sprites;

/// <summary>
/// Builds a <see cref="DirectionalAnimatedSprite"/> from a PixelLab export. PixelLab does not emit a
/// canonical sprite sheet (it exports loose per-frame PNGs), so this loader covers the two shapes a
/// consuming pipeline ends up with: an assembled grid sheet (8 direction rows x N frame columns) via
/// <see cref="FromGridSheet"/>, or loose per-direction frame textures via <see cref="FromFrames"/>.
/// PixelLab's directional row order (S, SE, E, NE, N, NW, W, SW) is the one PixelLab-specific
/// assumption and lives only here, in <see cref="RowFor"/>.
/// </summary>
public static class PixelLabSpriteLoader
{
    /// <summary>
    /// The sheet row index for a direction in a PixelLab grid export. PixelLab orders rows
    /// S, SE, E, NE, N, NW, W, SW, which is exactly the <see cref="Direction8"/> integer order.
    /// </summary>
    public static int RowFor(Direction8 direction) => (int)direction;

    /// <summary>
    /// Builds a sprite from an assembled grid sheet whose rows are the 8 directions (PixelLab order)
    /// and whose columns are the <paramref name="frameCount"/> animation frames. Frame rate is given
    /// in frames per second.
    /// </summary>
    public static DirectionalAnimatedSprite FromGridSheet(Texture2D sheet, int frameCount, float fps, bool loop = true)
    {
        if (fps <= 0f) throw new ArgumentOutOfRangeException(nameof(fps), "FPS must be positive.");
        return FromGridSheetFrameDuration(sheet, frameCount, 1f / fps, loop);
    }

    /// <summary>
    /// Builds a sprite from an assembled grid sheet (8 direction rows x <paramref name="frameCount"/>
    /// frame columns, PixelLab row order), with an explicit per-frame duration in seconds.
    /// </summary>
    public static DirectionalAnimatedSprite FromGridSheetFrameDuration(Texture2D sheet, int frameCount, float frameDuration, bool loop = true)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));

        var spriteSheet = SpriteSheet.FromGrid(sheet, rows: 8, columns: frameCount);
        var animations = new Dictionary<Direction8, SpriteAnimation>();
        foreach (Direction8 direction in Enum.GetValues<Direction8>())
        {
            int row = RowFor(direction);
            var frames = Enumerable.Range(0, frameCount)
                .Select(col => spriteSheet.Frame(row, col))
                .ToList();
            animations[direction] = new SpriteAnimation(frames, frameDuration, loop);
        }
        return new DirectionalAnimatedSprite(animations);
    }

    /// <summary>
    /// Builds a sprite from loose per-direction frame textures (PixelLab's native export shape, once
    /// each frame PNG is loaded as a <see cref="Texture2D"/>). All eight directions must be supplied,
    /// each with at least one frame. Frame rate is given in frames per second.
    /// </summary>
    public static DirectionalAnimatedSprite FromFrames(
        IReadOnlyDictionary<Direction8, IReadOnlyList<Texture2D>> framesByDirection,
        float fps,
        bool loop = true)
    {
        ArgumentNullException.ThrowIfNull(framesByDirection);
        if (fps <= 0f) throw new ArgumentOutOfRangeException(nameof(fps), "FPS must be positive.");

        var animations = new Dictionary<Direction8, SpriteAnimation>();
        foreach (Direction8 direction in Enum.GetValues<Direction8>())
        {
            if (!framesByDirection.TryGetValue(direction, out var textures) || textures.Count == 0)
                throw new ArgumentException($"Missing frames for direction {direction}.", nameof(framesByDirection));
            var frames = textures.Select(SpriteFrame.Whole).ToList();
            animations[direction] = SpriteAnimation.FromFps(frames, fps, loop);
        }
        return new DirectionalAnimatedSprite(animations);
    }
}
