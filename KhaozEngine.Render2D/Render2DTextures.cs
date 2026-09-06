using System;

namespace KhaozEngine.Render2D;

/// <summary>Standard small textures owned by the base Render2D package.</summary>
public static class Render2DTextures
{
    /// <summary>Creates a 1x1 opaque white texture on <paramref name="surface"/>'s device.</summary>
    public static Texture2D White(Render2DSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return surface.CreateTexture(CreateWhitePixels(), 1, 1);
    }

    /// <summary>Creates a 1x1 opaque white texture on the snapshot <paramref name="context"/>'s device.</summary>
    public static Texture2D White(Render2DContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.CreateTexture(CreateWhitePixels(), 1, 1);
    }

    internal static byte[] CreateWhitePixels() => new byte[] { 255, 255, 255, 255 };
}
