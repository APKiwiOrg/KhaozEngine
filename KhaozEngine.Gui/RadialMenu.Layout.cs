using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    public sealed partial class RadialMenu
    {
        const int MaximumEntryCount = 8;
        const int MaximumChoiceCount = 8;
        const float Tau = 2f * MathF.PI;

        public static void ValidateEntryCount(int count)
        {
            if (count is < 1 or > MaximumEntryCount)
                throw new ArgumentOutOfRangeException(nameof(count), count, "Entry count must be from one through eight.");
        }

        public static void ValidateChoiceCount(int count)
        {
            if (count is < 0 or > MaximumChoiceCount)
                throw new ArgumentOutOfRangeException(nameof(count), count, "Choice count must be from zero through eight.");
        }

        public static (float Start, float End) WedgeAngles(
            int entryIndex,
            int entryCount,
            RadialMenuMetrics metrics)
        {
            ValidateEntryCount(entryCount);
            ValidateIndex(entryIndex, entryCount, nameof(entryIndex));
            ValidateGeometry(metrics, entryCount);

            float step = Tau / entryCount;
            float centre = -MathF.PI / 2f + entryIndex * step;
            float halfVisibleArc = (step - metrics.WedgeGap) / 2f;
            return (centre - halfVisibleArc, centre + halfVisibleArc);
        }

        public static Vector2 ComputeCenter(
            Vector2 requestedCenter,
            Rect safeArea,
            int entryCount,
            int choiceCount,
            RadialMenuMetrics metrics)
        {
            ValidateEntryCount(entryCount);
            ValidateChoiceCount(choiceCount);
            ValidateGeometry(metrics, entryCount);
            ValidatePoint(requestedCenter, nameof(requestedCenter));
            ValidateRectangle(safeArea, nameof(safeArea));

            Rect bounds = ComputeBoundsCore(requestedCenter, choiceCount, metrics);
            ValidateRectangle(bounds, nameof(metrics));
            ValidateSafeAreaCanContain(bounds, safeArea, metrics);
            float minimumX = safeArea.X + metrics.Margin;
            float maximumX = safeArea.Right - metrics.Margin;
            float minimumY = safeArea.Y + metrics.Margin;
            float maximumY = safeArea.Bottom - metrics.Margin;

            float shiftX = ClampShift(bounds.X, bounds.Right, minimumX, maximumX);
            float shiftY = ClampShift(bounds.Y, bounds.Bottom, minimumY, maximumY);
            Vector2 center = requestedCenter + new Vector2(shiftX, shiftY);
            ValidatePoint(center, nameof(requestedCenter));
            return center;
        }

        public static Rect ComputeBounds(
            Vector2 center,
            int entryCount,
            int choiceCount,
            RadialMenuMetrics metrics)
        {
            ValidateEntryCount(entryCount);
            ValidateChoiceCount(choiceCount);
            ValidateGeometry(metrics, entryCount);
            ValidatePoint(center, nameof(center));

            Rect bounds = ComputeBoundsCore(center, choiceCount, metrics);
            ValidateRectangle(bounds, nameof(metrics));
            return bounds;
        }

        static Rect ComputeBoundsCore(
            Vector2 center,
            int choiceCount,
            RadialMenuMetrics metrics)
        {
            float left = center.X - metrics.OuterRadius;
            float top = center.Y - metrics.OuterRadius;
            float right = center.X + metrics.OuterRadius;
            float bottom = center.Y + metrics.OuterRadius;

            if (choiceCount > 0)
            {
                Rect first = ChoiceBoundsCore(center, choiceCount, 0, metrics);
                Rect last = ChoiceBoundsCore(center, choiceCount, choiceCount - 1, metrics);
                left = MathF.Min(left, first.X);
                right = MathF.Max(right, last.Right);
                bottom = last.Bottom;
            }

            return new Rect(left, top, right - left, bottom - top);
        }

        public static int EntryAt(
            Vector2 point,
            Vector2 center,
            int entryCount,
            RadialMenuMetrics metrics)
        {
            ValidateEntryCount(entryCount);
            ValidateGeometry(metrics, entryCount);
            ValidatePoint(point, nameof(point));
            ValidatePoint(center, nameof(center));

            Vector2 offset = point - center;
            float radiusSquared = offset.LengthSquared();
            if (radiusSquared <= metrics.InnerRadius * metrics.InnerRadius ||
                radiusSquared > metrics.OuterRadius * metrics.OuterRadius)
                return -1;

            float step = Tau / entryCount;
            float angleFromFirstEdge = NormalizeAngle(
                MathF.Atan2(offset.Y, offset.X) + MathF.PI / 2f + step / 2f);
            int entryIndex = Math.Min((int)(angleFromFirstEdge / step), entryCount - 1);
            float angleWithinWedge = angleFromFirstEdge - entryIndex * step;
            float halfGap = metrics.WedgeGap / 2f;
            if (angleWithinWedge < halfGap || angleWithinWedge > step - halfGap)
                return -1;

            return entryIndex;
        }

        public static Rect ChoiceBounds(
            Vector2 center,
            int choiceCount,
            int choiceIndex,
            RadialMenuMetrics metrics)
        {
            ValidateChoiceCount(choiceCount);
            ValidateIndex(choiceIndex, choiceCount, nameof(choiceIndex));
            ValidateGeometry(metrics);
            ValidatePoint(center, nameof(center));

            Rect bounds = ChoiceBoundsCore(center, choiceCount, choiceIndex, metrics);
            ValidateRectangle(bounds, nameof(metrics));
            return bounds;
        }

        static Rect ChoiceBoundsCore(
            Vector2 center,
            int choiceCount,
            int choiceIndex,
            RadialMenuMetrics metrics)
        {
            float width = choiceCount * metrics.FooterButtonSize.X + (choiceCount - 1) * metrics.FooterGap;
            float x = center.X - width / 2f + choiceIndex * (metrics.FooterButtonSize.X + metrics.FooterGap);
            float y = center.Y + metrics.OuterRadius + metrics.FooterGap;
            return new Rect(x, y, metrics.FooterButtonSize.X, metrics.FooterButtonSize.Y);
        }

        public static Vector2 LabelPoint(
            Vector2 center,
            int entryIndex,
            int entryCount,
            RadialMenuMetrics metrics)
        {
            ValidateEntryCount(entryCount);
            ValidateIndex(entryIndex, entryCount, nameof(entryIndex));
            ValidateGeometry(metrics, entryCount);
            ValidatePoint(center, nameof(center));

            float angle = -MathF.PI / 2f + entryIndex * Tau / entryCount;
            float radius = (metrics.InnerRadius + metrics.OuterRadius) / 2f;
            Vector2 point = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            ValidatePoint(point, nameof(metrics));
            return point;
        }

        static float ClampShift(float start, float end, float minimum, float maximum)
        {
            float extent = end - start;
            float available = maximum - minimum;
            if (extent > available)
                return minimum + (available - extent) / 2f - start;
            if (start < minimum)
                return minimum - start;
            if (end > maximum)
                return maximum - end;
            return 0f;
        }

        static float NormalizeAngle(float angle)
        {
            float normalized = angle % Tau;
            return normalized < 0f ? normalized + Tau : normalized;
        }

        static void ValidateIndex(int index, int count, string parameterName)
        {
            if ((uint)index >= (uint)count)
                throw new ArgumentOutOfRangeException(parameterName, index, "Index must identify an existing item.");
        }
    }
}
