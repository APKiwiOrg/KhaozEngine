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
    public List<string> Calls { get; } = [];

    public void FillRounded(Vector4 rect, Color color, float cornerRadius) =>
        Calls.Add(string.Create(CultureInfo.InvariantCulture,
            $"fill {rect.X:F3} {rect.Y:F3} {rect.Z:F3} {rect.W:F3} {color.R:F3} {color.G:F3} {color.B:F3} {color.A:F3} {cornerRadius:F3}"));

    public void StrokeRounded(Vector4 rect, Color color, float cornerRadius, float thickness) =>
        Calls.Add(string.Create(CultureInfo.InvariantCulture,
            $"stroke {rect.X:F3} {rect.Y:F3} {rect.Z:F3} {rect.W:F3} {color.R:F3} {color.G:F3} {color.B:F3} {color.A:F3} {cornerRadius:F3} {thickness:F3}"));

    public void Text(string text, Vector2 position, Color color, float scale) =>
        Calls.Add(string.Create(CultureInfo.InvariantCulture,
            $"text {JsonSerializer.Serialize(text)} {position.X:F3} {position.Y:F3} {color.R:F3} {color.G:F3} {color.B:F3} {color.A:F3} {scale:F3}"));

    public void OutlinedText(string text, Vector2 position, Color color, Color outline, float thickness, float scale) =>
        Calls.Add(string.Create(CultureInfo.InvariantCulture,
            $"outlined {JsonSerializer.Serialize(text)} {position.X:F3} {position.Y:F3} {color.R:F3} {color.G:F3} {color.B:F3} {color.A:F3} {outline.R:F3} {outline.G:F3} {outline.B:F3} {outline.A:F3} {thickness:F3} {scale:F3}"));
}
