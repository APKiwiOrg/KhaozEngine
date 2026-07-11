using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KhaozEngine.MapEdit.Tools;

/// <summary>Headless render verbs over the open document: a top-down orthographic map PNG and a perspective view
/// PNG. Each is a thin wrapper that delegates to <see cref="RenderService"/> through
/// <see cref="ToolGuard.Guard{T}"/> and returns a short text block naming the framing followed by the PNG image, so
/// the client can map screen pixels back to world coordinates. Coordinates are the engine's world frame: X and Z
/// span the ground plane and Y is up, all lengths in meters, angles in degrees. A session opened without asset
/// manifests renders terrain only (no prop or building meshes). Renders need a headless GPU device (Metal, D3D11,
/// or Vulkan); on a machine without one the verb fails with a precise error.</summary>
[McpServerToolType]
public sealed class RenderTools(RenderService render)
{
    [McpServerTool(Name = "render_topdown"), Description("Renders a top-down orthographic PNG of the open map over a world rect (defaults to the document bounds). The camera looks straight down with world +Z up the image and world +X to the right. Returns a text block naming the rect, image size, and meters-per-pixel, then the PNG image. Overlays (exclusion, region, and feature fills) draw by default. Renders terrain only when no asset manifests were supplied. Needs a headless GPU device.")]
    public IEnumerable<ContentBlock> RenderTopDown(
        [Description("Minimum world X of the rect in meters. Null uses the document bounds.")] float? minX = null,
        [Description("Minimum world Z of the rect in meters. Null uses the document bounds.")] float? minZ = null,
        [Description("Maximum world X of the rect in meters. Null uses the document bounds.")] float? maxX = null,
        [Description("Maximum world Z of the rect in meters. Null uses the document bounds.")] float? maxZ = null,
        [Description("Image width in pixels. Defaults to 1024.")] int width = 1024,
        [Description("Image height in pixels. Defaults to 1024.")] int height = 1024,
        [Description("When true, draw the exclusion, region, and feature overlay fills. Defaults to true.")] bool includeOverlays = true)
        => ToolGuard.Guard(() => TopDownBlocks(minX, minZ, maxX, maxZ, width, height, includeOverlays));

    [McpServerTool(Name = "render_view"), Description("Renders a perspective PNG of the open map from an eye point looking toward a target point, with a vertical field of view. Returns a text block naming the eye, target, image size, and field of view, then the PNG image. Eye and target must not coincide. Renders terrain only when no asset manifests were supplied. Needs a headless GPU device.")]
    public IEnumerable<ContentBlock> RenderView(
        [Description("Eye world X in meters.")] float eyeX,
        [Description("Eye world Y in meters (Y is up).")] float eyeY,
        [Description("Eye world Z in meters.")] float eyeZ,
        [Description("Target world X in meters.")] float targetX,
        [Description("Target world Y in meters (Y is up).")] float targetY,
        [Description("Target world Z in meters.")] float targetZ,
        [Description("Image width in pixels. Defaults to 1024.")] int width = 1024,
        [Description("Image height in pixels. Defaults to 720.")] int height = 720,
        [Description("Vertical field of view in degrees. Defaults to 60.")] float fovDegrees = 60f)
        => ToolGuard.Guard(() => ViewBlocks(eyeX, eyeY, eyeZ, targetX, targetY, targetZ, width, height, fovDegrees));

    IEnumerable<ContentBlock> TopDownBlocks(float? minX, float? minZ, float? maxX, float? maxZ,
        int width, int height, bool includeOverlays)
    {
        byte[] png = render.RenderTopDown(minX, minZ, maxX, maxZ, width, height, includeOverlays);
        string rect = "rect x[" + Fmt(minX) + ", " + Fmt(maxX) + "] z[" + Fmt(minZ) + ", " + Fmt(maxZ)
            + "] (null = document bounds).";
        // The orthographic view has one meters-per-pixel scale, known only when the X span is explicit. It is
        // approximate because the camera frames the rect centred with a small margin, and with defaulted bounds the
        // client reads map_summary for the true rect.
        string scale = minX is { } lo && maxX is { } hi && width > 0
            ? "about " + ((hi - lo) / width).ToString("0.####", CultureInfo.InvariantCulture)
                + " meters per pixel (the rect is framed with a small margin)"
            : "meters per pixel depends on the defaulted bounds (see map_summary for the rect)";
        string text = "top-down orthographic render. " + rect + " image " + Size(width, height) + ", " + scale + ".";
        return Blocks(text, png);
    }

    IEnumerable<ContentBlock> ViewBlocks(float eyeX, float eyeY, float eyeZ,
        float targetX, float targetY, float targetZ, int width, int height, float fovDegrees)
    {
        byte[] png = render.RenderView(eyeX, eyeY, eyeZ, targetX, targetY, targetZ, width, height, fovDegrees);
        string text = "perspective render. eye (" + Fmt(eyeX) + ", " + Fmt(eyeY) + ", " + Fmt(eyeZ)
            + ") target (" + Fmt(targetX) + ", " + Fmt(targetY) + ", " + Fmt(targetZ) + ") image "
            + Size(width, height) + ", fov " + Fmt(fovDegrees) + " degrees.";
        return Blocks(text, png);
    }

    // The framing text first (so the client can map pixels to world coordinates), then the PNG image itself.
    static ContentBlock[] Blocks(string text, byte[] png) => new ContentBlock[]
    {
        new TextContentBlock { Text = text },
        ImageContentBlock.FromBytes(png, "image/png"),
    };

    static string Size(int width, int height) =>
        width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture) + " px";

    static string Fmt(float? v) => v is { } f ? Fmt(f) : "null";
    static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
