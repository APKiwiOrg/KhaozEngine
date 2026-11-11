using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Render3D;

internal interface INameplateCanvas
{
    ITextMeasurer Font { get; }

    /// <summary>Device pixels per draw unit for the active pass, or zero when the pass does not snap to device
    /// pixels. Pairs with <see cref="DeviceOffset"/>.</summary>
    Vector2 DeviceScale { get; }

    /// <summary>The device-pixel offset of the draw origin for the active pass.</summary>
    Vector2 DeviceOffset { get; }

    void FillRounded(Vector4 rect, Color color, float cornerRadius);
    void StrokeRounded(Vector4 rect, Color color, float cornerRadius, float thickness);
    void Text(string text, Vector2 position, Color color, float scale);
    void OutlinedText(string text, Vector2 position, Color color, Color outline, float thickness, float scale);
}

internal readonly struct SpriteBatchNameplateCanvas(SpriteBatch batch, SpriteFont font, Texture2D white) : INameplateCanvas
{
    public ITextMeasurer Font => font;
    public Vector2 DeviceScale => batch.DeviceScale;
    public Vector2 DeviceOffset => batch.DeviceOffset;

    public void FillRounded(Vector4 rect, Color color, float cornerRadius) =>
        batch.DrawRounded(white, rect, color, cornerRadius);

    public void StrokeRounded(Vector4 rect, Color color, float cornerRadius, float thickness) =>
        batch.DrawRounded(white, rect, color, cornerRadius, 0f, thickness);

    public void Text(string text, Vector2 position, Color color, float scale) =>
        batch.DrawString(font, text, position, color, scale);

    public void OutlinedText(string text, Vector2 position, Color color, Color outline, float thickness, float scale) =>
        batch.DrawStringOutlined(font, text, position, color, outline, thickness, scale);
}
