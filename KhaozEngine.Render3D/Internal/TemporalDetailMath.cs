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
        int height, uint deviceLimit, long pixelLimit, int colorAttachments) => requested == TemporalHistoryDetail.Fine && resolves
            && colorAttachments >= ResolveAttachments && pixelLimit > 0 && (long)width * height <= pixelLimit && Extent(width, height, deviceLimit).HasValue
            ? TemporalHistoryDetail.Fine : TemporalHistoryDetail.Display;

    internal static (int X, int Y) DisplayPixel(int fineX, int fineY) => (fineX / 2, fineY / 2);
}
