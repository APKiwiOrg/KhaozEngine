using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui
{
    public sealed partial class RadialMenu
    {
        readonly float[] _drawStarts = new float[MaximumEntryCount];
        readonly float[] _drawSweeps = new float[MaximumEntryCount];
        readonly Vector2[] _drawInnerStarts = new Vector2[MaximumEntryCount];
        readonly Vector2[] _drawOuterStarts = new Vector2[MaximumEntryCount];
        readonly Vector2[] _drawInnerEnds = new Vector2[MaximumEntryCount];
        readonly Vector2[] _drawOuterEnds = new Vector2[MaximumEntryCount];
        readonly Vector2[] _drawLabelPositions = new Vector2[MaximumEntryCount];
        readonly Rect[] _drawIconBounds = new Rect[MaximumEntryCount];
        readonly Texture2D?[] _drawIconTextures = new Texture2D?[MaximumEntryCount];
        readonly Vector4[] _drawIconUvs = new Vector4[MaximumEntryCount];
        readonly Rect[] _drawChoiceBounds = new Rect[MaximumChoiceCount];
        readonly Vector2[] _drawChoiceLabelPositions = new Vector2[MaximumChoiceCount];

        PrimitiveRenderer? _drawPrimitives;
        Texture2D? _drawWhite;
        SpriteFont? _drawFont;
        IconAtlas? _drawIcons;
        ResolvedRadialMenuEntry[]? _drawEntriesSource;
        ResolvedRadialMenuChoice[]? _drawChoicesSource;
        RadialMenuMetrics _drawMetrics;
        Vector2 _drawCenter;
        Vector2 _drawTitlePosition;
        Vector2 _drawDetailPosition;
        int _drawEntryCount = -1;
        int _drawChoiceCount = -1;
        int _drawDetailEntry = -2;

        public RadialMenuTheme Theme { get; set; } = RadialMenuTheme.Default;

        /// <summary>The title resolved and retained by the most recent <see cref="Open"/> call.</summary>
        public string ResolvedTitle => _title;

        /// <summary>Returns an entry label resolved and retained by the most recent <see cref="Open"/> call.</summary>
        public string ResolvedEntryLabel(int entryIndex) => ResolvedEntry(entryIndex).Content;

        /// <summary>Returns entry detail resolved and retained by the most recent <see cref="Open"/> call.</summary>
        public string ResolvedEntryDetail(int entryIndex) => ResolvedEntry(entryIndex).Detail;

        /// <summary>Returns a footer label resolved and retained by the most recent <see cref="Open"/> call.</summary>
        public string ResolvedChoiceLabel(int choiceIndex) => ResolvedChoice(choiceIndex).Content;

        /// <summary>Draws the open radial menu through the shared Render2D batch.</summary>
        public void Draw(
            SpriteBatch batch,
            Texture2D white,
            SpriteFont font,
            IconAtlas? icons = null)
        {
            ArgumentNullException.ThrowIfNull(batch);
            ArgumentNullException.ThrowIfNull(white);
            ArgumentNullException.ThrowIfNull(font);
            if (!IsOpen)
                return;

            EnsureDrawCache(white, font, icons);
            PrimitiveRenderer primitives = _drawPrimitives!;

            DrawWheelBands(primitives, batch, _center + Metrics.ShadowOffset, Theme.Shadow, activeOnly: false);
            DrawWheelBands(primitives, batch, _center, Theme.Surface, activeOnly: false);
            DrawWheelBands(primitives, batch, _center, Theme.Accent, activeOnly: true);
            DrawUpperHighlights(primitives, batch);
            DrawBorders(primitives, batch);
            DrawIcons(batch);
            DrawLabels(batch, font);
            DrawCenterText(batch, font);
            DrawFooter(primitives, batch, font);
            DrawSheen(primitives, batch);
        }

        void EnsureDrawCache(Texture2D white, SpriteFont font, IconAtlas? icons)
        {
            if (!ReferenceEquals(_drawWhite, white))
            {
                _drawPrimitives = new PrimitiveRenderer(white);
                _drawWhite = white;
            }

            bool geometryChanged = _drawEntryCount != _entries.Length ||
                _drawChoiceCount != _choices.Length ||
                _drawCenter != _center ||
                _drawMetrics != Metrics;
            bool textChanged = geometryChanged ||
                !ReferenceEquals(_drawEntriesSource, _entries) ||
                !ReferenceEquals(_drawChoicesSource, _choices) ||
                !ReferenceEquals(_drawFont, font) ||
                !ReferenceEquals(_drawIcons, icons);

            if (geometryChanged)
            {
                CacheGeometry();
                _drawEntryCount = _entries.Length;
                _drawChoiceCount = _choices.Length;
                _drawCenter = _center;
                _drawMetrics = Metrics;
            }

            if (textChanged)
            {
                CacheTextLayout(font, icons);
                _drawFont = font;
                _drawIcons = icons;
                _drawEntriesSource = _entries;
                _drawChoicesSource = _choices;
                _drawDetailEntry = -2;
            }

            if (_drawDetailEntry != ActiveIndex)
            {
                string detail = ActiveIndex >= 0 ? _entries[ActiveIndex].Detail : "";
                Vector2 detailSize = font.Measure(detail) * Metrics.LabelScale;
                _drawDetailPosition = new Vector2(
                    _center.X - detailSize.X * 0.5f,
                    _center.Y + Metrics.DetailGap);
                _drawDetailEntry = ActiveIndex;
            }
        }

        void CacheGeometry()
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                (float start, float end) = WedgeAngles(i, _entries.Length, Metrics);
                float sweep = end - start;
                _drawStarts[i] = start;
                _drawSweeps[i] = sweep;
                _drawInnerStarts[i] = PointOnCircle(_center, Metrics.InnerRadius, start);
                _drawOuterStarts[i] = PointOnCircle(_center, Metrics.OuterRadius, start);
                _drawInnerEnds[i] = PointOnCircle(_center, Metrics.InnerRadius, end);
                _drawOuterEnds[i] = PointOnCircle(_center, Metrics.OuterRadius, end);
            }

            for (int i = 0; i < _choices.Length; i++)
                _drawChoiceBounds[i] = ChoiceBounds(_center, _choices.Length, i, Metrics);
        }

        void CacheTextLayout(SpriteFont font, IconAtlas? icons)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                Vector2 point = LabelPoint(_center, i, _entries.Length, Metrics);
                string label = _entries[i].Content;
                Vector2 labelSize = font.Measure(label) * Metrics.LabelScale;
                Texture2D? texture = null;
                Vector4 uv = default;
                bool hasIcon = _entries[i].IconId is { } iconId && icons is not null &&
                    icons.TryGet(iconId, out texture, out uv);

                if (hasIcon)
                {
                    _drawIconTextures[i] = texture;
                    _drawIconUvs[i] = uv;
                    _drawIconBounds[i] = new Rect(
                        point.X - Metrics.IconSize * 0.5f,
                        point.Y - Metrics.IconSize,
                        Metrics.IconSize,
                        Metrics.IconSize);
                    _drawLabelPositions[i] = new Vector2(
                        point.X - labelSize.X * 0.5f,
                        point.Y + 4f);
                }
                else
                {
                    _drawIconTextures[i] = null;
                    _drawIconUvs[i] = default;
                    _drawIconBounds[i] = default;
                    _drawLabelPositions[i] = point - labelSize * 0.5f;
                }
            }

            for (int i = 0; i < _choices.Length; i++)
            {
                Vector2 labelSize = font.Measure(_choices[i].Content) * Metrics.LabelScale;
                Rect bounds = _drawChoiceBounds[i];
                _drawChoiceLabelPositions[i] = new Vector2(
                    bounds.X + (bounds.Width - labelSize.X) * 0.5f,
                    bounds.Y + (bounds.Height - labelSize.Y) * 0.5f);
            }

            Vector2 titleSize = font.Measure(_title);
            _drawTitlePosition = new Vector2(
                _center.X - titleSize.X * 0.5f,
                _center.Y - titleSize.Y - Metrics.DetailGap * 0.5f);
        }

        void DrawWheelBands(
            PrimitiveRenderer primitives,
            SpriteBatch batch,
            Vector2 center,
            Vector4 color,
            bool activeOnly)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (activeOnly && i != ActiveIndex)
                    continue;
                primitives.DrawFilledArcBand(
                    batch,
                    center,
                    Metrics.InnerRadius,
                    Metrics.OuterRadius,
                    _drawStarts[i],
                    _drawSweeps[i],
                    DrawColor(color, _entries[i].Enabled));
            }
        }

        void DrawUpperHighlights(PrimitiveRenderer primitives, SpriteBatch batch)
        {
            float width = Metrics.OuterRadius - Metrics.InnerRadius;
            float highlightInner = Metrics.InnerRadius + width * 0.62f;
            for (int i = 0; i < _entries.Length; i++)
            {
                primitives.DrawFilledArcBand(
                    batch,
                    _center,
                    highlightInner,
                    Metrics.OuterRadius - Metrics.BorderThickness,
                    _drawStarts[i],
                    _drawSweeps[i],
                    DrawColor(Theme.SurfaceHighlight, _entries[i].Enabled));
            }
        }

        void DrawBorders(PrimitiveRenderer primitives, SpriteBatch batch)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                Vector4 border = i == ActiveIndex ? Theme.BorderActive : Theme.Border;
                Color color = DrawColor(border, _entries[i].Enabled);
                primitives.DrawArc(batch, _center, Metrics.InnerRadius, Metrics.BorderThickness,
                    _drawStarts[i], _drawSweeps[i], color);
                primitives.DrawArc(batch, _center, Metrics.OuterRadius, Metrics.BorderThickness,
                    _drawStarts[i], _drawSweeps[i], color);
                primitives.DrawLine(batch, _drawInnerStarts[i], _drawOuterStarts[i], color, Metrics.BorderThickness);
                primitives.DrawLine(batch, _drawInnerEnds[i], _drawOuterEnds[i], color, Metrics.BorderThickness);
            }
        }

        void DrawIcons(SpriteBatch batch)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                Texture2D? texture = _drawIconTextures[i];
                if (texture is null)
                    continue;
                batch.Draw(texture, _drawIconBounds[i], _drawIconUvs[i], DrawColor(Theme.Text, _entries[i].Enabled));
            }
        }

        void DrawLabels(SpriteBatch batch, SpriteFont font)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                Vector4 text = i == ActiveIndex ? Theme.Text : Theme.TextMuted;
                batch.DrawString(font, _entries[i].Content, _drawLabelPositions[i],
                    DrawColor(text, _entries[i].Enabled), Metrics.LabelScale);
            }
        }

        void DrawCenterText(SpriteBatch batch, SpriteFont font)
        {
            batch.DrawString(font, _title, _drawTitlePosition, (Color)Theme.Text);
            if (ActiveIndex < 0 || _entries[ActiveIndex].Detail.Length == 0)
                return;
            batch.DrawString(font, _entries[ActiveIndex].Detail, _drawDetailPosition,
                DrawColor(Theme.TextMuted, _entries[ActiveIndex].Enabled), Metrics.LabelScale);
        }

        void DrawFooter(PrimitiveRenderer primitives, SpriteBatch batch, SpriteFont font)
        {
            for (int i = 0; i < _choices.Length; i++)
            {
                Rect bounds = _drawChoiceBounds[i];
                Rect shadow = new(
                    bounds.X + Metrics.ShadowOffset.X,
                    bounds.Y + Metrics.ShadowOffset.Y,
                    bounds.Width,
                    bounds.Height);
                primitives.DrawFilledRect(batch, shadow, DrawColor(Theme.Shadow, _choices[i].Enabled));
            }

            long selectedTag = ActiveIndex >= 0 ? _entryChoiceTags[ActiveIndex] : 0;
            for (int i = 0; i < _choices.Length; i++)
            {
                bool selected = _choices[i].Tag == selectedTag;
                bool focused = _footerFocused && i == _focusedChoiceIndex;
                Vector4 fill = selected ? Theme.Accent : Theme.Surface;
                Vector4 border = selected || focused ? Theme.BorderActive : Theme.Border;
                primitives.DrawFilledRect(batch, _drawChoiceBounds[i], DrawColor(fill, _choices[i].Enabled));
                primitives.DrawRect(batch, _drawChoiceBounds[i], DrawColor(border, _choices[i].Enabled), Metrics.BorderThickness);
                batch.DrawString(font, _choices[i].Content, _drawChoiceLabelPositions[i],
                    DrawColor(selected ? Theme.Text : Theme.TextMuted, _choices[i].Enabled), Metrics.LabelScale);
            }
        }

        void DrawSheen(PrimitiveRenderer primitives, SpriteBatch batch)
        {
            float sheenAngle = _sheenPhase * Tau - MathF.PI / 2f;
            float width = Metrics.OuterRadius - Metrics.InnerRadius;
            float sheenInner = Metrics.InnerRadius + width * 0.78f;
            for (int i = 0; i < _entries.Length; i++)
            {
                float wedgeCenter = _drawStarts[i] + _drawSweeps[i] * 0.5f;
                float distance = MathF.Abs(NormalizeSignedAngle(wedgeCenter - sheenAngle));
                float strength = Math.Clamp(1f - distance / (MathF.PI * 0.75f), 0f, 1f);
                if (strength <= 0f)
                    continue;
                Color color = DrawColor(WithAlpha(Theme.Sheen, Theme.Sheen.W * strength), _entries[i].Enabled);
                primitives.DrawFilledArcBand(batch, _center, sheenInner, Metrics.OuterRadius,
                    _drawStarts[i], _drawSweeps[i], color);
            }
        }

        ResolvedRadialMenuEntry ResolvedEntry(int entryIndex)
        {
            if ((uint)entryIndex >= (uint)_entries.Length)
                throw new ArgumentOutOfRangeException(nameof(entryIndex), entryIndex, "Index must identify an existing entry.");
            return _entries[entryIndex];
        }

        ResolvedRadialMenuChoice ResolvedChoice(int choiceIndex)
        {
            if ((uint)choiceIndex >= (uint)_choices.Length)
                throw new ArgumentOutOfRangeException(nameof(choiceIndex), choiceIndex, "Index must identify an existing choice.");
            return _choices[choiceIndex];
        }

        Color DrawColor(Vector4 color, bool enabled) =>
            (Color)WithAlpha(color, color.W * (enabled ? 1f : Theme.Disabled.W));

        static Vector4 WithAlpha(Vector4 color, float alpha) =>
            new(color.X, color.Y, color.Z, alpha);

        static Vector2 PointOnCircle(Vector2 center, float radius, float angle) =>
            center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;

        static float NormalizeSignedAngle(float angle)
        {
            float normalized = angle % Tau;
            if (normalized > MathF.PI) normalized -= Tau;
            if (normalized < -MathF.PI) normalized += Tau;
            return normalized;
        }
    }
}
