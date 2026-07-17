using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Telegraphs;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The shared "floating island with an overhanging range ring" scene behind every void-fallback check: the
    /// committed golden (<c>GoldenSnapshotTests.Golden3D_TelegraphGroundVoid</c>), the raw-pixel A/B invariant
    /// (<c>GroundDecalVoidGoldenTests</c>), and the human-reviewed showcase dumps
    /// (<c>TelegraphVoidShowcaseGpuTests</c>) all render THIS, so no scene tweak can leave one of them silently
    /// describing a different picture. This is Hardpoint's actual case reduced to primitives: a mesa in the void with
    /// a tower's range ring overhanging its edge.
    /// <para>
    /// THE GEOMETRY IS LOAD-BEARING, so it is derived here rather than eyeballed. The island is a
    /// <see cref="MeshPrimitives.Tile"/>, footprint <see cref="TileSize"/> square with its top (the decal's plane) at
    /// y = <see cref="TileThickness"/> and four vertical cliff faces of that height. The default
    /// <see cref="IsoCamera3D"/> looks from (+x, +y, +z) at azimuth 45 and elevation atan(0.5), so its ray drops
    /// <see cref="RayDyDx"/> in y per unit of -x, and the +X and +Z cliffs are the CAMERA-FACING ones. That fixes
    /// three regions along the +X axis, and <see cref="VoidSample"/> / <see cref="GroundSample"/> /
    /// <see cref="CliffFrontSample"/> pick one pixel in each:
    /// </para>
    /// <list type="bullet">
    /// <item>plane x in [-3, 3] (and |z| &lt;= 3): the tile top. Real geometry inside the Y band, painted by the base
    /// pass exactly as it always was.</item>
    /// <item>plane x in (3, <see cref="CliffEndX"/>): the pixel shows the +X CLIFF, whose reconstructed world y falls
    /// away from the plane and, past <see cref="StripStartX"/>, below the Y gate. The plane point is nonetheless
    /// NEARER than that cliff (it hangs over the void at the top's height, the cliff recedes below and behind it), so
    /// the geometry pass's fallback paints it. Getting this region wrong is what makes an overhanging ring lose most
    /// of its near arc to the cliff's screen band.</item>
    /// <item>plane x &gt; <see cref="CliffEndX"/>: true background past the island's silhouette. The background pass
    /// paints the plane projection.</item>
    /// </list>
    /// <para>
    /// The ring band spans <see cref="RingInner"/>..<see cref="RingOuter"/>, chosen so a single frame shows all three
    /// at once: on the ground toward the near corner, crossing in front of the near cliffs, and continuing over the
    /// void on the far side (whose cliffs face away and occlude nothing). The correct result is an UNBROKEN annulus.
    /// </para>
    /// <para>
    /// <see cref="WallTransform"/> is the opt-in mirror case: a slab standing ON the plane, out where the ring overhangs, so
    /// the ring's projection is genuinely BEHIND it and must stay hidden. The two together pin both signs of the
    /// depth comparison - paint what is in front, refuse to x-ray what is behind.
    /// </para>
    /// </summary>
    internal static class VoidDecalScene
    {
        public const int W = 640, H = 480;

        public const float TileSize = 6f;
        public const float TileThickness = 1f;
        /// <summary>The tile's top surface, and therefore the decal's own plane (GroundDecal.Center.Y).</summary>
        public const float PlaneY = TileThickness;
        /// <summary>Half the tile footprint: the plane-space distance from the centre to each edge, on axis.</summary>
        public const float TileHalf = TileSize * 0.5f;

        public const float RingInner = 3.6f;
        public const float RingOuter = 5.0f;

        public const float YTolerance = 0.3f;   // GroundTelegraphs.DefaultYTolerance, mirrored so the math below is checkable

        /// <summary>How far the iso camera's ray falls in y per unit travelled in -x: sin(E) / (cos(E) * sin(A)) at
        /// the default azimuth 45 / elevation atan(0.5). Pinned by a test against the real camera.</summary>
        public const float RayDyDx = 0.70710678f;

        /// <summary>Plane x past which a pixel is no longer showing the +X cliff, i.e. where true background begins.
        /// The cliff is <see cref="TileThickness"/> tall and the ray falls <see cref="RayDyDx"/> per unit x.</summary>
        public const float CliffEndX = TileHalf + TileThickness / RayDyDx;          // 3 + 1.414 = 4.414

        /// <summary>Plane x past which the cliff pixel's reconstructed world y has fallen below the decal's Y gate, so
        /// the surface stops being the decal's ground and the fallback (rather than the conforming path) is what has
        /// to carry it. Between <see cref="TileHalf"/> and here the gate's tolerance still wraps the decal a little
        /// way down the cliff.</summary>
        public const float StripStartX = TileHalf + YTolerance / RayDyDx;            // 3 + 0.424 = 3.424

        public static readonly Vector3 Center = new(0f, PlaneY, 0f);

        /// <summary>Far side (-X), mid-band, no geometry between it and the eye: the void pass MUST paint here when
        /// flagged, and nothing may paint here when not.</summary>
        public static readonly Vector3 VoidSample = new(-4.3f, PlaneY, 0f);

        /// <summary>Near corner (azimuth 45, radius 3.8): on the tile top AND inside the ring band. The base pass
        /// paints it; the flag must not change one byte here.</summary>
        public static readonly Vector3 GroundSample = new(2.687f, PlaneY, 2.687f);

        /// <summary>Near edge (+X, plane x = 4.0), inside (<see cref="StripStartX"/>, <see cref="CliffEndX"/>): the
        /// pixel shows the camera-facing cliff, which is out of the Y band AND further along the ray than the plane.
        /// The ring hangs in FRONT of that cliff, so the geometry pass's fallback must paint it. This is the sample
        /// that fails if the fallback treats "geometry exists" as "do not project".</summary>
        public static readonly Vector3 CliffFrontSample = new(4.0f, PlaneY, 0f);

        /// <summary>A static, solid, alpha-blended ring style: no sweep, no flash, no pulse, no noise, so the render
        /// is a clean annulus and every pixel assertion is about the void projection rather than an animation phase.
        /// DangerColor matches FillColor so the colour is progress-independent even if a ramp is ever enabled.</summary>
        public static TelegraphStyle Style(bool voidFallback, float voidDim = 0f) => new()
        {
            FillColor = new Color(0.35f, 0.75f, 1f, 0.75f),
            OutlineColor = new Color(0.80f, 0.95f, 1f, 0.95f),
            DangerColor = new Color(0.35f, 0.75f, 1f, 0.75f),
            EdgeThickness = 2f,
            Opacity = 1f,
            FillMode = FillMode.OutlineAndFill,
            Animation = TelegraphAnim.None,
            Blend = TelegraphBlend.Alpha,
            Pattern = TelegraphFillPattern.Solid,
            EdgeWidthWorld = 0.08f,
            VoidFallback = voidFallback,
            VoidDim = voidDim,
        };

        /// <summary>Frame the full ring (outer radius 5) plus the island, with room for the void projection around it.</summary>
        static readonly Vector3 FrameCenter = new(0f, 0.5f, 0f);
        static readonly Vector3 FrameSize = new(13f, 3f, 13f);

        public static void ConfigureCamera(IsoCamera3D cam) => cam.Frame(FrameCenter, FrameSize);

        /// <summary>
        /// A CPU-side copy of the camera the render path actually ends up with, for projecting a world sample to its
        /// pixel. The ORDER matters and is the whole reason this is not just <c>new IsoCamera3D()</c> plus a Frame:
        /// <see cref="Scene3D"/> assigns <see cref="IsoCamera3D.AspectRatio"/> from the viewport at RENDER time, i.e.
        /// after setup ran, and <see cref="IsoCamera3D.Frame"/> reads the aspect it sees to size the ortho volume. So
        /// Frame runs at the camera's default aspect and the real one lands afterwards, and reproducing that sequence
        /// is what makes WorldToScreen agree with the rendered image.
        /// </summary>
        public static IsoCamera3D ProjectionCamera()
        {
            var cam = new IsoCamera3D();
            ConfigureCamera(cam);
            cam.AspectRatio = (float)W / H;
            return cam;
        }

        /// <summary>Load the island and pin the camera + post state. Solid background (not the default starfield): the
        /// pixel assertions are about the decal, and a star landing on a sample pixel would be noise in the signal.
        /// The showcase dumps a starfield variant separately, where the compositing IS the point.</summary>
        public static MeshHandle Setup(Scene3D s)
        {
            s.Post.Background = BackgroundMode.Solid;
            s.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.09f, 1f);
            s.Post.Outline = false;
            ConfigureCamera(s.Camera);
            s.EffectTimeSeconds = 0f;   // frozen, like every other golden: determinism across bakes and backends
            return s.LoadMesh(MeshPrimitives.Tile(TileSize, TileThickness));
        }

        /// <summary>A slab standing ON the plane at +X (x in [4.0, 5.2], y in [1, 4]), out where the ring overhangs.
        /// It is deliberately THICK in x: the sample ray must exit its +X face WELL above the decal's Y gate
        /// (gateHi = PlaneY + MaxStep = 1.5), or the legacy in-band path paints the decal onto the wall's foot and the
        /// test measures that instead of the thing it claims to. Opt-in via <see cref="Draw"/>.</summary>
        public static readonly Matrix4x4 WallTransform =
            Matrix4x4.CreateScale(1.2f, 3f, 5f) * Matrix4x4.CreateTranslation(4.6f, PlaneY + 1.5f, 0f);

        /// <summary>A point on the plane BEHIND the wall from the eye (the eye is at +X, so "behind" means a SMALLER
        /// x than the slab). Inside the ring band, and in the cliff region, so WITHOUT the wall it is painted by the
        /// fallback - which makes this a clean A/B: adding an occluder in front must take it away again. The ray from
        /// here to the eye exits the wall's +X face at y ~= 1.99, above the Y gate, so the legacy band path cannot
        /// paint it either.</summary>
        public static readonly Vector3 BehindWallSample = new(3.8f, PlaneY, 0f);

        /// <summary>Draw the island + the overhanging ring, optionally with the occluding wall. A dark island tint
        /// keeps the pale ring readable on both the lit top and the shaded cliffs.
        /// <para>
        /// <paramref name="yTolerance"/> overrides the decal's downward gate tolerance (<c>GroundTelegraphs</c>
        /// hardcodes 0.3). It exists for one test: the cliff face is 1 tall, so its TOP 0.3 falls inside the stock
        /// band and the legacy path conforms the decal down it. With the normal gate rejecting vertical faces, the
        /// tolerance must stop mattering on a cliff at all, and comparing 0.3 against 0 is how that is proved.
        /// </para></summary>
        public static void Draw(Scene3D s, MeshHandle island, bool voidFallback, float voidDim = 0f,
            MeshHandle? wall = null, float? yTolerance = null)
        {
            s.Draw(island, Matrix4x4.Identity, new Color(0.16f, 0.17f, 0.20f, 1f));
            if (wall is { } w) s.Draw(w, WallTransform, new Color(0.55f, 0.25f, 0.20f, 1f));
            var d = GroundTelegraphs.BuildRing(Center, RingInner, RingOuter, 1f, Style(voidFallback, voidDim));
            if (yTolerance is { } yt) d.YTolerance = yt;
            s.DrawGroundDecal(d);
        }

        /// <summary>Load the wall mesh (a unit box scaled by <see cref="WallTransform"/>).</summary>
        public static MeshHandle LoadWall(Scene3D s) => s.LoadMesh(MeshPrimitives.Box(1f));
    }
}
