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
    /// draws the panel with <see cref="Draw"/>. Unlike <see cref="DiagnosticsOverlay"/> this widget has no
    /// <c>Visible</c>/fade state of its own: it snaps on/off because the caller only calls <see cref="Draw"/>
    /// while its own overlay is on. Headless-testable: <see cref="SetEntries"/> and the empty-legend path of
    /// <see cref="Measure"/> need no GPU.
    /// </summary>
    public sealed class OverlayLegend
    {
        const float Pad = 8f;
        const float SwatchSize = 14f;
        const float Gap = 8f;
        const float RowSpacing = 4f;

        IReadOnlyList<LegendEntry> _entries = Array.Empty<LegendEntry>();

        /// <summary>Number of rows currently set.</summary>
        public int EntryCount => _entries.Count;

        /// <summary>Set the rows drawn next <see cref="Draw"/>. The reference is stored as-is (no copy); a
        /// caller may reuse its own buffer between frames. <c>null</c> resets to empty.</summary>
        public void SetEntries(IReadOnlyList<LegendEntry> entries) => _entries = entries ?? Array.Empty<LegendEntry>();

        /// <summary>The panel size at the origin (0,0). Empty when there are no entries, so the caller can
        /// skip drawing without ever touching <paramref name="font"/>.</summary>
        public Rect Measure(SpriteFont font)
        {
            if (_entries.Count == 0) return new Rect(0, 0, 0, 0);

            float rowH = MathF.Max(SwatchSize, font.LineHeight);
            float w = 0f;
            for (int i = 0; i < _entries.Count; i++)
                w = MathF.Max(w, SwatchSize + Gap + font.Measure(_entries[i].Label).X);

            float h = _entries.Count * rowH + (_entries.Count - 1) * RowSpacing;
            return new Rect(0, 0, w + Pad * 2f, h + Pad * 2f);
        }

        /// <summary>Draw the panel anchored near the top-left of <paramref name="viewport"/>. No-op when empty.</summary>
        public void Draw(SpriteBatch batch, SpriteFont font, Texture2D white, Rect viewport)
        {
            if (_entries.Count == 0) return;

            Rect size = Measure(font);
            var panel = new Rect(viewport.X + 12f, viewport.Y + 12f, size.Width, size.Height);
            GuiDraw.Fill(batch, white, panel, new Vector4(0.05f, 0.06f, 0.09f, 0.75f));
            GuiDraw.Border(batch, white, panel, 1f, new Vector4(0.25f, 0.28f, 0.34f, 0.9f));

            float rowH = MathF.Max(SwatchSize, font.LineHeight);
            float x = panel.X + Pad;
            float y = panel.Y + Pad;
            for (int i = 0; i < _entries.Count; i++)
            {
                LegendEntry e = _entries[i];
                GuiDraw.Fill(batch, white, new Rect(x, y + (rowH - SwatchSize) * 0.5f, SwatchSize, SwatchSize), e.Swatch.ToVector4());
                batch.DrawString(font, e.Label, new Vector2(x + SwatchSize + Gap, y + (rowH - font.LineHeight) * 0.5f),
                    new Color(0.92f, 0.94f, 0.97f, 1f));
                y += rowH + RowSpacing;
            }
        }
    }
}
