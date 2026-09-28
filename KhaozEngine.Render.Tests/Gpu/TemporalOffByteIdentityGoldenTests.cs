using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN section 7 item 8, the half a test can hold: a frame under every other
    /// anti-aliasing mode builds none of the resolve's objects (the renderer, its display targets, the sharpen, the
    /// debug view pass and the count probe), allocates no motion target, and uploads exact zeros in the frame block's
    /// mip bias lanes. The other half is the whole committed golden suite passing unchanged, on this leg locally and
    /// on every leg through the golden bake comparison.
    /// </summary>
    public sealed class TemporalOffByteIdentityGoldenTests
    {
        // Params.z and Params.w of the frame header, read off the struct the frame block packs rather than trusted.
        static readonly int ParamsOffset =
            (int)Marshal.OffsetOf<ModelRenderer.FrameUbo>(nameof(ModelRenderer.FrameUbo.Params));
        static readonly int BiasOffset = ParamsOffset + 8, GradScaleOffset = ParamsOffset + 12;

        [GpuTheory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void AFrameWithoutTemporalBuildsNoneOfTheResolveObjects(int mode)
        {
            Assert.Equal(120, BiasOffset);   // the offset the mip bias tests and the shaders' Params.z agree on
            Assert.Equal(124, GradScaleOffset);
            AntiAliasing aa = mode switch { 0 => AntiAliasing.Off, 1 => AntiAliasing.Fxaa, _ => AntiAliasing.Msaa(4) };
            var stage = new FrontStage(160, 90, 4.5f);
            // A resolve debug view and a count request every frame: both take effect only under temporal
            // anti-aliasing, so neither may build anything here.
            using var fx = new TemporalFixture(160, 90, s =>
            {
                stage.Setup(s, aa);
                s.DebugView = SceneDebugView.History;
            });
            fx.Frames(3, (s, _) =>
            {
                s.RequestTemporalCounts();
                stage.Wall(s);
                s.Draw(stage.Box, Matrix4x4.Identity);
            });
            Scene3D scene = fx.Scene;

            if (mode == 2) Assert.True(scene.ModelSampleCountForTests > 1, "the device must honour MSAA for this row");
            else Assert.Equal(1, scene.ModelSampleCountForTests);
            Assert.Null(scene.TemporalResolveRendererForTests);
            Assert.Null(scene.TemporalPostTargetsForTests);
            Assert.Null(scene.MotionResourcesForTests);
            Assert.False(scene.TemporalSharpenBuiltForTests, $"{aa.Mode} built the temporal sharpen");
            Assert.False(scene.TemporalDebugViewBuiltForTests, $"{aa.Mode} built the temporal debug view pass");
            Assert.False(scene.TemporalCountProbeBuiltForTests, $"{aa.Mode} built the temporal count probe");
            Assert.False(scene.LaterRenderPostCreatedForTests, $"{aa.Mode} built a later render's post chain");
            ReadOnlySpan<byte> frameBlock = scene.FrameImageForTests;
            Assert.Equal(0, BitConverter.ToInt32(frameBlock.Slice(BiasOffset, 4)));
            Assert.Equal(0, BitConverter.ToInt32(frameBlock.Slice(GradScaleOffset, 4)));
        }
    }
}
