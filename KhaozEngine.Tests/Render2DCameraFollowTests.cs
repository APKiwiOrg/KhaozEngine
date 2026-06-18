using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for the 5.x camera feel layer (PixelSnap, LookAheadSettings, CameraFollow).</summary>
public class Render2DCameraFollowTests
{
    private const int Vw = 800, Vh = 600;                    // center (400, 300)
    private const float Tol = 1e-2f;
    private static readonly Rect Unbounded = new(-1_000_000f, -1_000_000f, 2_000_000f, 2_000_000f);

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol) =>
        Assert.True(Vector2.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    // ---- PixelSnap ----

    [Fact]
    public void PixelSnap_RoundsEachAxisToGrid()
    {
        var snap = new PixelSnap(10f);
        AssertClose(new Vector2(10f, 10f), snap.Apply(new Vector2(13f, 7f)));
        AssertClose(new Vector2(20f, -10f), snap.Apply(new Vector2(16f, -12f)));
    }

    [Fact]
    public void PixelSnap_DisabledIsIdentity()
    {
        var snap = default(PixelSnap);          // Enabled == false
        var p = new Vector2(13.37f, -4.2f);
        AssertClose(p, snap.Apply(p));
    }

    [Fact]
    public void PixelSnap_NonPositiveGridIsIdentity()
    {
        var snap = new PixelSnap(0f);           // non-positive grid -> Enabled stays false
        var p = new Vector2(13.37f, -4.2f);
        AssertClose(p, snap.Apply(p));
    }
}
