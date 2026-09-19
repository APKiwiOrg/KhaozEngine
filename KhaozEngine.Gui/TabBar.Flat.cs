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
        /// <see cref="TabBar.FlatTheme"/>. What a side panel's separated tabs want. Read by the fixed-size layout
        /// only (a bar with a <see cref="TabBar.TabWidth"/>): an even-split bar keeps its own segmented draw
        /// whatever this says.</summary>
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
        /// <remarks>
        /// The mode reads COLOURS off this and nothing else. <see cref="GuiTheme.BorderThickness"/> and
        /// <see cref="GuiTheme.CornerRadius"/> are not read: a flat tab is a square fill under a one-unit border
        /// whatever the theme says, where the button draw honours both. So a strip switched from
        /// <see cref="TabBarDrawMode.Button"/> to <see cref="TabBarDrawMode.Flat"/> under a rounded theme loses
        /// its corners on purpose.
        /// </remarks>
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

        // The fitted label per tab, remembered against everything it was fitted to. Fitting a label that does not
        // fit binary-searches and builds a string per probe, and a strip of ten over-long labels redrawn every
        // frame would make thousands of short-lived strings a second for an answer that only moves when the
        // label, the tab width, the scale or the font does.
        string?[]? _fitSource;
        string[]? _fitText;
        float _fitWidth = float.NaN, _fitScale = float.NaN;
        SpriteFont? _fitFont;

        string FittedLabel(int index, string resolved, float tabWidth, SpriteFont font, Func<string, float> measure)
        {
            if (_fitSource is null || _fitText is null || _fitWidth != tabWidth || _fitScale != TextScale
                || !ReferenceEquals(_fitFont, font))
            {
                _fitSource = new string?[_items.Length];
                _fitText = new string[_items.Length];
                _fitWidth = tabWidth;
                _fitScale = TextScale;
                _fitFont = font;
            }
            if (!string.Equals(_fitSource[index], resolved, StringComparison.Ordinal))
            {
                _fitSource[index] = resolved;
                _fitText[index] = FitFlatLabel(tabWidth, resolved, measure, TextScale);
            }
            return _fitText[index];
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
                string label = FittedLabel(i, _labels[i].Resolve(), rect.Width, font, measure);
                if (label.Length == 0) continue;
                Vector2 at = FlatLabelOrigin(rect, label, measure, font.LineHeight, TextScale);
                batch.DrawString(font, label, at, (Color)text, TextScale);
            }
        }

        // The three colours one flat tab draws in, already faded by Opacity. Split out of the draw so the choice
        // is headless-testable: a SpriteBatch needs a GPU device, and which colour a state picks is the half of
        // the draw a consumer can get wrong.
        internal (Vector4 Fill, Vector4 Border, Vector4 Text) ResolveFlatVisual(int index)
        {
            if (index < 0 || index >= _items.Length) throw new ArgumentOutOfRangeException(nameof(index));
            GuiTheme t = FlatTheme;
            bool active = index == ActiveTab;
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
            string fitted = FitFlatLabel(tab.Width, text, measureWidth, scale);
            if (fitted.Length == 0) return (string.Empty, new Vector2(tab.X, tab.Y));
            return (fitted, FlatLabelOrigin(tab, fitted, measureWidth, lineHeight, scale));
        }

        // The fit alone, which is the half worth remembering between frames. A scale of zero or less reads as 1,
        // so an unset or nonsense TextScale fits the label at its natural size rather than dividing by zero.
        internal static string FitFlatLabel(float tabWidth, string text, Func<string, float> measureWidth, float scale)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            float safeScale = scale > 0f ? scale : 1f;
            float budget = MathF.Max(0f, tabWidth - FlatLabelPad) / safeScale;
            return GuiDraw.TruncateWithEllipsis(text, budget, measureWidth);
        }

        // Where an already fitted label starts so that it sits centred in the tab. Allocates nothing.
        internal static Vector2 FlatLabelOrigin(in Rect tab, string fitted, Func<string, float> measureWidth,
            float lineHeight, float scale)
        {
            var measured = new Vector2(measureWidth(fitted), 0f);
            return GuiDraw.AlignedTextPos(tab, measured, lineHeight, GuiAlign.Center, scale);
        }
    }
}
