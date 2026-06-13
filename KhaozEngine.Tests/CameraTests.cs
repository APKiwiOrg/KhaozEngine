using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace KhaozEngine.Tests;

public class CameraTests
{
    private const int W = 800;
    private const int H = 600;
    private static Viewport Vp => new Viewport(0, 0, W, H);
    private const float Tol = 1e-3f;

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol)
    {
        Assert.True(Vector2.Distance(expected, actual) <= tol,
            $"expected {expected}, got {actual}");
    }

    [Fact]
    public void WorldOrigin_AtDefaults_MapsToViewportCenter()
    {
        var camera = new Camera2D();
        AssertClose(new Vector2(W / 2f, H / 2f), camera.WorldToScreen(Vector2.Zero, Vp));
    }

    [Fact]
    public void NoArgOverloads_WithUnsetViewport_UseZeroSizeViewport()
    {
        // Viewport defaults to default(Viewport) (zero size): the no-arg overloads center on
        // (0,0), so world origin maps to screen (0,0). Documents the "set Viewport first" footgun.
        var camera = new Camera2D();
        AssertClose(Vector2.Zero, camera.WorldToScreen(Vector2.Zero));

        camera.Viewport = Vp;   // once set, the no-arg path matches the per-call path
        AssertClose(camera.WorldToScreen(Vector2.Zero, Vp), camera.WorldToScreen(Vector2.Zero));
    }

    [Theory]
    [InlineData(123f, -45f, 1f, 0f)]
    [InlineData(-10f, 200f, 2.5f, 0.7f)]
    [InlineData(500f, 500f, 0.4f, -1.2f)]
    public void Position_AlwaysMapsToViewportCenter(float px, float py, float zoom, float rot)
    {
        var camera = new Camera2D
        {
            Position = new Vector2(px, py),
            Zoom = zoom,
            Rotation = rot,
        };
        AssertClose(new Vector2(W / 2f, H / 2f), camera.WorldToScreen(camera.Position, Vp));
    }

    [Theory]
    [InlineData(0f, 0f, 1f, 0f)]
    [InlineData(123f, -45f, 2.5f, 0.7f)]
    [InlineData(-10f, 200f, 0.4f, -1.2f)]
    public void ScreenToWorld_IsInverseOfWorldToScreen(float px, float py, float zoom, float rot)
    {
        var camera = new Camera2D
        {
            Position = new Vector2(px, py),
            Zoom = zoom,
            Rotation = rot,
        };
        foreach (var p in new[]
        {
            new Vector2(0f, 0f), new Vector2(50f, 120f),
            new Vector2(-300f, 80f), new Vector2(640f, 400f),
        })
        {
            var screen = camera.WorldToScreen(p, Vp);
            AssertClose(p, camera.ScreenToWorld(screen, Vp));
        }
    }

    [Fact]
    public void Zoom_ScalesWorldOffsetFromCenter()
    {
        var camera = new Camera2D { Position = Vector2.Zero, Zoom = 2f };
        // World (10,0) is 10 units right of Position; at zoom 2 that is 20 px right of center.
        AssertClose(new Vector2(W / 2f + 20f, H / 2f), camera.WorldToScreen(new Vector2(10f, 0f), Vp));
    }

    [Fact]
    public void Rotation_QuarterTurn_MapsWorldXOffsetToScreenYOffset()
    {
        var camera = new Camera2D { Position = Vector2.Zero, Zoom = 1f, Rotation = MathHelper.PiOver2 };
        // A +X world offset, rotated +90deg CCW under MonoGame's screen-space transform,
        // lands on the +Y screen axis (below center). Pins rotation direction + matrix fold.
        var screen = camera.WorldToScreen(new Vector2(10f, 0f), Vp);
        AssertClose(new Vector2(W / 2f, H / 2f + 10f), screen);
    }

    [Fact]
    public void NoArgOverloads_UseViewportProperty_AndMatchPerCall()
    {
        var camera = new Camera2D
        {
            Position = new Vector2(40f, -15f),
            Zoom = 1.3f,
            Rotation = 0.5f,
            Viewport = Vp,
        };

        Assert.Equal(camera.GetViewMatrix(Vp), camera.GetViewMatrix());

        var world = new Vector2(77f, 12f);
        AssertClose(camera.WorldToScreen(world, Vp), camera.WorldToScreen(world));

        var screen = new Vector2(300f, 220f);
        AssertClose(camera.ScreenToWorld(screen, Vp), camera.ScreenToWorld(screen));
    }

    [Fact]
    public void ClampPosition_WorldLargerThanView_ClampsToEdges()
    {
        var camera = new Camera2D { Zoom = 1f };
        var bounds = new Rectangle(0, 0, 1000, 1000); // halfW=400 -> X in [400,600], halfH=300 -> Y in [300,700]

        // Far past top-left -> clamps to (Left+halfW, Top+halfH).
        AssertClose(new Vector2(400f, 300f), camera.ClampPosition(new Vector2(-500f, -500f), bounds, Vp));
        // Far past bottom-right -> clamps to (Right-halfW, Bottom-halfH).
        AssertClose(new Vector2(600f, 700f), camera.ClampPosition(new Vector2(5000f, 5000f), bounds, Vp));
        // Already inside -> unchanged.
        AssertClose(new Vector2(500f, 500f), camera.ClampPosition(new Vector2(500f, 500f), bounds, Vp));
    }

    [Fact]
    public void ClampPosition_WorldSmallerThanViewOnAxis_CentersThatAxis()
    {
        var camera = new Camera2D { Zoom = 1f };
        // World 200 wide (< 800 view) but 2000 tall (> 600 view).
        var bounds = new Rectangle(0, 0, 200, 2000); // X centers at 100; Y halfH=300 -> [300,1700]
        var result = camera.ClampPosition(new Vector2(9999f, 9999f), bounds, Vp);
        AssertClose(new Vector2(100f, 1700f), result);
    }

    [Fact]
    public void ClampPosition_IsZoomAware()
    {
        var bounds = new Rectangle(0, 0, 1000, 1000);
        var desired = new Vector2(-500f, 500f); // past the left edge

        // Zoom 1: halfW=400 -> X clamps to 400.
        var z1 = new Camera2D { Zoom = 1f };
        Assert.Equal(400f, z1.ClampPosition(desired, bounds, Vp).X, 3);

        // Zoom 2: halfW=200 -> X clamps to 200 (less margin needed when zoomed in).
        var z2 = new Camera2D { Zoom = 2f };
        Assert.Equal(200f, z2.ClampPosition(desired, bounds, Vp).X, 3);
    }

    [Fact]
    public void ClampPosition_NoArgOverload_UsesViewportProperty()
    {
        var camera = new Camera2D { Zoom = 1f, Viewport = Vp };
        var bounds = new Rectangle(0, 0, 1000, 1000);
        var desired = new Vector2(-500f, 5000f);
        AssertClose(camera.ClampPosition(desired, bounds, Vp), camera.ClampPosition(desired, bounds));
    }

    [Fact]
    public void InsetViewport_HonorsOffset_MapsPositionToInsetCenter()
    {
        var camera = new Camera2D { Position = new Vector2(50f, 60f), Zoom = 1f };
        var inset = new Viewport(300, 200, 400, 300);   // center = (300+200, 200+150) = (500, 350)
        AssertClose(new Vector2(500f, 350f), camera.WorldToScreen(camera.Position, inset));
    }

    [Fact]
    public void PanByScreenDelta_MovesPositionOppositeDividedByZoom()
    {
        var cam = new Camera2D { Zoom = 2f, Position = Vector2.Zero };
        cam.PanByScreenDelta(new Vector2(40f, 0f));
        AssertClose(new Vector2(-20f, 0f), cam.Position);   // 40 / 2 = 20, opposite (grab-and-drag)
    }

    [Fact]
    public void PanByScreenDelta_IgnoresZeroAndDegenerateZoom()
    {
        var cam = new Camera2D { Zoom = 0f, Position = new Vector2(5f, 5f) };
        cam.PanByScreenDelta(new Vector2(10f, 10f));   // Zoom <= 0 guarded -> no-op
        AssertClose(new Vector2(5f, 5f), cam.Position);
    }

    [Fact]
    public void ZoomAboutScreenPoint_KeepsFocusWorldPointFixed()
    {
        var cam = new Camera2D { Zoom = 1f, Viewport = Vp };
        var focus = new Vector2(500f, 300f);   // Vp center (400,300) -> world (100,0)
        var worldBefore = cam.ScreenToWorld(focus, Vp);
        cam.ZoomAboutScreenPoint(2f, focus, Vp, 0.1f, 10f);
        Assert.Equal(2f, cam.Zoom, 3);
        AssertClose(worldBefore, cam.ScreenToWorld(focus, Vp));   // focal world point pinned under focus
    }

    [Fact]
    public void ZoomAboutScreenPoint_ClampsToMax()
    {
        var cam = new Camera2D { Zoom = 1f };
        cam.ZoomAboutScreenPoint(50f, new Vector2(400f, 300f), Vp, 0.1f, 10f);
        Assert.Equal(10f, cam.Zoom, 3);
    }

    [Fact]
    public void ZoomAboutScreenPoint_KeepsFocusWorldPointFixed_AtNonzeroPosition()
    {
        var cam = new Camera2D { Zoom = 1f, Position = new Vector2(200f, -150f), Viewport = Vp };
        var focus = new Vector2(500f, 300f);
        var worldBefore = cam.ScreenToWorld(focus, Vp);
        cam.ZoomAboutScreenPoint(2f, focus, Vp, 0.1f, 10f);
        AssertClose(worldBefore, cam.ScreenToWorld(focus, Vp));   // focus pinned even with nonzero start position
    }

    [Fact]
    public void ZoomAboutScreenPoint_ClampsToMin()
    {
        var cam = new Camera2D { Zoom = 1f };
        cam.ZoomAboutScreenPoint(0.001f, new Vector2(400f, 300f), Vp, 0.1f, 10f);
        Assert.Equal(0.1f, cam.Zoom, 3);
    }
}
