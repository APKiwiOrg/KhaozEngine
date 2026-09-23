using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class Render3DSnapshotBackendGpuTests
    {
        const int Width = 80;
        const int Height = 60;

        [GpuFact]
        public void CaptureWithBackend_reports_metadata_for_the_returned_pixels()
        {
            (Action<Scene3D> setup, Action<Scene3D> drawFrame) richScene = CreateScene();
            Render3DCapture capture = Render3DSnapshot.CaptureWithBackend(
                Width, Height, richScene.setup, richScene.drawFrame, frames: 2);

            (Action<Scene3D> setup, Action<Scene3D> drawFrame) legacyScene = CreateScene();
            byte[] legacyRgba = Render3DSnapshot.Capture(
                Width, Height, legacyScene.setup, legacyScene.drawFrame, frames: 2);

            Assert.Equal(Width, capture.Width);
            Assert.Equal(Height, capture.Height);
            Assert.Equal(Width * Height * 4, capture.Rgba.Length);
            Assert.True(Enum.IsDefined(capture.Backend));
            Assert.False(GpuBackendSelector.IsRetired(capture.Backend));
            Assert.Equal(legacyRgba, capture.Rgba);
        }

        static (Action<Scene3D> Setup, Action<Scene3D> DrawFrame) CreateScene()
        {
            MeshHandle box = default;
            return (
                scene =>
                {
                    box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
                    scene.Camera.Frame(Vector3.Zero, new Vector3(3f, 3f, 3f));
                },
                scene => scene.Draw(
                    box,
                    Matrix4x4.CreateRotationY(0.35f),
                    new Color(0.2f, 0.7f, 0.3f, 1f)));
        }
    }
}
