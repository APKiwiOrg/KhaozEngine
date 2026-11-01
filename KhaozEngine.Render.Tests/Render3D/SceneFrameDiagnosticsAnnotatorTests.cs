using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SceneFrameDiagnosticsAnnotatorTests
{
    [Fact]
    public void Channels_cover_public_workload_and_preserve_camera_and_light_float_bits()
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new GpuOutputDescription(GpuPixelFormat.D24UNormS8UInt, GpuPixelFormat.R8G8B8A8UNorm));
        var camera = new Camera();
        scene.CameraOverride = camera;
        scene.Post.LightDirection = new Vector3(-0.0f, 0.25f, -1);
        scene.EnableTiming = true;
        var annotator = new SceneFrameDiagnosticsAnnotator(scene);
        var values = new long[annotator.Channels.Length];
        annotator.Sample(values);
        Assert.NotEmpty(values);
        Assert.Equal(annotator.Channels.Length, annotator.Channels.Distinct().Count());
        Assert.Equal(BitConverter.SingleToInt32Bits(-0.0f), Value("camera.view.M11.bits"));
        Assert.Equal(BitConverter.SingleToInt32Bits(0.75f), Value("camera.projection.M44.bits"));
        Assert.Equal(BitConverter.SingleToInt32Bits(camera.Eye.Y), Value("camera.eye.y.bits"));
        Assert.Equal(BitConverter.SingleToInt32Bits(camera.Forward.Z), Value("camera.forward.z.bits"));
        Assert.Equal(BitConverter.SingleToInt32Bits(scene.Post.LightDirection.X), Value("light.direction.x.bits"));
        Assert.Equal(1, Value("scene.EnableTiming"));
        var shadowProperties = typeof(ShadowPassDiagnostics).GetProperties()
            .Where(p => p.PropertyType == typeof(bool) || p.PropertyType == typeof(int)).ToArray();
        Assert.NotEmpty(shadowProperties);
        foreach (var property in shadowProperties)
        {
            Assert.Contains("shadow." + property.Name, annotator.Channels);
            object value = property.GetValue(scene.LastShadowPassDiagnostics)!;
            Assert.Equal(value is bool flag ? flag ? 1 : 0 : (int)value, Value("shadow." + property.Name));
        }
        var statsFields = typeof(RenderFrameStats).GetFields();
        Assert.NotEmpty(statsFields);
        foreach (var field in statsFields)
            Assert.Equal(Convert.ToInt64(field.GetValue(scene.LastFrameStats)), Value("stats." + field.Name));
        for (int c = 0; c < ShadowSettings.MaxCascades; c++)
        {
            Assert.Equal(scene.LastShadowPassDiagnostics.RigidSpanCount(c), Value($"shadow.RigidSpanCount.{c}"));
            Assert.Equal(scene.ShadowCascadeCasterCount(c), Value($"scene.ShadowCascadeCasterCount.{c}"));
            Assert.Equal(scene.ShadowCascadeSpanCount(c), Value($"scene.ShadowCascadeSpanCount.{c}"));
        }
        long Value(string channel)
        {
            int index = Array.IndexOf(annotator.Channels, channel);
            Assert.True(index >= 0, channel);
            return values[index];
        }
    }

    sealed class Camera : IIsoCamera3D
    {
        public Matrix4x4 View => new(-0.0f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        public Matrix4x4 Projection => new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, .75f);
        public Matrix4x4 ViewProjection => View * Projection;
        public Vector3 Eye => new(1.5f, 2.5f, 3.5f);
        public Vector3 Forward => new(0, 0, -1);
        public bool WorldToScreen(Vector3 world, int width, int height, out Vector2 pixel) { pixel = default; return false; }
    }
}
