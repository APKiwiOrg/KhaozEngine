using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class EffectAtmosphereCommandTests
{
    [Theory]
    [InlineData("overlay", false, false, 2)]
    [InlineData("overlay", false, true, 2)]
    [InlineData("trail", false, false, 2)]
    [InlineData("trail", false, true, 2)]
    [InlineData("textured", false, false, 2)]
    [InlineData("textured", false, true, 2)]
    [InlineData("overlay", true, false, 3)]
    [InlineData("overlay", true, true, 3)]
    [InlineData("trail", true, false, 3)]
    [InlineData("trail", true, true, 3)]
    [InlineData("textured", true, false, 3)]
    [InlineData("textured", true, true, 3)]
    public void EffectUploads_MatchLegacyCostUnlessAtmosphereIsEnabled(
        string effect, bool enabled, bool additive, int expectedUploads)
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.Post.Atmosphere.Enabled = enabled;
        rig.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        rig.Scene.Camera.Frame(Vector3.Zero, new Vector3(6, 4, 6));
        var texture = rig.Scene.LoadTexture(new byte[4], 1, 1, TextureMipPolicy.None);
        var trail = new[] { new TrailSample(new(-1, 1, 0), .1f, 1), new TrailSample(new(1, 1, 0), .1f, 1) };
        var blend = additive ? BillboardBlend.Additive : BillboardBlend.Alpha;
        void Draw(Scene3D scene)
        {
            switch (effect)
            {
                case "overlay": scene.DrawBillboard(new(0, 1, 0), .5f, Color.White, blend); break;
                case "textured": scene.DrawBillboard(texture, new(0, 1, 0), .5f, Color.White, blend); break;
                case "trail":
                    scene.DrawTrail(trail, TrailStyle.Default with
                    { Blend = additive ? TrailBlend.Additive : TrailBlend.Alpha }); break;
            }
        }

        rig.Frame(Draw);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList())
        {
            CapturePayloads = true,
            UniformWindowsOfSet = WindowsOf,
        };
        rig.Frame(commands: commands);
        int baselineUploads = commands.Uploads.Count;
        int baselineDraws = commands.DrawCount;
        commands.Clear();
        rig.Frame(Draw, commands);

        // At bd2ed647f every path uploads one vertex span and one matrix. Enabled fog adds one blend lane.
        Assert.Equal(expectedUploads, commands.Uploads.Count - baselineUploads);
        Assert.Equal(baselineDraws + 1, commands.DrawCount);
        uint uniformSize = effect == "textured" ? 512u : 80u;
        var uniforms = commands.Uploads.Where(upload => upload.Buffer.SizeInBytes == uniformSize).ToArray();
        Assert.Equal(enabled ? 2 : 1, uniforms.Length);
        var matrix = effect == "overlay" ? rig.Scene.CurrentFrameView.ViewProjection
            : rig.Scene.CurrentFrameView.JitteredViewProjection;
        foreach (var read in commands.Reads.Where(read => read.Buffer.SizeInBytes == uniformSize))
        {
            var upload = Assert.Single(uniforms, upload => ReferenceEquals(upload.Buffer, read.Buffer)
                && upload.Offset == read.Offset);
            Assert.Equal(matrix, MemoryMarshal.Read<Matrix4x4>(upload.Data));
            // This flag must travel with the frame matrix, rather than an older atmosphere ring segment.
            Assert.NotNull(upload.Data);
            Assert.Equal(80, upload.Data.Length);
            Assert.Equal(enabled ? 1f : 0f, MemoryMarshal.Read<Vector4>(upload.Data.AsSpan(64)).Y);
        }
        Assert.Contains(commands.Reads, read => read.Buffer.SizeInBytes == uniformSize);
    }

    static IReadOnlyList<UniformWindow> WindowsOf(IGpuResourceSet set)
        => Assert.IsType<FakeResourceSet>(set).Resources.Select(resource => resource switch
        {
            IGpuBuffer buffer => new UniformWindow(buffer, 0, buffer.SizeInBytes, false),
            GpuBufferRange range => new UniformWindow(range.Buffer, range.Offset, range.Size, true),
            _ => default,
        }).Where(window => window.Buffer is not null).ToArray();
}
