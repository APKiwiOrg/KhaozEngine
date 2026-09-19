using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>How a <see cref="TabBar"/> paints one tab.</summary>
    public enum TabBarDrawMode
    {
        /// <summary>The shipped draw: every tab is a <see cref="GuiDraw.DrawButton"/> in the bar's
        /// <see cref="TabBar.ActiveStyle"/> or <see cref="TabBar.InactiveStyle"/>.</summary>
        Button,

        /// <summary>A flat fill, a one-unit border and a centred label, coloured from
        /// <see cref="TabBar.FlatTheme"/>. What a side panel's separated tabs want.</summary>
        Flat,
    }

    public sealed partial class TabBar
    {
        /// <summary>
        /// How each tab is painted. Defaults to <see cref="TabBarDrawMode.Button"/>, the shipped draw, so no
        /// existing bar moves a pixel. <see cref="TabBarDrawMode.Flat"/> paints a flat fill, a one-unit border and
        /// a centred label scaled by <see cref="TextScale"/> and truncated to the tab, which is the look a strip
        /// of separated tabs over a panel body wants.
        /// </summary>
        /// <remarks>
        /// Read by the fixed-size layout only. The even-split layout is already a flat segmented control and its
        /// tabs abut with no gutter, so a per-tab border there would draw every interior seam twice, which is the
        /// doubling its single shared border grid exists to avoid.
        /// </remarks>
        public TabBarDrawMode DrawMode = TabBarDrawMode.Button;

        /// <summary>
        /// The palette <see cref="TabBarDrawMode.Flat"/> draws from, captured from <see cref="GuiTheme.Default"/>
        /// at construction the way every other widget captures its colours, so setting the ambient theme once at
        /// startup rebrands this strip with the rest of the UI. It carries the two tab colours the mode is built
        /// on, <see cref="GuiTheme.TabFill"/> and <see cref="GuiTheme.TabActiveFill"/>, and each derives from the
        /// palette a game did set when it named neither. Ignored by <see cref="TabBarDrawMode.Button"/>, which
        /// draws from <see cref="ActiveStyle"/> and <see cref="InactiveStyle"/>.
        /// </summary>
        public GuiTheme FlatTheme = GuiTheme.Default;

        // Whether Draw takes the flat path this frame. The even-split layout never does, whatever the mode says,
        // so the rule is in one place rather than read off DrawMode at the call site.
        internal bool DrawsFlat => UsesFixedLayout && DrawMode == TabBarDrawMode.Flat;

        // How much of a tab's width is kept clear of its label, so a long one ellipsises inside the border rather
        // than up against it. Three units a side at the shipped tab sizes.
        const float FlatLabelPad = 6f;

        // One cached measure delegate per font, so a per-frame draw of ten tabs allocates no closures. Rebuilt
        // only when Font itself changes, which is a caller assignment rather than a per-frame event.
        Func<string, float>? _measureWidth;
        SpriteFont? _measuredFont;

        Func<string, float> MeasureWidth(SpriteFont font)
        {
            if (!ReferenceEquals(_measuredFont, font) || _measureWidth is null)
            {
                _measuredFont = font;
                _measureWidth = s => font.Measure(s).X;
            }
            return _measureWidth;
        }

        void DrawFlat(SpriteBatch batch, Texture2D white, SpriteFont font)
        {
            Func<string, float> measure = MeasureWidth(font);
            for (int i = 0; i < _items.Length; i++)
            {
                Rect rect = TabRect(i);
                (Vector4 fill, Vector4 border, Vector4 text) = ResolveFlatVisual(i);
                GuiDraw.Fill(batch, white, rect, fill);
                GuiDraw.Border(batch, white, rect, 1f, border);
                var (label, at) = FlatLabel(rect, _labels[i].Resolve(), measure, font.LineHeight, TextScale);
                if (label.Length > 0) batch.DrawString(font, label, at, (Color)text, TextScale);
            }
        }

        // The three colours one flat tab draws in, already faded by Opacity. Split out of the draw so the choice
        // is headless-testable: a SpriteBatch needs a GPU device, and which colour a state picks is the half of
        // the draw a consumer can get wrong.
        internal (Vector4 Fill, Vector4 Border, Vector4 Text) ResolveFlatVisual(int index)
        {
            if (index < 0 || index >= _items.Length) throw new ArgumentOutOfRangeException(nameof(index));
            GuiTheme t = FlatTheme;
            bool active = index == _activeIndex;
            bool enabled = _items[index].Enabled;
            Vector4 fill = !enabled ? t.SurfaceDisabled
                : active ? t.TabActiveFill
                : _pressIndex == index ? t.SurfacePress
                : _hoverIndex == index ? t.SurfaceHover
                : t.TabFill;
            Vector4 border = active ? t.BorderHover : enabled ? t.BorderShadow : t.BorderDisabled;
            Vector4 text = active ? t.Text : enabled ? t.TextMuted : t.TextDisabled;
            return (Fade(fill), Fade(border), Fade(text));
        }

        Vector4 Fade(Vector4 color) => new(color.X, color.Y, color.Z, color.W * Opacity);

        // The label one flat tab draws, fitted to the tab and centred in it. Pure given a measure function, so it
        // is headless-testable without a font: the budget is measured in UNSCALED units, because the caller draws
        // the fitted string at `scale` and truncating against the scaled width would fit a different string.
        internal static (string Text, Vector2 At) FlatLabel(in Rect tab, string text,
            Func<string, float> measureWidth, float lineHeight, float scale)
        {
            if (string.IsNullOrEmpty(text)) return (string.Empty, new Vector2(tab.X, tab.Y));
            float safeScale = scale > 0f ? scale : 1f;
            float budget = MathF.Max(0f, tab.Width - FlatLabelPad) / safeScale;
            string fitted = GuiDraw.TruncateWithEllipsis(text, budget, measureWidth);
            var measured = new Vector2(measureWidth(fitted), 0f);
            return (fitted, GuiDraw.AlignedTextPos(tab, measured, lineHeight, GuiAlign.Center, scale));
        }
    }
}
