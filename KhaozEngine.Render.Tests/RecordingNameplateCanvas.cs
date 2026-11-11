using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests;

internal sealed class RecordingNameplateCanvas(ITextMeasurer font) : INameplateCanvas
{
    public ITextMeasurer Font { get; } = font;

    /// <summary>Zero, the default, is a pass that does not snap. A positive scale records a point-space UI pass.
    /// </summary>
    public Vector2 DeviceScale { get; init; }
    public Vector2 DeviceOffset { get; init; }
    public List<string> Calls { get; } = [];

    /// <summary>Every filled rect, in call order.</summary>
    public List<Vector4> Fills { get; } = [];

    /// <summary>Every stroked rect with its thickness, in call order.</summary>
    public List<(Vector4 Rect, float Thickness)> Strokes { get; } = [];

    public void FillRounded(Vector4 rect, Color color, float cornerRadius)
    {
        Fills.Add(rect);
        Calls.Add(string.Create(CultureInfo.InvariantCulture,
            $"fill {rect.X:F3} {rect.Y:F3} {rect.Z:F3} {rect.W:F3} {color.R:F3} {color.G:F3} {color.B:F3} {color.A:F3} {cornerRadius:F3}"));
    }

    public void StrokeRounded(Vector4 rect, Color color, float cornerRadius, float thickness)
    {
        Strokes.Add((rect, thickness));
        Calls.Add(string.Create(CultureInfo.InvariantCulture,
            $"stroke {rect.X:F3} {rect.Y:F3} {rect.Z:F3} {rect.W:F3} {color.R:F3} {color.G:F3} {color.B:F3} {color.A:F3} {cornerRadius:F3} {thickness:F3}"));
    }

    public void Text(string text, Vector2 position, Color color, float scale) =>
        Calls.Add(string.Create(CultureInfo.InvariantCulture,
            $"text {JsonSerializer.Serialize(text)} {position.X:F3} {position.Y:F3} {color.R:F3} {color.G:F3} {color.B:F3} {color.A:F3} {scale:F3}"));

    public void OutlinedText(string text, Vector2 position, Color color, Color outline, float thickness, float scale) =>
        Calls.Add(string.Create(CultureInfo.InvariantCulture,
            $"outlined {JsonSerializer.Serialize(text)} {position.X:F3} {position.Y:F3} {color.R:F3} {color.G:F3} {color.B:F3} {color.A:F3} {outline.R:F3} {outline.G:F3} {outline.B:F3} {outline.A:F3} {thickness:F3} {scale:F3}"));
}
