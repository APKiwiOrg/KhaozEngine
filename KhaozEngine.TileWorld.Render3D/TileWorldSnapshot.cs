using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Headless RGBA8 captures of a tile world through <see cref="Render3DSnapshot"/>: an orthographic
/// top-down over a tile rect, and a perspective view from an eye toward a target. Both build a throwaway
/// <see cref="TileWorldView"/> over the captured scene, load the regions the shot can see, settle every queued
/// rebuild before the first frame, and render two frames so nothing that warms up over a frame is read back cold.
/// The goldens and the editor's render verbs both come through here, so a tool render and a golden are the same
/// code path. Needs a headless GPU device, like everything else in <see cref="Render3DSnapshot"/>.</summary>
public static class TileWorldSnapshot
{
    /// <summary>How far off vertical the top-down camera tilts, in radians. Straight down would put the view up
    /// vector parallel to the look direction and degenerate the LookAt, so the camera leans a seventeen
    /// milliradian hair, which at a two metre hill displaces the image by under four centimetres.</summary>
    public const float TopDownTiltRadians = 0.017f;

    /// <summary>The top-down camera's azimuth in radians, which decides which way the compass falls on the image.
    /// Zero puts world +x to the RIGHT and world -z UP, and since world z is minus tile z
    /// (<see cref="TileWorldSpace"/>) that is east right and NORTH UP, the pair a map reader expects. Both are
    /// had at once because (east, north, up) = (+x, -z, +y) is a right-handed triple, which is the whole reason
    /// the tile-to-world seam flips z: mapping north onto +z would make the triple left handed against a right
    /// handed render space, and a camera looking down would then have to give one of them up.</summary>
    public const float TopDownAzimuth = 0f;

    /// <summary>Metres of vertical slack added above the highest and below the lowest sampled corner before the
    /// top-down clip band is sized, so relief at the rect edges is never clipped.</summary>
    public const float TopDownHeightPadding = 4f;

    /// <summary>Metres the top-down eye sits above the tallest ground of the rect. Orthographic, so this changes
    /// the clip band and nothing about the image scale.</summary>
    public const float TopDownEyeLift = 100f;

    /// <summary>The top-down near plane in metres.</summary>
    public const float TopDownNearPlane = 0.1f;

    /// <summary>Vertical field of view in degrees of the perspective capture.</summary>
    public const float PerspectiveFieldOfViewDegrees = 60f;

    /// <summary>Chebyshev radius in REGIONS around the target's region that a perspective capture loads.</summary>
    public const int PerspectiveRegionRadius = 3;

    /// <summary>Frames every capture renders before the pixels are read back.</summary>
    public const int CaptureFrames = 2;

    /// <summary>An orthographic map shot of one plane's worth of world: <paramref name="rect"/> at
    /// <paramref name="pxPerTile"/> pixels per tile, so the image is exactly
    /// <c>rect.Width * pxPerTile</c> by <c>rect.Height * pxPerTile</c> and one tile is exactly that many pixels
    /// square at ground level (the vertical extent is set outright rather than framed, because a fit margin turns
    /// an exact scale into an approximate one). <paramref name="plane"/> chooses the plane whose corner heights
    /// size the clip band, not what is drawn: every plane of every loaded region is drawn, which is what a map
    /// view wants. The observer stands on the top plane, which no tile flags indoors, so the roof rule shows every
    /// roof. See <see cref="TopDownAzimuth"/> for which way the compass falls on the image.</summary>
    /// <param name="doc">The world to draw.</param>
    /// <param name="catalogs">The catalogs its material and archetype ids resolve through.</param>
    /// <param name="resolver">Where the object archetypes get their meshes.</param>
    /// <param name="rect">The world tile rect to cover, far edges exclusive.</param>
    /// <param name="plane">The plane whose heights size the clip band.</param>
    /// <param name="pxPerTile">Image pixels per tile, at least 1.</param>
    /// <param name="options">View knobs. Null takes the defaults with the prop draw radius widened to cover the
    /// whole rect, so a wide shot does not silently drop the props at its edges. An options object that IS passed
    /// is used exactly as given, radius included.</param>
    /// <param name="configureScene">Runs last inside the capture's setup, so a caller's lighting, post or camera
    /// changes win over everything set here.</param>
    /// <returns>The captured image as RGBA8, four bytes per pixel, row major from the top.</returns>
    public static byte[] CaptureTopDown(
        TileWorldDocument doc,
        TileWorldCatalogs catalogs,
        ITileMeshResolver resolver,
        TileRect rect,
        int plane,
        int pxPerTile,
        TileWorldViewOptions? options = null,
        Action<Scene3D>? configureScene = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(resolver);
        if (rect.IsEmpty) throw new ArgumentException("the capture rect covers no tiles.", nameof(rect));
        ArgumentOutOfRangeException.ThrowIfLessThan(pxPerTile, 1);
        if (plane < 0 || plane >= doc.PlaneCount)
            throw new ArgumentOutOfRangeException(nameof(plane), plane,
                $"the document has {doc.PlaneCount} planes, so the plane must be 0..{doc.PlaneCount - 1}.");

        int width = rect.Width * pxPerTile;
        int height = rect.Height * pxPerTile;
        float tileSize = doc.TileSize;
        (float midY, float spanY) = VerticalFrame(doc, rect, plane);
        Vector3 centre = TileWorldSpace.ToWorld(
            rect.X + rect.Width * 0.5f, midY, rect.Z + rect.Height * 0.5f, tileSize);

        TileWorldViewOptions viewOptions = options ?? TopDownDefaults(rect, tileSize);
        List<RegionCoord> regions = RegionsTouching(doc, rect);
        // The top plane is the one nothing flags indoors, so the roof rule shows every roof: a map view looks AT
        // the roofs rather than out from under one.
        var observer = new TileCoord(rect.X + rect.Width / 2, rect.Z + rect.Height / 2, Math.Max(0, doc.PlaneCount - 1));

        TileWorldView? view = null;
        return Render3DSnapshot.Capture(width, height,
            setup: scene =>
            {
                view = Build(scene, doc, catalogs, resolver, viewOptions, regions, observer);
                IsoCamera3D camera = scene.Camera;
                camera.Azimuth = TopDownAzimuth;
                camera.Elevation = MathF.PI / 2f - TopDownTiltRadians;
                camera.Target = centre;
                camera.Zoom = 1f;
                camera.AspectRatio = width / (float)height;
                // OrthoSize is the FULL vertical world extent, so the rect's depth in metres makes one tile
                // exactly pxPerTile pixels tall, and the aspect makes it exactly that wide.
                camera.OrthoSize = rect.Height * tileSize;
                camera.Distance = spanY * 0.5f + TopDownEyeLift;
                camera.NearPlane = TopDownNearPlane;
                camera.FarPlane = camera.Distance * 2f + spanY;
                configureScene?.Invoke(scene);
            },
            drawFrame: scene => view!.Draw(centre),
            frames: CaptureFrames);
    }

    /// <summary>A perspective shot from <paramref name="eye"/> toward <paramref name="target"/>, both in world
    /// metres, at <see cref="PerspectiveFieldOfViewDegrees"/> vertical field of view. Loads every region within
    /// <see cref="PerspectiveRegionRadius"/> of the target's region, which is what a game camera would have
    /// resident around its subject, and measures the prop draw radius from the target rather than from the
    /// observer, so a camera pulled back from an indoor observer still draws the props around it.</summary>
    /// <param name="doc">The world to draw.</param>
    /// <param name="catalogs">The catalogs its material and archetype ids resolve through.</param>
    /// <param name="resolver">Where the object archetypes get their meshes.</param>
    /// <param name="eye">World-metre eye position.</param>
    /// <param name="target">World-metre point the camera looks at, and the point the props are culled around.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="observer">The tile the roof rule is judged from. Null takes the tile under
    /// <paramref name="target"/> on plane 0, so a shot aimed inside a house hides that house's roof.</param>
    /// <param name="options">View knobs, or null for the defaults.</param>
    /// <param name="configureScene">Runs last inside the capture's setup, so a caller's lighting, post or camera
    /// changes win over everything set here.</param>
    /// <returns>The captured image as RGBA8, four bytes per pixel, row major from the top.</returns>
    /// <exception cref="ArgumentException">The eye and the target coincide, so the look direction is zero.</exception>
    public static byte[] CapturePerspective(
        TileWorldDocument doc,
        TileWorldCatalogs catalogs,
        ITileMeshResolver resolver,
        Vector3 eye,
        Vector3 target,
        int width,
        int height,
        TileCoord? observer = null,
        TileWorldViewOptions? options = null,
        Action<Scene3D>? configureScene = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        Vector3 look = target - eye;
        if (look.LengthSquared() < 1e-12f)
            throw new ArgumentException(
                "the eye and the target coincide, so the look direction is zero. Move the eye away from the target.",
                nameof(target));
        Vector3 unit = Vector3.Normalize(look);

        float tileSize = doc.TileSize;
        TileCoord subject = observer ?? new TileCoord(TileXAt(target.X, tileSize), TileZAt(target.Z, tileSize), 0);
        List<RegionCoord> regions = RegionsAround(doc, subject.Region, PerspectiveRegionRadius);
        TileWorldViewOptions viewOptions = options ?? new TileWorldViewOptions();

        TileWorldView? view = null;
        return Render3DSnapshot.Capture(width, height,
            setup: scene =>
            {
                view = Build(scene, doc, catalogs, resolver, viewOptions, regions, subject);
                scene.CameraOverride = new FlyCamera3D
                {
                    Position = eye,
                    // Yaw 0 with pitch 0 looks along +z, so the heading is atan2(x, z) rather than the atan2(z, x)
                    // a maths-convention angle would use.
                    Yaw = MathF.Atan2(look.X, look.Z),
                    Pitch = MathF.Asin(unit.Y),
                    FieldOfView = PerspectiveFieldOfViewDegrees * MathF.PI / 180f,
                    AspectRatio = width / (float)height,
                };
                configureScene?.Invoke(scene);
            },
            drawFrame: scene => view!.Draw(target),
            frames: CaptureFrames);
    }

    // The middle and the full spread of the rect's corner heights on one plane, padded top and bottom. Only the
    // clip band and the eye lift read this: the image scale comes from the rect alone.
    static (float Mid, float Span) VerticalFrame(TileWorldDocument doc, TileRect rect, int plane)
    {
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        // Corners, not tiles, so the far edge is included: a rect of W tiles has W+1 corner columns.
        for (int z = rect.Z; z <= rect.Z1; z++)
            for (int x = rect.X; x <= rect.X1; x++)
            {
                float h = doc.CornerHeight(x, z, plane);
                if (h < min) min = h;
                if (h > max) max = h;
            }
        return ((min + max) * 0.5f, max - min + 2f * TopDownHeightPadding);
    }

    // The default view options of a top-down, whose prop draw radius reaches the rect's far corner. A wide map
    // shot with the stock 96 m radius would drop every prop at its edges, and a snapshot that silently omits
    // content is worse than one that costs a few extra draws.
    static TileWorldViewOptions TopDownDefaults(TileRect rect, float tileSize)
    {
        float halfWidth = rect.Width * tileSize * 0.5f;
        float halfDepth = rect.Height * tileSize * 0.5f;
        float reach = MathF.Sqrt(halfWidth * halfWidth + halfDepth * halfDepth);
        var options = new TileWorldViewOptions();
        options.PropDrawRadius = MathF.Max(options.PropDrawRadius, reach);
        return options;
    }

    // Builds the view, primes it and hands it back for the draw lambda.
    //
    // Deliberately never disposed. Render3DSnapshot.Capture OWNS the Scene3D and disposes it before returning,
    // and that dispose already frees every handle this view uploaded, so a view dispose afterwards would be a
    // second free through a dead scene. Same ownership shape as the throwaway ViewportWorld the map editor's
    // RenderService builds inside its own capture.
    static TileWorldView Build(
        Scene3D scene,
        TileWorldDocument doc,
        TileWorldCatalogs catalogs,
        ITileMeshResolver resolver,
        TileWorldViewOptions options,
        IReadOnlyList<RegionCoord> regions,
        TileCoord observer)
    {
        var view = new TileWorldView(new Scene3DTileWorldScene(scene), doc, catalogs, resolver, options)
        {
            Observer = observer,
        };
        for (int i = 0; i < regions.Count; i++) view.LoadRegion(regions[i]);
        // Settle everything now rather than over frames: a capture renders two, and a rebuild the per-flush budget
        // deferred would land after the pixels had been read back.
        view.Flush(int.MaxValue);
        return view;
    }

    static List<RegionCoord> RegionsTouching(TileWorldDocument doc, TileRect rect)
    {
        RegionCoord min = RegionCoord.Of(rect.X, rect.Z);
        RegionCoord max = RegionCoord.Of(rect.X1 - 1, rect.Z1 - 1);
        var regions = new List<RegionCoord>();
        for (int rz = min.Rz; rz <= max.Rz; rz++)
            for (int rx = min.Rx; rx <= max.Rx; rx++)
                AddPresent(doc, regions, new RegionCoord(rx, rz));
        return regions;
    }

    static List<RegionCoord> RegionsAround(TileWorldDocument doc, RegionCoord centre, int radius)
    {
        var regions = new List<RegionCoord>();
        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
                AddPresent(doc, regions, centre.Offset(dx, dz));
        return regions;
    }

    // A region the document does not hold is skipped rather than loaded. Loading it would mesh to null and build
    // two empty placement lists on every plane, which buys nothing, and the perspective ring is 49 regions wide.
    static void AddPresent(TileWorldDocument doc, List<RegionCoord> into, RegionCoord region)
    {
        if (doc.GetRegion(region) is not null) into.Add(region);
    }

    // Floors rather than truncates, so a world point west or south of the origin lands in the tile that covers it
    // instead of the one on the other side of zero. Two of them because the two axes convert differently: world z
    // is minus tile z (TileWorldSpace), so one shared helper would put the z reading a tile out on one side.
    static int TileXAt(float worldX, float tileSize) => (int)MathF.Floor(TileWorldSpace.TileX(worldX, tileSize));

    static int TileZAt(float worldZ, float tileSize) => (int)MathF.Floor(TileWorldSpace.TileZ(worldZ, tileSize));
}
