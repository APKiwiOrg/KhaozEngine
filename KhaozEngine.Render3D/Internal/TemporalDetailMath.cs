namespace KhaozEngine.Render3D.Internal;

/// <summary>The integer grid and admission rules of the additional colour history. No render or history state
/// changes size with this grid.</summary>
internal static partial class TemporalDetailMath
{
    internal static (int Width, int Height)? Extent(int width, int height, uint deviceLimit)
    {
        long fineWidth = 2L * width, fineHeight = 2L * height;
        return width > 0 && height > 0 && fineWidth <= deviceLimit && fineHeight <= deviceLimit
            && fineWidth <= int.MaxValue && fineHeight <= int.MaxValue
            ? ((int)fineWidth, (int)fineHeight) : null;
    }

    /// <summary>The fine resolve's colour attachments: R, state and the four child texels of each display pixel, so
    /// the least the device must allow for Fine to be admitted.</summary>
    internal const int ResolveAttachments = 6;

    internal static TemporalHistoryDetail Mode(TemporalHistoryDetail requested, bool resolves, int width,
        int height, uint deviceLimit, long pixelLimit, int colorAttachments) => requested == TemporalHistoryDetail.Fine
            && Fallback(requested, resolves, width, height, deviceLimit, pixelLimit, colorAttachments) == TemporalHistoryDetailFallback.None
            ? TemporalHistoryDetail.Fine : TemporalHistoryDetail.Display;

    internal static TemporalHistoryDetailFallback Fallback(TemporalHistoryDetail requested, bool resolves, int width,
        int height, uint deviceLimit, long pixelLimit, int colorAttachments)
    {
        if (requested != TemporalHistoryDetail.Fine) return TemporalHistoryDetailFallback.None;
        if (!resolves) return TemporalHistoryDetailFallback.NoResolve;
        if (colorAttachments < ResolveAttachments) return TemporalHistoryDetailFallback.ColorAttachments;
        if (pixelLimit <= 0 || (long)width * height > pixelLimit) return TemporalHistoryDetailFallback.PixelBudget;
        return Extent(width, height, deviceLimit).HasValue
            ? TemporalHistoryDetailFallback.None : TemporalHistoryDetailFallback.TextureSize;
    }

    internal static (int X, int Y) DisplayPixel(int fineX, int fineY) => (fineX / 2, fineY / 2);
}
