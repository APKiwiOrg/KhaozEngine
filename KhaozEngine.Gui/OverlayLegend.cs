using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui
{
    /// <summary>One legend row: a color swatch paired with its label (e.g. a collision-shape kind and its
    /// overlay tint).</summary>
    public readonly record struct LegendEntry(Color Swatch, string Label);

    /// <summary>
    /// A domain-agnostic color-swatch + label panel for debug overlays. Any caller (collision shapes, navmesh
    /// regions, AI states, ...) feeds a list of <see cref="LegendEntry"/> in via <see cref="SetEntries"/> and
    /// draws the panel with <see cref="Draw(SpriteBatch,SpriteFont,Texture2D,Rect)"/> (corner-anchored) or
    /// <see cref="Draw(SpriteBatch,SpriteFont,Texture2D,Vector2)"/> (placed at an explicit top-left, e.g. beside
    /// another panel). Look and layout come from <see cref="Theme"/>; <see cref="OverlayLegendTheme.FromDiagnostics"/>
    /// makes the legend match an adjacent <see cref="DiagnosticsOverlay"/>. Unlike <see cref="DiagnosticsOverlay"/>
    /// this widget has no <c>Visible</c>/fade state of its own: it snaps on/off because the caller only calls
    /// <c>Draw</c> while its own overlay is on. After a draw, <see cref="Bounds"/> holds the panel rect so a
    /// caller can chain another panel off its edge. Headless-testable: <see cref="SetEntries"/>, the empty-legend
    /// path of <see cref="Measure"/>, and <see cref="OverlayLegendTheme.Anchor"/> need no GPU.
    /// </summary>
    public sealed class OverlayLegend
    {
        IReadOnlyList<LegendEntry> _entries = Array.Empty<LegendEntry>();

        /// <summary>Look and layout for the panel. Never null; defaults to <see cref="OverlayLegendTheme.Default"/>.</summary>
        public OverlayLegendTheme Theme { get; set; }

        /// <summary>The panel rect of the most recent <c>Draw</c> (empty <see cref="Rect"/> when the last draw was
        /// a no-op, i.e. no entries). Lets a caller place an adjacent panel off <see cref="Rect.Right"/> etc.</summary>
        public Rect Bounds { get; private set; }

        public OverlayLegend(OverlayLegendTheme? theme = null) => Theme = theme ?? OverlayLegendTheme.Default;

        /// <summary>Number of rows currently set.</summary>
        public int EntryCount => _entries.Count;

        /// <summary>Set the rows drawn next <c>Draw</c>. The reference is stored as-is (no copy), so a
        /// caller may reuse its own buffer between frames. <c>null</c> resets to empty.</summary>
        public void SetEntries(IReadOnlyList<LegendEntry> entries) => _entries = entries ?? Array.Empty<LegendEntry>();

        /// <summary>The panel size at the origin (0,0). Empty when there are no entries, so the caller can
        /// skip drawing without ever touching <paramref name="font"/>.</summary>
        public Rect Measure(SpriteFont font)
        {
            if (_entries.Count == 0) return new Rect(0, 0, 0, 0);

            float scale = Theme.TextScale;
            float rowH = MathF.Max(Theme.SwatchSize, font.LineHeight * scale);
            float w = 0f;
            for (int i = 0; i < _entries.Count; i++)
                w = MathF.Max(w, Theme.SwatchSize + Theme.SwatchGap + font.Measure(_entries[i].Label).X * scale);

            float h = _entries.Count * rowH + (_entries.Count - 1) * Theme.RowSpacing;
            return new Rect(0, 0, w + Theme.Padding * 2f, h + Theme.Padding * 2f);
        }

        /// <summary>Draw the panel anchored to <see cref="OverlayLegendTheme.Corner"/> of <paramref name="viewport"/>.
        /// No-op when empty.</summary>
        public void Draw(SpriteBatch batch, SpriteFont font, Texture2D white, Rect viewport)
        {
            if (_entries.Count == 0) { Bounds = default; return; }
            Rect size = Measure(font);
            DrawAt(batch, font, white, Theme.Anchor(viewport, size.Width, size.Height), size);
        }

        /// <summary>Draw the panel with its top-left at <paramref name="topLeft"/> (ignores the theme corner/margin).
        /// Use to place the legend beside another panel, e.g. off a <see cref="DiagnosticsOverlay.Bounds"/> edge.
        /// No-op when empty.</summary>
        public void Draw(SpriteBatch batch, SpriteFont font, Texture2D white, Vector2 topLeft)
        {
            if (_entries.Count == 0) { Bounds = default; return; }
            DrawAt(batch, font, white, topLeft, Measure(font));
        }

        void DrawAt(SpriteBatch batch, SpriteFont font, Texture2D white, Vector2 topLeft, Rect size)
        {
            var panel = new Rect(topLeft.X, topLeft.Y, size.Width, size.Height);
            GuiDraw.Fill(batch, white, panel, Theme.PanelFill);
            GuiDraw.Border(batch, white, panel, Theme.BorderThickness, Theme.BorderColor);

            float scale = Theme.TextScale;
            float rowH = MathF.Max(Theme.SwatchSize, font.LineHeight * scale);
            float x = panel.X + Theme.Padding;
            float y = panel.Y + Theme.Padding;
            for (int i = 0; i < _entries.Count; i++)
            {
                LegendEntry e = _entries[i];
                GuiDraw.Fill(batch, white, new Rect(x, y + (rowH - Theme.SwatchSize) * 0.5f, Theme.SwatchSize, Theme.SwatchSize), e.Swatch.ToVector4());
                batch.DrawString(font, e.Label, new Vector2(x + Theme.SwatchSize + Theme.SwatchGap, y + (rowH - font.LineHeight * scale) * 0.5f),
                    (Color)Theme.LabelText, scale);
                y += rowH + Theme.RowSpacing;
            }

            Bounds = panel;
        }
    }
}
