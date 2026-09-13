using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui
{
    public sealed partial class RadialMenu
    {
        const int MaximumCenterLines = 5;
        const byte CenterText = 0;
        const byte CenterMuted = 1;
        const byte CenterDisabled = 2;

        readonly string?[] _drawCenterLines = new string?[MaximumCenterLines];
        readonly Vector2[] _drawCenterPositions = new Vector2[MaximumCenterLines];
        readonly Vector2[] _drawCenterMeasurements = new Vector2[MaximumCenterLines];
        readonly float[] _drawCenterScales = new float[MaximumCenterLines];
        readonly float[] _drawCenterGaps = new float[MaximumCenterLines];
        readonly byte[] _drawCenterColors = new byte[MaximumCenterLines];
        int _drawCenterLineCount;

        void CacheCenterText(SpriteFont font)
        {
            _drawCenterLineCount = 0;
            float paddedRadius = MathF.Max(1f, Metrics.InnerRadius - MathF.Max(6f, Metrics.BorderThickness * 3f));
            float wrapWidth = paddedRadius * 1.45f;

            if (LockedEntryIndex >= 0)
            {
                AppendCenterText(
                    font,
                    _entries[LockedEntryIndex].Content,
                    Metrics.LabelScale * 0.72f,
                    wrapWidth,
                    0f,
                    CenterText);
                AppendCenterText(
                    font,
                    _choicePrompt,
                    Metrics.LabelScale,
                    wrapWidth,
                    _drawCenterLineCount > 0 ? 4f : 0f,
                    CenterMuted);
            }
            else
            {
                AppendCenterText(font, _title, 1f, wrapWidth, 0f, CenterText);
                if (ActiveIndex >= 0 && _entries[ActiveIndex].Detail.Length > 0)
                {
                    AppendCenterText(
                        font,
                        _entries[ActiveIndex].Detail,
                        Metrics.LabelScale * 0.78f,
                        wrapWidth,
                        _drawCenterLineCount > 0 ? 4f : 0f,
                        _entries[ActiveIndex].Enabled ? CenterMuted : CenterDisabled);
                }
            }

            FitCenterText(paddedRadius);
        }

        void AppendCenterText(
            SpriteFont font,
            string text,
            float preferredScale,
            float wrapWidth,
            float gapBefore,
            byte color)
        {
            if (text.Length == 0 || _drawCenterLineCount >= MaximumCenterLines)
                return;

            if (font.Measure(text).X * preferredScale > wrapWidth &&
                _drawCenterLineCount + 1 < MaximumCenterLines &&
                TrySplitLabel(font, text, out string first, out string second))
            {
                AppendCenterLine(font, first, preferredScale, gapBefore, color);
                AppendCenterLine(font, second, preferredScale, 1f, color);
                return;
            }

            AppendCenterLine(font, text, preferredScale, gapBefore, color);
        }

        void AppendCenterLine(SpriteFont font, string text, float scale, float gapBefore, byte color)
        {
            int index = _drawCenterLineCount++;
            _drawCenterLines[index] = text;
            _drawCenterMeasurements[index] = font.Measure(text);
            _drawCenterScales[index] = scale;
            _drawCenterGaps[index] = gapBefore;
            _drawCenterColors[index] = color;
        }

        void FitCenterText(float radius)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                float height = CenterBlockHeight();
                if (height > radius * 2f)
                {
                    float scale = radius * 2f / height;
                    for (int i = 0; i < _drawCenterLineCount; i++)
                    {
                        _drawCenterScales[i] *= scale;
                        _drawCenterGaps[i] *= scale;
                    }
                    height = CenterBlockHeight();
                }

                float y = _center.Y - height * 0.5f;
                for (int i = 0; i < _drawCenterLineCount; i++)
                {
                    y += _drawCenterGaps[i];
                    Vector2 measured = _drawCenterMeasurements[i];
                    float scale = _drawCenterScales[i];
                    float lineHeight = measured.Y * scale;
                    float farY = MathF.Max(MathF.Abs(y - _center.Y), MathF.Abs(y + lineHeight - _center.Y));
                    float halfChord = MathF.Sqrt(MathF.Max(0f, radius * radius - farY * farY));
                    float maximumWidth = halfChord * 2f;
                    if (measured.X * scale > maximumWidth && measured.X > 0f)
                    {
                        scale = maximumWidth / measured.X;
                        _drawCenterScales[i] = scale;
                        lineHeight = measured.Y * scale;
                    }
                    _drawCenterPositions[i] = new Vector2(
                        _center.X - measured.X * scale * 0.5f,
                        y);
                    y += lineHeight;
                }
            }
        }

        float CenterBlockHeight()
        {
            float height = 0f;
            for (int i = 0; i < _drawCenterLineCount; i++)
                height += _drawCenterGaps[i] + _drawCenterMeasurements[i].Y * _drawCenterScales[i];
            return height;
        }

        void DrawCenterText(SpriteBatch batch, SpriteFont font)
        {
            for (int i = 0; i < _drawCenterLineCount; i++)
            {
                Color color = _drawCenterColors[i] switch
                {
                    CenterMuted => (Color)Theme.TextMuted,
                    CenterDisabled => DisabledDetailColor(),
                    _ => (Color)Theme.Text,
                };
                batch.DrawString(
                    font,
                    _drawCenterLines[i]!,
                    _drawCenterPositions[i],
                    color,
                    _drawCenterScales[i]);
            }
        }
    }
}
