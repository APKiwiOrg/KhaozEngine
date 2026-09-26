using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The MotionVectors view on a real device: black background, and a body moving right at 8 internal pixels a
/// frame painted half-bright cyan, the colour the view's hue wheel gives rightward motion.</summary>
public sealed class MotionVectorsViewGpuTests
{
    const int W = 320, H = 180;

    [GpuFact]
    public void ABodyMovingRightShowsHalfBrightCyanOverBlack()
    {
        using var fx = new TemporalFixture(W, H, s =>
        {
            s.DebugView = SceneDebugView.MotionVectors;   // turns temporal rendering on by itself
            s.Camera.OrthoSize = 12f;
        });
        MeshHandle box = fx.Scene.LoadMesh(MeshPrimitives.Box(2f));
        Vector3 right = Vector3.Normalize(Vector3.Cross(fx.Scene.Camera.Forward, Vector3.UnitY));
        const float PixelsPerMetre = H / 12f;
        Vector3 At(int n) => right * (8f / PixelsPerMetre * n);
        void Draw(Scene3D s, int n) =>
            s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(At(n))) { Motion = MotionKey.From(3) });

        fx.Frame(Draw);
        byte[] image = fx.Frame(Draw);

        Assert.Equal(new byte[] { 0, 0, 0, 255 }, image[..4]);
        Assert.True(fx.Scene.Camera.WorldToScreen(At(1), W, H, out Vector2 centre));
        int i = ((int)centre.Y * W + (int)centre.X) * 4;
        Assert.InRange(image[i], 0, 2);
        Assert.InRange(image[i + 1], 125, 130);
        Assert.InRange(image[i + 2], 125, 130);
    }
}
