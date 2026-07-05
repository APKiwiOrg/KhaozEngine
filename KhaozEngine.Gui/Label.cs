using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A non-interactive text widget: draws <see cref="Content"/> (resolved against the current locale) in
    /// <see cref="Font"/> aligned within <see cref="Bounds"/>, optionally word-wrapped. Pure presentation over
    /// the (tested) <see cref="TextLayout"/> helpers; the text re-resolves each <see cref="Draw"/>.
    /// </summary>
    public sealed class Label
    {
        public Rect Bounds;
        /// <summary>The (lazily resolved) label text.</summary>
        public LocalizedText Content;
        public SpriteFont Font;
        public Vector4 Color = Vector4.One;
        public TextAlign Align = TextAlign.Left;
        /// <summary>When true, the text word-wraps to <see cref="Bounds"/>.Width; otherwise it draws on one line.</summary>
        public bool Wrap;
        /// <summary>When true, a single (unwrapped) line is centered vertically within <see cref="Bounds"/>.</summary>
        public bool VerticalCenter = true;
        /// <summary>Uniform text scale applied about each line's top-left (glyphs, advances, line height, and the
        /// wrap width all scale together). Defaults to 1 (the unscaled path).</summary>
        public float Scale = 1f;

        /// <summary>Create a label from localized text.</summary>
        public Label(Rect bounds, LocalizedText text, SpriteFont font)
        {
            Bounds = bounds; Content = text; Font = font;
        }

        /// <summary>Obsolete: pass a <see cref="LocalizedText"/>. A raw string bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public Label(Rect bounds, string text, SpriteFont font)
            : this(bounds, LocalizedText.Raw(text), font) { }

        /// <summary>Obsolete shim for the former string field.</summary>
        [Obsolete("Use Content (LocalizedText). Setting Text stores a raw, non-localized value.")]
        [LocalizationExempt]
        public string Text
        {
            get => Content.Resolve();
            set => Content = LocalizedText.Raw(value);
        }

        /// <summary>The current resolved text (for tests / measurement).</summary>
        public string Resolved => Content.Resolve();

        /// <summary>Draw the label's text into <see cref="Bounds"/>.</summary>
        public void Draw(SpriteBatch batch)
        {
            string text = Content.Resolve();
            if (Wrap)
            {
                TextLayout.DrawWrapped(batch, Font, text, new Vector2(Bounds.X, Bounds.Y), Bounds.Width, Align, (KhaozEngine.Primitives.Color)Color, Scale);
                return;
            }
            float y = VerticalCenter ? Bounds.Y + (Bounds.Height - Font.LineHeight * Scale) * 0.5f : Bounds.Y;
            TextLayout.DrawAligned(batch, Font, text, Bounds.X, Bounds.Width, y, Align, (KhaozEngine.Primitives.Color)Color, Scale);
        }
    }
}
