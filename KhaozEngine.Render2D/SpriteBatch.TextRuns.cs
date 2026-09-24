using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D;

/// <summary>One resolved text run. Adjacent runs share a glyph pen and one snapped block origin.</summary>
public readonly record struct ColoredTextRun(string Text, Vector4 Color);

public sealed partial class SpriteBatch
{
    /// <summary>Draw adjacent coloured runs as one text block, snapping its origin once. A uniform
    /// <paramref name="opacity"/> changes alpha only and preserves each run's RGB colour.</summary>
    public void DrawStringRuns(SpriteFont font, ReadOnlySpan<ColoredTextRun> runs,
        Vector2 position, float scale = 1f, float opacity = 1f)
    {
        ArgumentNullException.ThrowIfNull(font);
        float k = font.RenderScale * scale;
        (float penX, float baseline) = SnapTextOrigin(position.X, position.Y + font.Ascent * scale);
        foreach (ColoredTextRun run in runs)
        {
            Vector4 rgba = run.Color;
            rgba.W *= opacity;
            DrawGlyphRun(font, run.Text ?? "", (Color)rgba, k, scale, ref penX, baseline);
        }
    }

    /// <summary>The glyph destinations the segmented draw would emit, for a parity test against one
    /// uniform string under the same point-space snapping.</summary>
    internal List<Vector4> DebugGlyphDestsRuns(SpriteFont font, ReadOnlySpan<ColoredTextRun> runs,
        Vector2 position, float scale)
    {
        float k = font.RenderScale * scale;
        (float penX, float baseline) = SnapTextOrigin(position.X, position.Y + font.Ascent * scale);
        var destinations = new List<Vector4>();
        foreach (ColoredTextRun run in runs)
            DrawGlyphRun(font, run.Text ?? "", default, k, scale, ref penX, baseline, destinations);
        return destinations;
    }

    // The same glyph loop serves uniform and segmented text. The pen survives every run, so a colour
    // boundary cannot snap an origin again or change the width measured from the concatenated text.
    void DrawGlyphRun(SpriteFont font, string text, Color color, float k, float scale,
        ref float penX, float baseline, List<Vector4>? destinations = null)
    {
        for (int i = 0; i < text.Length; i++)
        {
            GlyphInfo? g = SpriteFont.ResolveGlyph(font.Glyphs, text, ref i);
            if (g == null) continue;
            if (g.W > 0 && g.H > 0)
            {
                var dest = new Vector4(penX + g.XOff * k, baseline + g.YOff * k, g.W * k, g.H * k);
                if (destinations is not null) destinations.Add(dest);
                else
                {
                    var uv = new Vector4((float)g.Ax / font.AtlasW, (float)g.Ay / font.AtlasH,
                        (float)(g.Ax + g.W) / font.AtlasW, (float)(g.Ay + g.H) / font.AtlasH);
                    Draw(font.Atlas, dest, uv, color);
                }
            }
            penX += g.Advance * scale;
        }
    }
}
