using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEdit;

/// <summary>Headless PNG renders of the session's open document: a top-down orthographic map view and a
/// perspective view from an eye toward a target. Both build a throwaway <see cref="ViewportWorld"/> inside the
/// only public headless render entry (<see cref="Render3DSnapshot"/>), draw the streamed terrain plus
/// scatter, authored placements, spawn markers, and water at full visibility, and encode the captured RGBA to a
/// PNG. The top-down view also paints the exclusion, region, and feature overlays. Nothing is written to disk: the
/// verbs hand the PNG bytes straight back as an MCP image block. A session without asset manifests renders
/// terrain-only (the world tolerates unknown kinds and simply loads no meshes). When no headless GPU device can be
/// created the render fails with a precise <see cref="InvalidOperationException"/> the adapter turns into a clean
/// client error.</summary>
public sealed class RenderService(MapEditSession session)
{
    // Straight-down would degenerate the LookAt (view up parallel to forward), so tip the top-down camera a
    // seventeen-milliradian hair off vertical, matching the editor's own top view.
    const float TopDownElevation = MathF.PI / 2f - 0.017f;

    // The vertical slack (metres) added above the tallest and below the lowest sampled ground so the orthographic
    // frame never clips terrain relief at the rect edges.
    const float HeightPadding = 10f;

    // The streaming focus is lifted this far (metres) above the sampled ground at the rect/bounds centre so the
    // primed ring is centred on the zone the camera looks at, not buried in the terrain.
    const float FocusLift = 2f;

    /// <summary>Top-down orthographic PNG over the world rect (defaulting to the document bounds). The camera looks
    /// straight down with azimuth zero, so world +Z runs up the image and world +X runs to the right.
    /// <paramref name="includeOverlays"/> paints the exclusion, region, and feature fills over the terrain.
    /// <paramref name="textured"/> mirrors the editor's TexturedProps toggle: true (the default) renders a manifest
    /// entry's textured parts when it declares them, false renders every prop flattened regardless of the manifest
    /// flag. Throws <see cref="InvalidOperationException"/> when no document is open or no headless GPU device
    /// exists.</summary>
    public byte[] RenderTopDown(float? minX = null, float? minZ = null, float? maxX = null, float? maxZ = null,
        int width = 1024, int height = 1024, bool includeOverlays = true, bool textured = true)
    {
        return session.WithDocument((doc, registry) =>
        {
            TerrainField field = session.Field();
            MapBounds b = doc.Bounds;
            float rMinX = minX ?? b.MinX;
            float rMinZ = minZ ?? b.MinZ;
            float rMaxX = maxX ?? b.MaxX;
            float rMaxZ = maxZ ?? b.MaxZ;

            float cx = (rMinX + rMaxX) * 0.5f;
            float cz = (rMinZ + rMaxZ) * 0.5f;
            float rectWidth = rMaxX - rMinX;
            float rectDepth = rMaxZ - rMinZ;
            (float midHeight, float heightSpan) = VerticalFrame(field, rMinX, rMinZ, rMaxX, rMaxZ, cx, cz);

            var visibility = new EditorVisibility();
            var focus = new Vector3(cx, field.SampleHeight(cx, cz) + FocusLift, cz);

            ViewportWorld? world = null;
            return CaptureToPng(width, height,
                setup: scene =>
                {
                    world = ConfigureWorld(scene, textured);
                    world.Build(doc, registry);
                    IsoCamera3D cam = scene.Camera;
                    cam.Elevation = TopDownElevation;
                    cam.Azimuth = 0f;
                    cam.AspectRatio = width / (float)height;
                    cam.Frame(new Vector3(cx, midHeight, cz), new Vector3(rectWidth, heightSpan, rectDepth),
                        margin: 1.05f);
                },
                drawFrame: scene =>
                {
                    world!.Update(focus, 0f);
                    world.Draw(focus, selectedPlacementId: null, highlightTint: default, visibility);
                    if (includeOverlays) DrawOverlays(scene, doc, field, visibility);
                });
        });
    }

    /// <summary>Perspective PNG from <paramref name="eyeX"/>,<paramref name="eyeY"/>,<paramref name="eyeZ"/> looking
    /// toward the target point, with a vertical field of view of <paramref name="fovDegrees"/>. Rejects a
    /// zero-length look direction (eye and target coincide) with an <see cref="ArgumentException"/> before any GPU
    /// work. <paramref name="textured"/> mirrors the editor's TexturedProps toggle: true (the default) renders a
    /// manifest entry's textured parts when it declares them, false renders every prop flattened regardless of the
    /// manifest flag. Throws <see cref="InvalidOperationException"/> when no document is open or no headless GPU
    /// device exists.</summary>
    public byte[] RenderView(float eyeX, float eyeY, float eyeZ,
        float targetX, float targetY, float targetZ,
        int width = 1024, int height = 720, float fovDegrees = 60f, bool textured = true)
    {
        var eye = new Vector3(eyeX, eyeY, eyeZ);
        var target = new Vector3(targetX, targetY, targetZ);
        Vector3 d = target - eye;
        if (d.LengthSquared() < 1e-12f)
            throw new ArgumentException(
                "render_view eye and target coincide, so the look direction is zero. Move the eye away from the target.");
        Vector3 dNorm = Vector3.Normalize(d);

        return session.WithDocument((doc, registry) =>
        {
            TerrainField field = session.Field();
            MapBounds b = doc.Bounds;
            float bx = (b.MinX + b.MaxX) * 0.5f;
            float bz = (b.MinZ + b.MaxZ) * 0.5f;

            var visibility = new EditorVisibility();
            var focus = new Vector3(bx, field.SampleHeight(bx, bz) + FocusLift, bz);

            ViewportWorld? world = null;
            return CaptureToPng(width, height,
                setup: scene =>
                {
                    world = ConfigureWorld(scene, textured);
                    world.Build(doc, registry);
                    scene.CameraOverride = new FlyCamera3D
                    {
                        Position = eye,
                        Yaw = MathF.Atan2(d.X, d.Z),
                        Pitch = MathF.Asin(dNorm.Y),
                        FieldOfView = fovDegrees * MathF.PI / 180f,
                        AspectRatio = width / (float)height,
                    };
                },
                drawFrame: scene =>
                {
                    world!.Update(focus, 0f);
                    world.Draw(focus, selectedPlacementId: null, highlightTint: default, visibility);
                });
        });
    }

    /// <summary>Constructs the throwaway <see cref="ViewportWorld"/> a render uses and wires the MCP
    /// <c>textured</c> parameter into its <see cref="ViewportWorld.TexturedPropsEnabled"/> BEFORE the caller runs
    /// <see cref="ViewportWorld.Build"/> (Build is the only GPU-touching step of the two), so a test can pin that
    /// <see cref="RenderTopDown"/> / <see cref="RenderView"/> thread the parameter through without a headless GPU
    /// device.</summary>
    public ViewportWorld ConfigureWorld(Scene3D scene, bool textured) =>
        new(scene, session.ManifestPaths) { TexturedPropsEnabled = () => textured };

    // Runs the headless capture and encodes it to a PNG, mapping any capture failure (no device, no driver) to a
    // precise InvalidOperationException naming the selected backend so the client learns why the render failed and
    // how to fix it. Two frames so the streamer settles before the pixels are read back.
    //
    // The ViewportWorld built inside setup is deliberately NOT disposed by the caller: Render3DSnapshot.Capture
    // owns the Scene3D and disposes it before returning, and that Scene3D.Dispose already frees every GPU resource
    // the world allocated (its kit meshes, the streamed terrain-chunk meshes, and the splat material). Disposing the
    // world afterwards would run its teardown through the already-disposed scene, whose splat-material list Dispose
    // has cleared, and throw. Nothing the world holds outlives the scene, so leaving teardown to the scene is
    // leak-free. (An engine-side guard on Scene3D.UnloadSplatMaterial would let the world be disposed explicitly.)
    static byte[] CaptureToPng(int width, int height, Action<Scene3D> setup, Action<Scene3D> drawFrame)
    {
        byte[] rgba;
        try
        {
            rgba = Render3DSnapshot.Capture(width, height, setup, drawFrame, frames: 2);
        }
        catch (Exception ex)
        {
            GpuBackendKind selected = GpuBackendSelector.Select();
            throw new InvalidOperationException("render failed, no headless GPU device available (backend "
                + selected + "). Set KE_GRAPHICS_BACKEND or run on a machine with Metal, D3D11, or Vulkan. Details: "
                + ex.Message);
        }
        return PngWriter.Encode(rgba, width, height);
    }

    // Mirrors MapEditorScene.DrawOverlays: turn the document's exclusions, regions, and features into the pure
    // overlay draw-list (nothing selected, everything visible), then submit each entry through the matching
    // Scene3D debug-fill primitive. Kept identical to the editor's per-kind submission so a render reads like the
    // live viewport.
    static void DrawOverlays(Scene3D scene, MapDocument doc, TerrainField field, EditorVisibility visibility)
    {
        foreach (OverlayDraw o in MapEditorScene.ComputeOverlayDrawList(
                     doc, new EditorSelection(), field.SampleHeight, showOverlays: true, visibility))
        {
            switch (o.Shape)
            {
                case OverlayShape.Disc:
                    scene.DebugFilledCircle(o.Center, Vector3.UnitY, o.Radius, o.Color);
                    break;
                case OverlayShape.Rect:
                    scene.DebugFilledQuad(o.Center, o.HalfExtents, o.Color);
                    break;
                case OverlayShape.Polygon:
                    if (o.Rim is { Count: >= 3 } rim) scene.DebugFilledFan(o.Center, rim, o.Color);
                    break;
                default:
                    break;
            }
        }
    }

    // Samples the rect's four corners plus its centre and pads the min/max ground heights, returning the midpoint
    // height (the camera target's Y) and the padded vertical span the orthographic frame must cover so relief at
    // the rect edges never clips.
    static (float MidHeight, float HeightSpan) VerticalFrame(TerrainField field,
        float minX, float minZ, float maxX, float maxZ, float cx, float cz)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        ReadOnlySpan<(float X, float Z)> points =
        [
            (minX, minZ), (maxX, minZ), (minX, maxZ), (maxX, maxZ), (cx, cz),
        ];
        foreach ((float x, float z) in points)
        {
            float h = field.SampleHeight(x, z);
            lo = MathF.Min(lo, h);
            hi = MathF.Max(hi, h);
        }
        lo -= HeightPadding;
        hi += HeightPadding;
        return ((lo + hi) * 0.5f, hi - lo);
    }
}
