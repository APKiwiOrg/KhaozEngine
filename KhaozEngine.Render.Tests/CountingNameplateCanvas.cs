using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests;

/// <summary>A non-allocating canvas that only counts calls, for warm allocation tests.</summary>
internal struct CountingNameplateCanvas(ITextMeasurer font) : INameplateCanvas
{
    public readonly ITextMeasurer Font => font;
    public int Calls { get; private set; }

    public void FillRounded(Vector4 rect, Color color, float cornerRadius) => Calls++;
    public void StrokeRounded(Vector4 rect, Color color, float cornerRadius, float thickness) => Calls++;
    public void Text(string text, Vector2 position, Color color, float scale) => Calls++;
    public void OutlinedText(string text, Vector2 position, Color color, Color outline, float thickness, float scale) =>
        Calls++;
}
