using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using KhaozEngine.Imaging;
using KhaozEngine.TileWorld;

namespace KhaozEngine.TileEdit;

/// <summary>Headless PNG renders of the session's open world: an orthographic top-down over a tile rect, north
/// up, and a perspective shot from an eye toward a target. Both go through <see cref="TileWorldSnapshot"/>, which
/// is the same code path the render goldens take, so what a client is shown is what the engine draws.
///
/// <para>Meshes come from <see cref="GreyboxMeshResolver"/>: one procedural box per archetype, sized from its
/// footprint and shaped by its collision kind. A resolver that loads a game's real glb by mesh reference is a
/// later round's work, so a render here reads as structure rather than as art.</para>
///
/// <para>The capture runs INSIDE <see cref="TileEditSession.Read{T}"/>, so the world cannot move under a shot
/// that takes a second, and the overlays are painted into the captured buffer before it is encoded.</para></summary>
public sealed class RenderService(TileEditSession session)
{
    /// <summary>An orthographic map shot of one plane's worth of world at <paramref name="pxPerTile"/> pixels
    /// per tile, so the image is exactly the rect's tiles across and down and one tile is that many pixels
    /// square. <paramref name="overlays"/> is a comma list of <see cref="TopDownOverlayPainter.OverlayNames"/>,
    /// painted into the captured pixels. When <paramref name="savePath"/> is given the PNG is written there too,
    /// creating the directory, and a relative path resolves against the world's own directory.</summary>
    /// <exception cref="ArgumentException">An overlay name is not known, or the rect covers no tiles.</exception>
    public RenderResult RenderTopDown(TileRect rect, int plane, int pxPerTile, string? overlays = null,
        string? savePath = null)
    {
        // Parsed before anything else, so a typo in the overlay list costs nothing rather than a whole capture.
        IReadOnlyList<string> names = TopDownOverlayPainter.Parse(overlays);
        ArgumentOutOfRangeException.ThrowIfLessThan(pxPerTile, 1);
        if (rect.IsEmpty)
            throw new ArgumentException(
                $"the rect ({rect.X}, {rect.Z}, {rect.Width}, {rect.Height}) covers nothing to render.", nameof(rect));

        int width = rect.Width * pxPerTile;
        int height = rect.Height * pxPerTile;
        byte[] rgba = session.Read(e =>
        {
            byte[] captured = TileWorldSnapshot.CaptureTopDown(e.Document, e.Catalogs,
                new GreyboxMeshResolver(e.Document.TileSize, e.Document.PlaneHeight), rect, plane, pxPerTile);
            TopDownOverlayPainter.Paint(captured, width, height, rect, plane, pxPerTile, e.Document, e.Collision, names);
            return captured;
        });

        byte[] png = PngWriter.Encode(rgba, width, height);
        string framing = string.Create(CultureInfo.InvariantCulture,
            $"top-down, rect ({rect.X}, {rect.Z}, {rect.Width}, {rect.Height}) plane {plane}, {pxPerTile} px/tile, north up, image ({width} x {height})");
        if (names.Count > 0) framing += ", overlays " + string.Join(",", names);
        return new RenderResult(png, framing, width, height, Save(savePath, png));
    }

    /// <summary>A perspective shot from <paramref name="eye"/> toward <paramref name="target"/>, both in WORLD
    /// metres (world z is minus tile z, see <see cref="TileWorldSpace"/>). <paramref name="observer"/> is the
    /// tile the roof rule is judged from, so a shot aimed inside a building hides that building's roof. Null
    /// takes the tile under the target on plane 0. When <paramref name="savePath"/> is given the PNG is written
    /// there too.</summary>
    /// <exception cref="ArgumentException">The eye and the target coincide, so the look direction is zero.</exception>
    public RenderResult RenderView(Vector3 eye, Vector3 target, int width, int height, TileCoord? observer = null,
        string? savePath = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if ((target - eye).LengthSquared() < 1e-12f)
            throw new ArgumentException(
                "the eye and the target coincide, so the look direction is zero. Move the eye away from the target.",
                nameof(target));

        byte[] rgba = session.Read(e => TileWorldSnapshot.CapturePerspective(e.Document, e.Catalogs,
            new GreyboxMeshResolver(e.Document.TileSize, e.Document.PlaneHeight), eye, target, width, height, observer));

        byte[] png = PngWriter.Encode(rgba, width, height);
        string from = string.Create(CultureInfo.InvariantCulture, $"({eye.X}, {eye.Y}, {eye.Z})");
        string to = string.Create(CultureInfo.InvariantCulture, $"({target.X}, {target.Y}, {target.Z})");
        string who = observer is TileCoord o ? o.ToString() : "the tile under the target";
        string framing = string.Create(CultureInfo.InvariantCulture,
            $"perspective from {from} to {to}, {width} x {height}, observer {who}");
        return new RenderResult(png, framing, width, height, Save(savePath, png));
    }

    // Writes the PNG when a path was asked for and hands back where it landed, creating the directory first. A
    // relative path follows the same rule as every other path the tool takes: against the world's directory.
    string? Save(string? savePath, byte[] png)
    {
        if (string.IsNullOrWhiteSpace(savePath)) return null;
        string resolved = session.ResolvePath(savePath);
        string? parent = Path.GetDirectoryName(Path.GetFullPath(resolved));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.WriteAllBytes(resolved, png);
        return resolved;
    }
}
