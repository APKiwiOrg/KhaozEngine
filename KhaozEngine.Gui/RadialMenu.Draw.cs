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
        readonly Vector2[] _drawSecondLabelPositions = new Vector2[MaximumEntryCount];
        readonly Vector2[] _drawDisabledDetailPositions = new Vector2[MaximumEntryCount];
        readonly float[] _drawLabelScales = new float[MaximumEntryCount];
        readonly float[] _drawDisabledDetailScales = new float[MaximumEntryCount];
        readonly string?[] _drawFirstLabelLines = new string?[MaximumEntryCount];
        readonly string?[] _drawSecondLabelLines = new string?[MaximumEntryCount];
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
        int _drawEntryCount = -1;
        int _drawChoiceCount = -1;
        int _drawDetailEntry = -2;
        int _drawLockedEntry = -2;
        int _drawQuickSelectPreviewEntry = -2;
        int _drawQuickSelectPreviewChoice = -2;

        public RadialMenuTheme Theme { get; set; } = RadialMenuTheme.Default;

        /// <summary>The title resolved and retained by the most recent <c>Open</c> call.</summary>
        public string ResolvedTitle => _title;

        /// <summary>The post-lock choice prompt resolved and retained by the most recent <c>Open</c> call.</summary>
        public string ResolvedChoicePrompt => _choicePrompt;

        /// <summary>Returns an entry label resolved and retained by the most recent <c>Open</c> call.</summary>
        public string ResolvedEntryLabel(int entryIndex) => ResolvedEntry(entryIndex).Content;

        /// <summary>Returns entry detail resolved and retained by the most recent <c>Open</c> call.</summary>
        public string ResolvedEntryDetail(int entryIndex) => ResolvedEntry(entryIndex).Detail;

        /// <summary>Returns a footer label resolved and retained by the most recent <c>Open</c> call.</summary>
        public string ResolvedChoiceLabel(int choiceIndex) => ResolvedChoice(choiceIndex).Content;

        void InvalidateDrawLayoutCache()
        {
            _drawEntryCount = -1;
            _drawChoiceCount = -1;
            _drawDetailEntry = -2;
            _drawLockedEntry = -2;
            _drawQuickSelectPreviewEntry = -2;
            _drawQuickSelectPreviewChoice = -2;
        }

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
            DrawCenterPlateShadow(primitives, batch);
            DrawWheelBands(primitives, batch, _center, Theme.Surface, activeOnly: false);
            DrawCenterPlateSurface(primitives, batch);
            DrawWheelBands(primitives, batch, _center, Theme.Accent, activeOnly: true);
            DrawUpperHighlights(primitives, batch);
            DrawCenterPlateHighlight(primitives, batch);
            DrawBorders(primitives, batch);
            DrawCenterPlateBorder(primitives, batch);
            DrawIcons(batch);
            DrawLabels(batch, font);
            DrawCenterText(batch, font);
            DrawFooter(primitives, batch, font);
            DrawSheen(primitives, batch);
            EntryContextMenu?.Draw(batch, white);
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
                _drawLockedEntry = -2;
                _drawQuickSelectPreviewEntry = -2;
                _drawQuickSelectPreviewChoice = -2;
            }

            int previewChoice = QuickSelectPreviewChoiceIndex;
            if (_drawDetailEntry != ActiveIndex ||
                _drawLockedEntry != LockedEntryIndex ||
                _drawQuickSelectPreviewEntry != QuickSelectPreviewIndex ||
                _drawQuickSelectPreviewChoice != previewChoice)
            {
                CacheCenterText(font);
                _drawDetailEntry = ActiveIndex;
                _drawLockedEntry = LockedEntryIndex;
                _drawQuickSelectPreviewEntry = QuickSelectPreviewIndex;
                _drawQuickSelectPreviewChoice = previewChoice;
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
                float maximumTextWidth = EntryTextWidth(i);
                Vector2 measuredLabel = font.Measure(label);
                float labelScale = FittedScale(measuredLabel.X, Metrics.LabelScale, maximumTextWidth);
                string firstLabelLine = label;
                string? secondLabelLine = null;
                if (labelScale < Metrics.LabelScale * 0.75f &&
                    TrySplitLabel(font, label, out string first, out string second))
                {
                    firstLabelLine = first;
                    secondLabelLine = second;
                    float widestLine = MathF.Max(font.Measure(first).X, font.Measure(second).X);
                    labelScale = FittedScale(widestLine, Metrics.LabelScale, maximumTextWidth);
                }
                Vector2 firstLabelSize = font.Measure(firstLabelLine) * labelScale;
                Vector2 secondLabelSize = secondLabelLine is null
                    ? Vector2.Zero
                    : font.Measure(secondLabelLine) * labelScale;
                float labelBlockHeight = firstLabelSize.Y + (secondLabelLine is null ? 0f : secondLabelSize.Y + 1f);
                bool hasDisabledDetail = !_entries[i].Enabled && _entries[i].Detail.Length > 0;
                Vector2 measuredDisabledDetail = hasDisabledDetail
                    ? font.Measure(_entries[i].Detail)
                    : Vector2.Zero;
                float disabledDetailScale = FittedScale(
                    measuredDisabledDetail.X,
                    Metrics.LabelScale * 0.72f,
                    maximumTextWidth);
                Vector2 disabledDetailSize = measuredDisabledDetail * disabledDetailScale;
                _drawLabelScales[i] = labelScale;
                _drawDisabledDetailScales[i] = disabledDetailScale;
                _drawFirstLabelLines[i] = firstLabelLine;
                _drawSecondLabelLines[i] = secondLabelLine;
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
                        point.X - firstLabelSize.X * 0.5f,
                        point.Y + 4f);
                    _drawSecondLabelPositions[i] = new Vector2(
                        point.X - secondLabelSize.X * 0.5f,
                        _drawLabelPositions[i].Y + firstLabelSize.Y + 1f);
                    _drawDisabledDetailPositions[i] = new Vector2(
                        point.X - disabledDetailSize.X * 0.5f,
                        _drawLabelPositions[i].Y + labelBlockHeight + 2f);
                }
                else
                {
                    _drawIconTextures[i] = null;
                    _drawIconUvs[i] = default;
                    _drawIconBounds[i] = default;
                    float blockHeight = labelBlockHeight + (hasDisabledDetail ? disabledDetailSize.Y + 2f : 0f);
                    _drawLabelPositions[i] = new Vector2(
                        point.X - firstLabelSize.X * 0.5f,
                        point.Y - blockHeight * 0.5f);
                    _drawSecondLabelPositions[i] = new Vector2(
                        point.X - secondLabelSize.X * 0.5f,
                        _drawLabelPositions[i].Y + firstLabelSize.Y + 1f);
                    _drawDisabledDetailPositions[i] = new Vector2(
                        point.X - disabledDetailSize.X * 0.5f,
                        _drawLabelPositions[i].Y + labelBlockHeight + 2f);
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
                if (activeOnly && i != HighlightedEntryIndex)
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
            for (int i = 0; i < _entries.Length; i++)
            {
                primitives.DrawFilledArcBandGradient(
                    batch,
                    _center,
                    Metrics.InnerRadius,
                    Metrics.OuterRadius - Metrics.BorderThickness,
                    _drawStarts[i],
                    _drawSweeps[i],
                    DrawColor(WithAlpha(Theme.SurfaceHighlight, 0f), _entries[i].Enabled),
                    DrawColor(Theme.SurfaceHighlight, _entries[i].Enabled));
            }
        }

        void DrawBorders(PrimitiveRenderer primitives, SpriteBatch batch)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                Vector4 border = i == HighlightedEntryIndex
                    ? Theme.BorderActive
                    : Theme.Border;
                Color color = DrawColor(border, _entries[i].Enabled);
                primitives.DrawArc(batch, _center, Metrics.InnerRadius, Metrics.BorderThickness,
                    _drawStarts[i], _drawSweeps[i], color);
                primitives.DrawArc(batch, _center, Metrics.OuterRadius, Metrics.BorderThickness,
                    _drawStarts[i], _drawSweeps[i], color);
                primitives.DrawLine(batch, _drawInnerStarts[i], _drawOuterStarts[i], color, Metrics.BorderThickness);
                primitives.DrawLine(batch, _drawInnerEnds[i], _drawOuterEnds[i], color, Metrics.BorderThickness);
            }
        }

        void DrawCenterPlateShadow(PrimitiveRenderer primitives, SpriteBatch batch) =>
            primitives.DrawFilledCircle(
                batch,
                _center + Metrics.ShadowOffset,
                Metrics.InnerRadius,
                (Color)Theme.Shadow);

        void DrawCenterPlateSurface(PrimitiveRenderer primitives, SpriteBatch batch) =>
            primitives.DrawFilledCircle(batch, _center, Metrics.InnerRadius, (Color)Theme.Surface);

        void DrawCenterPlateHighlight(PrimitiveRenderer primitives, SpriteBatch batch)
        {
            float radius = Metrics.InnerRadius - MathF.Max(2f, Metrics.BorderThickness * 2f);
            float thickness = MathF.Max(1f, Metrics.BorderThickness * 0.75f);
            primitives.DrawRing(batch, _center, radius, thickness, (Color)Theme.SurfaceHighlight);
        }

        void DrawCenterPlateBorder(PrimitiveRenderer primitives, SpriteBatch batch)
        {
            float radius = Metrics.InnerRadius - Metrics.BorderThickness * 0.5f;
            primitives.DrawRing(batch, _center, radius, Metrics.BorderThickness, (Color)Theme.Border);
        }

        void DrawIcons(SpriteBatch batch)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                Texture2D? texture = _drawIconTextures[i];
                if (texture is null)
                    continue;
                batch.Draw(texture, _drawIconBounds[i], _drawIconUvs[i],
                    ForegroundColor(Theme.Text, _entries[i].Enabled));
            }
        }

        void DrawLabels(SpriteBatch batch, SpriteFont font)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                Vector4 text = i == HighlightedEntryIndex
                    ? Theme.Text
                    : Theme.TextMuted;
                batch.DrawString(font, _drawFirstLabelLines[i]!, _drawLabelPositions[i],
                    ForegroundColor(text, _entries[i].Enabled), _drawLabelScales[i]);
                if (_drawSecondLabelLines[i] is { } secondLine)
                {
                    batch.DrawString(font, secondLine, _drawSecondLabelPositions[i],
                        ForegroundColor(text, _entries[i].Enabled), _drawLabelScales[i]);
                }
                if (!_entries[i].Enabled && _entries[i].Detail.Length > 0)
                {
                    batch.DrawString(
                        font,
                        _entries[i].Detail,
                        _drawDisabledDetailPositions[i],
                        DisabledDetailColor(),
                        _drawDisabledDetailScales[i]);
                }
            }
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

            int selectedEntryIndex = InteractionMode == RadialMenuInteractionMode.EntryThenChoice
                ? LockedEntryIndex
                : ActiveIndex;
            long selectedTag = selectedEntryIndex >= 0 ? _entryChoiceTags[selectedEntryIndex] : 0;
            bool footerActionable = InteractionMode != RadialMenuInteractionMode.EntryThenChoice ||
                LockedEntryIndex >= 0;
            for (int i = 0; i < _choices.Length; i++)
            {
                bool selected = _choices[i].Tag == selectedTag;
                bool focused = _footerFocused && i == _focusedChoiceIndex;
                bool enabled = _choices[i].Enabled && footerActionable;
                bool emphasized = enabled && (focused || i == HoveredChoiceIndex);
                Vector4 fill = emphasized ? ChoiceHoverFill() : Theme.Surface;
                Vector4 border = emphasized
                    ? Theme.BorderActive
                    : selected ? Theme.Accent : Theme.Border;
                primitives.DrawFilledRect(batch, _drawChoiceBounds[i], DrawColor(fill, enabled));
                primitives.DrawRect(batch, _drawChoiceBounds[i], DrawColor(border, enabled), Metrics.BorderThickness);
                batch.DrawString(font, _choices[i].Content, _drawChoiceLabelPositions[i],
                    ForegroundColor(selected || emphasized ? Theme.Text : Theme.TextMuted, enabled), Metrics.LabelScale);
            }
        }

        Vector4 ChoiceHoverFill() => new(
            Theme.Surface.X + (Theme.BorderActive.X - Theme.Surface.X) * 0.36f,
            Theme.Surface.Y + (Theme.BorderActive.Y - Theme.Surface.Y) * 0.36f,
            Theme.Surface.Z + (Theme.BorderActive.Z - Theme.Surface.Z) * 0.36f,
            Theme.Surface.W);

        void DrawSheen(PrimitiveRenderer primitives, SpriteBatch batch)
        {
            float sheenAngle = _sheenPhase * Tau - MathF.PI / 2f;
            for (int i = 0; i < _entries.Length; i++)
            {
                float wedgeCenter = _drawStarts[i] + _drawSweeps[i] * 0.5f;
                float distance = MathF.Abs(NormalizeSignedAngle(wedgeCenter - sheenAngle));
                float strength = Math.Clamp(1f - distance / (MathF.PI * 0.75f), 0f, 1f);
                if (strength <= 0f)
                    continue;
                Vector4 outer = WithAlpha(Theme.Sheen, Theme.Sheen.W * strength);
                primitives.DrawFilledArcBandGradient(
                    batch,
                    _center,
                    Metrics.InnerRadius,
                    Metrics.OuterRadius,
                    _drawStarts[i],
                    _drawSweeps[i],
                    DrawColor(WithAlpha(outer, 0f), _entries[i].Enabled),
                    DrawColor(outer, _entries[i].Enabled));
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

        Color DrawColor(Vector4 color, bool enabled)
        {
            if (enabled)
                return (Color)color;

            float luminance = color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;
            float tintLuminance =
                Theme.Disabled.X * 0.2126f + Theme.Disabled.Y * 0.7152f + Theme.Disabled.Z * 0.0722f;
            float tintScale = tintLuminance > 1e-5f ? luminance / tintLuminance : 0f;
            return new Color(
                Math.Clamp(Theme.Disabled.X * tintScale, 0f, 1f),
                Math.Clamp(Theme.Disabled.Y * tintScale, 0f, 1f),
                Math.Clamp(Theme.Disabled.Z * tintScale, 0f, 1f),
                color.W * Theme.Disabled.W);
        }

        Color DisabledDetailColor() => Theme.Disabled.W == 0f
            ? (Color)WithAlpha(Theme.DisabledDetail, 0f)
            : (Color)Theme.DisabledDetail;

        Color ForegroundColor(Vector4 color, bool enabled) =>
            enabled ? (Color)color : (Color)Theme.Disabled;

        float EntryTextWidth(int entryIndex)
        {
            float angle = _drawStarts[entryIndex] + _drawSweeps[entryIndex] * 0.5f;
            float radialSpan = Metrics.OuterRadius - Metrics.InnerRadius;
            float radialProjection = MathF.Abs(MathF.Cos(angle));
            float radialWidth = radialProjection > 1e-4f
                ? radialSpan / radialProjection
                : float.PositiveInfinity;

            float labelRadius = (Metrics.InnerRadius + Metrics.OuterRadius) * 0.5f;
            float sweep = MathF.Abs(_drawSweeps[entryIndex]);
            float tangentialSpan = sweep >= MathF.PI
                ? Metrics.OuterRadius * 2f
                : MathF.Min(
                    Metrics.OuterRadius * 2f,
                    labelRadius * 2f * MathF.Tan(sweep * 0.5f));
            float tangentialProjection = MathF.Abs(MathF.Sin(angle));
            float tangentialWidth = tangentialProjection > 1e-4f
                ? tangentialSpan / tangentialProjection
                : float.PositiveInfinity;

            return MathF.Max(1f, MathF.Min(radialWidth, tangentialWidth) - 8f);
        }

        static float FittedScale(float measuredWidth, float preferredScale, float maximumWidth) =>
            measuredWidth > 0f
                ? MathF.Min(preferredScale, maximumWidth / measuredWidth)
                : preferredScale;

        static bool TrySplitLabel(SpriteFont font, string label, out string first, out string second)
        {
            int bestBreak = -1;
            float bestWidth = float.PositiveInfinity;
            for (int i = 1; i < label.Length - 1; i++)
            {
                if (!char.IsWhiteSpace(label[i]))
                    continue;
                float firstWidth = font.Measure(label.AsSpan(0, i)).X;
                int secondStart = i + 1;
                while (secondStart < label.Length && char.IsWhiteSpace(label[secondStart]))
                    secondStart++;
                if (secondStart >= label.Length)
                    continue;
                float secondWidth = font.Measure(label.AsSpan(secondStart)).X;
                float widest = MathF.Max(firstWidth, secondWidth);
                if (widest >= bestWidth)
                    continue;
                bestWidth = widest;
                bestBreak = i;
            }

            if (bestBreak < 0)
            {
                first = label;
                second = "";
                return false;
            }

            int start = bestBreak + 1;
            while (start < label.Length && char.IsWhiteSpace(label[start]))
                start++;
            first = label[..bestBreak];
            second = label[start..];
            return true;
        }

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
