using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Render3D;

internal interface INameplateCanvas
{
    ITextMeasurer Font { get; }
    void FillRounded(Vector4 rect, Color color, float cornerRadius);
    void StrokeRounded(Vector4 rect, Color color, float cornerRadius, float thickness);
    void Text(string text, Vector2 position, Color color, float scale);
    void OutlinedText(string text, Vector2 position, Color color, Color outline, float thickness, float scale);
}

internal readonly struct SpriteBatchNameplateCanvas(SpriteBatch batch, SpriteFont font, Texture2D white) : INameplateCanvas
{
    public ITextMeasurer Font => font;

    public void FillRounded(Vector4 rect, Color color, float cornerRadius) =>
        batch.DrawRounded(white, rect, color, cornerRadius);

    public void StrokeRounded(Vector4 rect, Color color, float cornerRadius, float thickness) =>
        batch.DrawRounded(white, rect, color, cornerRadius, 0f, thickness);

    public void Text(string text, Vector2 position, Color color, float scale) =>
        batch.DrawString(font, text, position, color, scale);

    public void OutlinedText(string text, Vector2 position, Color color, Color outline, float thickness, float scale) =>
        batch.DrawStringOutlined(font, text, position, color, outline, thickness, scale);
}
