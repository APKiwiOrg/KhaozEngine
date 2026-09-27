using System;
using System.Numerics;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui
{
    internal readonly record struct RadialMenuEntryTextLayout(
        string FirstLabelLine,
        string? SecondLabelLine,
        Vector2 FirstLabelPosition,
        Vector2 SecondLabelPosition,
        Vector2 DetailPosition,
        float LabelScale,
        float DetailScale,
        float BlockHeight);

    internal static class RadialMenuTextLayout
    {
        internal static RadialMenuEntryTextLayout ComputeEntry(
            ITextMeasurer font,
            string label,
            string detail,
            bool showDetail,
            bool hasIcon,
            Vector2 point,
            float maximumTextWidth,
            RadialMenuMetrics metrics)
        {
            ArgumentNullException.ThrowIfNull(font);
            Vector2 measuredLabel = font.Measure(label);
            float labelScale = FittedScale(measuredLabel.X, metrics.LabelScale, maximumTextWidth);
            string firstLabelLine = label;
            string? secondLabelLine = null;
            if (labelScale < metrics.LabelScale * 0.75f &&
                TrySplitLabel(font, label, out string first, out string second))
            {
                firstLabelLine = first;
                secondLabelLine = second;
                float widestLine = MathF.Max(font.Measure(first).X, font.Measure(second).X);
                labelScale = FittedScale(widestLine, metrics.LabelScale, maximumTextWidth);
            }

            Vector2 firstLabelSize = font.Measure(firstLabelLine) * labelScale;
            Vector2 secondLabelSize = secondLabelLine is null
                ? Vector2.Zero
                : font.Measure(secondLabelLine) * labelScale;
            float labelBlockHeight = firstLabelSize.Y +
                (secondLabelLine is null ? 0f : secondLabelSize.Y + 1f);
            bool hasDetail = showDetail && detail.Length > 0;
            Vector2 measuredDetail = hasDetail ? font.Measure(detail) : Vector2.Zero;
            float detailScale = FittedScale(
                measuredDetail.X,
                metrics.LabelScale * 0.72f,
                maximumTextWidth);
            Vector2 detailSize = measuredDetail * detailScale;
            float detailGap = hasDetail ? metrics.DetailGap : 0f;
            float blockHeight = labelBlockHeight + detailGap + detailSize.Y;
            float labelY = hasIcon ? point.Y + 4f : point.Y - blockHeight * 0.5f;
            Vector2 firstLabelPosition = new(
                point.X - firstLabelSize.X * 0.5f,
                labelY);
            Vector2 secondLabelPosition = new(
                point.X - secondLabelSize.X * 0.5f,
                labelY + firstLabelSize.Y + 1f);
            Vector2 detailPosition = new(
                point.X - detailSize.X * 0.5f,
                labelY + labelBlockHeight + detailGap);
            return new RadialMenuEntryTextLayout(
                firstLabelLine,
                secondLabelLine,
                firstLabelPosition,
                secondLabelPosition,
                detailPosition,
                labelScale,
                detailScale,
                blockHeight);
        }

        internal static bool TrySplitLabel(
            ITextMeasurer font,
            string label,
            out string first,
            out string second)
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

        static float FittedScale(float measuredWidth, float preferredScale, float maximumWidth) =>
            measuredWidth > 0f
                ? MathF.Min(preferredScale, maximumWidth / measuredWidth)
                : preferredScale;
    }
}
