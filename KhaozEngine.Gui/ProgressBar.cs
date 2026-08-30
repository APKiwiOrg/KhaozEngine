using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Render2D;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>The edge a <see cref="ProgressBar"/> fills FROM: the accent grows out of that edge as
    /// <see cref="ProgressBar.Fraction"/> rises. <see cref="LeftToRight"/> is the default (today's look).</summary>
    public enum FillDirection
    {
        /// <summary>Fill grows from the left edge rightward (the default).</summary>
        LeftToRight,
        /// <summary>Fill grows from the right edge leftward.</summary>
        RightToLeft,
        /// <summary>Fill grows from the bottom edge upward (vertical bar).</summary>
        BottomToTop,
        /// <summary>Fill grows from the top edge downward (vertical bar).</summary>
        TopToBottom,
    }

    /// <summary>How a segmented <see cref="ProgressBar"/> paints its accent across the segments.</summary>
    public enum SegmentFillMode
    {
        /// <summary>Proportional fill: the continuous accent is clipped into each segment, so the separator gaps
        /// show as ticks over an otherwise smooth bar (xp / cast bars).</summary>
        Continuous,
        /// <summary>Discrete pips: a segment is painted only once the fill fully covers it (combo points,
        /// ability charges).</summary>
        Discrete,
    }

    /// <summary>
    /// A thin fill bar (health, XP, cast, load, charge pips): a themed track, an accent fill scaled by
    /// <see cref="Fraction"/> (always clamped 0..1) sitting inside the border frame, and an optional centered overlay
    /// label. The fill grows from the edge named by <see cref="FillDirection"/> (default <see cref="FillDirection.LeftToRight"/>,
    /// today's look). Set <see cref="SegmentCount"/> &gt; 1 to break the bar into segments: <see cref="SegmentFillMode.Continuous"/>
    /// keeps a proportional fill with separator gaps, <see cref="SegmentFillMode.Discrete"/> lights whole segments as
    /// pips. Segmentation composes with every <see cref="FillDirection"/> (a vertical segmented bar works). The accent
    /// fill is flat, matching <see cref="Slider"/>'s fill look. Corners and border come from <see cref="Style"/>.
    /// Non-interactive: there is no Update, so call <see cref="Draw"/> each frame.
    /// </summary>
    public sealed class ProgressBar
    {
        /// <summary>The full bar rect (keep the short axis small for a thin bar).</summary>
        public Rect Bounds;

        float _fraction;
        /// <summary>Fill amount, always clamped to 0..1 on assignment.</summary>
        public float Fraction
        {
            get => _fraction;
            set => _fraction = value < 0f ? 0f : value > 1f ? 1f : value;
        }

        /// <summary>Track (background) fill.</summary>
        public Vector4 TrackColor = GuiTheme.Default.Surface;
        /// <summary>Accent fill colour (overridable, defaults to the theme accent).</summary>
        public Vector4 FillColor = GuiTheme.Default.Accent;
        /// <summary>Border frame colour.</summary>
        public Vector4 BorderColor = GuiTheme.Default.Border;
        /// <summary>Overlay-text colour.</summary>
        public Vector4 OverlayTextColor = GuiTheme.Default.Text;

        /// <summary>Look knobs (corner radius / shadow / border thickness / skin) for the track + frame, defaulting to
        /// <see cref="GuiStyle.Default"/>.</summary>
        public GuiStyle Style = GuiStyle.Default;
        /// <summary>Uniform fade multiplied into every colour's alpha at draw time (1 = opaque).</summary>
        public float Opacity = 1f;

        /// <summary>
        /// Uniform scale for the centred <see cref="OverlayText"/>. Defaults to <c>1f</c> (today's rendering,
        /// byte-for-byte). Scales the TEXT only: <see cref="Bounds"/>, <see cref="InnerBounds"/>, the track, the
        /// accent fill and the segmentation are unchanged at any scale, so a thin readout bar can carry a small
        /// label. Mirrors <see cref="TabBar.TextScale"/>.
        /// </summary>
        public float OverlayTextScale = 1f;

        /// <summary>The edge the accent grows from. Default <see cref="FillDirection.LeftToRight"/> (today's look).</summary>
        public FillDirection FillDirection = FillDirection.LeftToRight;

        /// <summary>Number of segments the bar is split into. 0 or 1 (default) = a single continuous fill, unchanged.
        /// &gt; 1 breaks the inner track into that many equal segments (separated by <see cref="SegmentSpacing"/>)
        /// painted per <see cref="SegmentFillMode"/>.</summary>
        public int SegmentCount;
        /// <summary>Gap between segments in draw units (only used when <see cref="SegmentCount"/> &gt; 1). Default 2.</summary>
        public float SegmentSpacing = 2f;
        /// <summary>How the accent is painted across the segments (only used when <see cref="SegmentCount"/> &gt; 1).</summary>
        public SegmentFillMode SegmentFillMode = SegmentFillMode.Continuous;

        /// <summary>Create a bar with an initial <paramref name="fraction"/> (clamped 0..1).</summary>
        public ProgressBar(Rect bounds, float fraction = 0f)
        {
            Bounds = bounds;
            Fraction = fraction;
        }

        /// <summary>The area inside the frame, where the accent fill lives: <see cref="GuiStyle.ContentRect"/> of
        /// <see cref="Bounds"/>, so a flat bar insets by the border thickness (unchanged) and a skinned bar insets
        /// by the nine-slice frame's destination insets so the fill never overpaints the skin. Pure geometry.</summary>
        public Rect InnerBounds => Style.ContentRect(Bounds);

        /// <summary>True when <see cref="FillDirection"/> runs along the X axis (Left/Right).</summary>
        bool IsHorizontal => FillDirection is FillDirection.LeftToRight or FillDirection.RightToLeft;

        /// <summary>The whole-bar accent fill rect at the current <see cref="Fraction"/>, growing from the
        /// <see cref="FillDirection"/> edge. This is the un-segmented fill (and the source the
        /// <see cref="SegmentFillMode.Continuous"/> mode clips into each segment). Pure geometry.</summary>
        public Rect FillRect => FillRectFor(_fraction);

        Rect FillRectFor(float f)
        {
            Rect inner = InnerBounds;
            return FillDirection switch
            {
                FillDirection.LeftToRight => new Rect(inner.X, inner.Y, inner.Width * f, inner.Height),
                FillDirection.RightToLeft => new Rect(inner.X + inner.Width * (1f - f), inner.Y, inner.Width * f, inner.Height),
                FillDirection.TopToBottom => new Rect(inner.X, inner.Y, inner.Width, inner.Height * f),
                _ /* BottomToTop */        => new Rect(inner.X, inner.Y + inner.Height * (1f - f), inner.Width, inner.Height * f),
            };
        }

        /// <summary>
        /// The per-segment TRACK rects in FILL order (index 0 is the segment at the <see cref="FillDirection"/> origin
        /// edge, so <see cref="SegmentFillMode.Discrete"/> lights 0..N-1 in order). Returns a single rect equal to
        /// <see cref="InnerBounds"/> when <see cref="SegmentCount"/> &lt;= 1. Pure geometry (no GPU), headless-testable.
        /// </summary>
        public Rect[] SegmentRects()
        {
            Rect inner = InnerBounds;
            int n = SegmentCount;
            if (n <= 1) return new[] { inner };

            float spacing = MathF.Max(0f, SegmentSpacing);
            bool horiz = IsHorizontal;
            float axisExtent = horiz ? inner.Width : inner.Height;
            float segExtent = MathF.Max(0f, (axisExtent - spacing * (n - 1)) / n);

            var rects = new Rect[n];
            for (int i = 0; i < n; i++)
            {
                float offset = i * (segExtent + spacing);   // from the origin edge, in fill order
                rects[i] = FillDirection switch
                {
                    FillDirection.LeftToRight => new Rect(inner.X + offset, inner.Y, segExtent, inner.Height),
                    FillDirection.RightToLeft => new Rect(inner.Right - offset - segExtent, inner.Y, segExtent, inner.Height),
                    FillDirection.TopToBottom => new Rect(inner.X, inner.Y + offset, inner.Width, segExtent),
                    _ /* BottomToTop */        => new Rect(inner.X, inner.Bottom - offset - segExtent, inner.Width, segExtent),
                };
            }
            return rects;
        }

        /// <summary>
        /// The number of fully-covered segments at the current <see cref="Fraction"/> (for
        /// <see cref="SegmentFillMode.Discrete"/>): <c>floor(Fraction * SegmentCount)</c> clamped to 0..N, so a segment
        /// lights only once the fill reaches its far edge (a small epsilon lands exact segment boundaries filled).
        /// When <see cref="SegmentCount"/> &lt;= 1 this is 1 while any fill is present, else 0. Pure, headless-testable.
        /// </summary>
        public int FilledSegmentCount
        {
            get
            {
                int n = SegmentCount;
                if (n <= 1) return _fraction > 0f ? 1 : 0;
                int filled = (int)MathF.Floor(_fraction * n + 1e-4f);
                return filled < 0 ? 0 : filled > n ? n : filled;
            }
        }

        static Rect Intersect(Rect a, Rect b)
        {
            float x0 = MathF.Max(a.X, b.X), y0 = MathF.Max(a.Y, b.Y);
            float x1 = MathF.Min(a.Right, b.Right), y1 = MathF.Min(a.Bottom, b.Bottom);
            return new Rect(x0, y0, MathF.Max(0f, x1 - x0), MathF.Max(0f, y1 - y0));
        }

        /// <summary>
        /// Draw the track + border, the accent fill (honouring <see cref="FillDirection"/> and any segmentation), and
        /// the optional overlay text (always centered in <see cref="Bounds"/>). <paramref name="white"/> is a 1x1
        /// white texture. <paramref name="font"/> renders <see cref="OverlayText"/> and is only needed when it is set.
        /// </summary>
        public void Draw(SpriteBatch batch, Texture2D white, SpriteFont? font = null)
        {
            GuiDraw.FillStyled(batch, white, Bounds, Style,
                GuiDraw.WithOpacity(TrackColor, Opacity), GuiDraw.WithOpacity(BorderColor, Opacity));

            Vector4 fillColor = GuiDraw.WithOpacity(FillColor, Opacity);

            if (SegmentCount > 1)
            {
                Rect[] segs = SegmentRects();
                if (SegmentFillMode == SegmentFillMode.Discrete)
                {
                    int filled = FilledSegmentCount;
                    for (int i = 0; i < filled && i < segs.Length; i++)
                        DrawFill(batch, white, segs[i], fillColor);
                }
                else
                {
                    Rect fill = FillRect;
                    foreach (Rect seg in segs)
                        DrawFill(batch, white, Intersect(seg, fill), fillColor);
                }
            }
            else
            {
                DrawFill(batch, white, FillRect, fillColor);
            }

            if (font != null && OverlayText is { } text)
            {
                string s = text.Resolve();
                if (s.Length > 0)
                {
                    Vector2 pos = GuiDraw.AlignedTextPos(Bounds, font.Measure(s), font.LineHeight, GuiAlign.Center,
                        OverlayTextScale);
                    batch.DrawString(font, s, pos, (Color)GuiDraw.WithOpacity(OverlayTextColor, Opacity), OverlayTextScale);
                }
            }
        }

        /// <summary>Optional centered label drawn over the bar (default <c>null</c> = none). A <see cref="LocalizedText"/>
        /// so a caption resolves through the catalog. Wrap a pure number / percentage in <see cref="LocalizedText.Raw"/>
        /// (a non-localizable token).</summary>
        public LocalizedText? OverlayText;

        static void DrawFill(SpriteBatch batch, Texture2D white, Rect r, Vector4 color)
        {
            if (r.Width > 0f && r.Height > 0f)
                GuiDraw.Fill(batch, white, r, color);
        }
    }
}
