using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    public sealed partial class RadialMenu
    {
        static void ValidateGeometry(RadialMenuMetrics metrics, int entryCount = 0)
        {
            if (!float.IsFinite(metrics.InnerRadius) || metrics.InnerRadius < 0f)
                throw InvalidMetric(metrics, "InnerRadius must be finite and nonnegative.");
            if (!float.IsFinite(metrics.OuterRadius) || metrics.OuterRadius <= metrics.InnerRadius)
                throw InvalidMetric(metrics, "OuterRadius must be finite and greater than InnerRadius.");
            if (!float.IsFinite(metrics.WedgeGap) || metrics.WedgeGap < 0f)
                throw InvalidMetric(metrics, "WedgeGap must be finite and nonnegative.");
            if (entryCount > 0 && metrics.WedgeGap >= Tau / entryCount)
                throw InvalidMetric(metrics, "WedgeGap must be smaller than one entry's angular step.");
            if (!float.IsFinite(metrics.IconSize) || metrics.IconSize < 0f)
                throw InvalidMetric(metrics, "IconSize must be finite and nonnegative.");
            if (!float.IsFinite(metrics.LabelScale) || metrics.LabelScale <= 0f)
                throw InvalidMetric(metrics, "LabelScale must be finite and greater than zero.");
            if (!float.IsFinite(metrics.DetailGap) || metrics.DetailGap < 0f)
                throw InvalidMetric(metrics, "DetailGap must be finite and nonnegative.");
            if (!float.IsFinite(metrics.FooterGap) || metrics.FooterGap < 0f)
                throw InvalidMetric(metrics, "FooterGap must be finite and nonnegative.");
            if (!float.IsFinite(metrics.FooterButtonSize.X) || metrics.FooterButtonSize.X <= 0f ||
                !float.IsFinite(metrics.FooterButtonSize.Y) || metrics.FooterButtonSize.Y <= 0f)
            {
                throw InvalidMetric(metrics, "FooterButtonSize dimensions must be finite and greater than zero.");
            }
            if (!float.IsFinite(metrics.Margin) || metrics.Margin < 0f)
                throw InvalidMetric(metrics, "Margin must be finite and nonnegative.");
            if (!float.IsFinite(metrics.BorderThickness) || metrics.BorderThickness < 0f)
                throw InvalidMetric(metrics, "BorderThickness must be finite and nonnegative.");
            if (!IsFinite(metrics.ShadowOffset))
                throw InvalidMetric(metrics, "ShadowOffset must be finite.");
            if (!float.IsFinite(metrics.SheenSpeed) || metrics.SheenSpeed < 0f)
                throw InvalidMetric(metrics, "SheenSpeed must be finite and nonnegative.");
        }

        static void ValidatePoint(Vector2 point, string parameterName)
        {
            if (!IsFinite(point))
                throw new ArgumentOutOfRangeException(parameterName, point, "Point coordinates must be finite.");
        }

        static void ValidateRectangle(Rect rectangle, string parameterName)
        {
            if (!float.IsFinite(rectangle.X) || !float.IsFinite(rectangle.Y) ||
                !float.IsFinite(rectangle.Width) || !float.IsFinite(rectangle.Height) ||
                rectangle.Width < 0f || rectangle.Height < 0f ||
                !float.IsFinite(rectangle.Right) || !float.IsFinite(rectangle.Bottom))
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    rectangle,
                    "Rectangle coordinates and dimensions must be finite, with nonnegative dimensions.");
            }
        }

        static void ValidateSafeAreaCanContain(Rect bounds, Rect safeArea, RadialMenuMetrics metrics)
        {
            float requiredWidth = bounds.Width + metrics.Margin * 2f;
            float requiredHeight = bounds.Height + metrics.Margin * 2f;
            if (!float.IsFinite(requiredWidth) || !float.IsFinite(requiredHeight) ||
                safeArea.Width < requiredWidth || safeArea.Height < requiredHeight)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(safeArea),
                    safeArea,
                    "Safe area must contain the complete radial menu plus both margins.");
            }
        }

        static ArgumentOutOfRangeException InvalidMetric(RadialMenuMetrics metrics, string message) =>
            new(nameof(metrics), metrics, message);

        static bool IsFinite(Vector2 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y);
    }
}
