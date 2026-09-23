using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui.Chat;

/// <summary>
/// Where <see cref="ChatBox"/> sends each run of history text. The frame draw passes
/// <see cref="SpriteBatchChatRowSink"/>. Tests pass a recording or counting sink, because a
/// <see cref="SpriteBatch"/> needs a GPU device and which rows a frame draws is the thing they prove.
/// </summary>
internal interface IChatRowSink
{
    /// <summary>Draw <paramref name="text"/> with its top-left at <paramref name="position"/>.</summary>
    void DrawText(string text, Vector2 position, Color color);
}

/// <summary>The sink the frame draw uses, a pass-through to <c>SpriteBatch.DrawString</c>. A struct, so the
/// generic row loop runs without boxing it.</summary>
internal readonly struct SpriteBatchChatRowSink : IChatRowSink
{
    readonly SpriteBatch _batch;
    readonly SpriteFont _font;

    internal SpriteBatchChatRowSink(SpriteBatch batch, SpriteFont font)
    {
        _batch = batch;
        _font = font;
    }

    public void DrawText(string text, Vector2 position, Color color) => _batch.DrawString(_font, text, position, color);
}
