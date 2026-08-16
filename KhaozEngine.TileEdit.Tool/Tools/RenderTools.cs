using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using KhaozEngine.TileWorld;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>Headless render verbs over the open world: an orthographic top-down map PNG and a perspective shot.
/// Each delegates to <see cref="RenderService"/> through <see cref="ToolGuard.Guard{T}"/> and returns a short
/// text block naming the framing followed by the PNG image, so a client can map image pixels back to tiles
/// before it looks at them.
///
/// <para>The two verbs speak different coordinate systems on purpose. The top-down takes a TILE rect (x east, z
/// north, far edges exclusive) and draws it north up, one tile exactly the requested number of pixels square.
/// The perspective shot takes WORLD metres, where world z is MINUS tile z: north is minus world z, and y is up.
/// So to look at tile (10, 20) from the south-east, put the target at world (10.5, 0, -20.5) and the eye at
/// (18, 8, -12).</para>
///
/// <para>Meshes are greybox boxes sized from each archetype's footprint and collision kind, so a render reads as
/// structure rather than as art. Both verbs need a real headless GPU device (Metal, D3D11 or Vulkan) and fail
/// with a precise error on a machine without one.</para></summary>
[McpServerToolType]
public sealed class RenderTools(RenderService render)
{
    /// <summary>An orthographic map shot of a tile rect.</summary>
    [McpServerTool(Name = "render_topdown"), Description("Renders an orthographic top-down PNG of one plane over a tile rect, north up and west left, exactly pxPerTile pixels per tile so the image is rect width times pxPerTile across. Optional overlays (grid, collision, objects, regions) are painted into the pixels afterwards. Returns a text block naming the rect, plane, scale and overlays, then the PNG image itself. Needs a headless GPU device.")]
    public IEnumerable<ContentBlock> RenderTopDown(
        [Description("Rect's west edge, tile x, inclusive.")] int x,
        [Description("Rect's south edge, tile z, inclusive.")] int z,
        [Description("Rect width in tiles, x + width exclusive.")] int width,
        [Description("Rect height in tiles, z + height exclusive.")] int height,
        [Description("Plane index to draw, 0 is the ground storey.")] int plane,
        [Description("Pixels per tile, at least 1. Defaults to 4, which keeps a whole 64 tile region inside a 256 pixel image.")] int pxPerTile = 4,
        [Description("Comma list of overlays to paint: grid, collision, objects, regions. Empty (the default) paints none. An unknown name fails before any GPU work.")] string overlays = "",
        [Description("Optional path to also write the PNG to. A relative path resolves against the OPEN WORLD's directory, and the directory is created. Null returns the image only.")] string? savePath = null)
        => ToolGuard.Guard(() => Blocks(render.RenderTopDown(new TileRect(x, z, width, height), plane, pxPerTile,
            overlays, savePath)));

    /// <summary>A perspective shot from an eye toward a target.</summary>
    [McpServerTool(Name = "render_view"), Description("Renders a perspective PNG from an eye point looking at a target point, both in WORLD metres, where y is up and world z is MINUS tile z (north is minus world z). To look at tile (10, 20) from the south-east, put the target at world (10.5, 0, -20.5) and the eye at (18, 8, -12). Returns a text block naming the eye, target, size and roof observer, then the PNG image itself. The eye and target must not coincide. Needs a headless GPU device.")]
    public IEnumerable<ContentBlock> RenderView(
        [Description("Eye world x in metres (east).")] float eyeX,
        [Description("Eye world y in metres (up).")] float eyeY,
        [Description("Eye world z in metres. World z is minus tile z, so a tile at z 20 sits at world z -20.")] float eyeZ,
        [Description("Target world x in metres.")] float targetX,
        [Description("Target world y in metres (up). Ground level is 0 plus whatever the corner heights lift it by.")] float targetY,
        [Description("Target world z in metres, again minus the tile z.")] float targetZ,
        [Description("Image width in pixels. Defaults to 640.")] int width = 640,
        [Description("Image height in pixels. Defaults to 480.")] int height = 480,
        [Description("Tile x the roof rule is judged from, so a shot aimed inside a building hides that building's roof. Null (with observerZ null too) uses the tile under the target.")] int? observerX = null,
        [Description("Tile z the roof rule is judged from. Must be given together with observerX. The observer stands on plane 0.")] int? observerZ = null,
        [Description("Optional path to also write the PNG to. A relative path resolves against the OPEN WORLD's directory. Null returns the image only.")] string? savePath = null)
        => ToolGuard.Guard(() => Blocks(render.RenderView(new Vector3(eyeX, eyeY, eyeZ),
            new Vector3(targetX, targetY, targetZ), width, height, Observer(observerX, observerZ), savePath)));

    // Both halves or neither. One half alone would silently fall back to the tile under the target, which reads
    // as the roof rule ignoring an argument the client did supply.
    static TileCoord? Observer(int? observerX, int? observerZ)
    {
        if (observerX is null && observerZ is null) return null;
        if (observerX is not { } ox || observerZ is not { } oz)
            throw new System.ArgumentException(
                "observerX and observerZ go together: give both to pin the roof observer, or neither to use the tile under the target.",
                nameof(observerX));
        return new TileCoord(ox, oz, 0);
    }

    // The framing text first (so the client can map pixels back to tiles), then the PNG image itself. The saved
    // path joins the framing line rather than becoming a third block, so a client reading only the text still
    // learns where the file went.
    static ContentBlock[] Blocks(RenderResult result)
    {
        string text = result.SavedPath is null ? result.Framing : result.Framing + ", saved to " + result.SavedPath;
        return new ContentBlock[]
        {
            new TextContentBlock { Text = text },
            ImageContentBlock.FromBytes(result.Png, "image/png"),
        };
    }
}
