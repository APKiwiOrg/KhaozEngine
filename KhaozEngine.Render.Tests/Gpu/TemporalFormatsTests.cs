using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;
using VkFormat = Silk.NET.Vulkan.Format;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE HISTORY FORMATS, PINNED. The temporal history colour is half float RGBA, the confidence and stability target
    /// half float RG, and the previous depth single float R. All three are seam members every backend already renders
    /// and samples. R11G11B10 float, the design's first choice, is not a seam member. The reasoning is on
    /// <see cref="TemporalFormats"/>. Device-free. The Direct3D 11 map is a Windows-only type, so its fact asserts only
    /// on Windows, and that leg also proves the three formats through the HDR, distortion and outline goldens.
    /// </summary>
    public sealed class TemporalFormatsTests
    {
        [Fact]
        public void TheDirect3D11MapMapsEveryHistoryFormatAsAColourTarget()
        {
            // The map is a Windows-only type. Its body lives in a method this one never JIT-compiles off Windows, so the
            // Vortice assemblies stay unloaded there, which the off-Windows Direct3D 11 tests rely on.
            if (OperatingSystem.IsWindows()) AssertDirect3D11Maps();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static void AssertDirect3D11Maps()
        {
            Assert.Equal(Vortice.DXGI.Format.R16G16B16A16_Float,
                D3D11Formats.ToDxgiFormat(TemporalFormats.HistoryColor, false));
            Assert.Equal(Vortice.DXGI.Format.R16G16_Float,
                D3D11Formats.ToDxgiFormat(TemporalFormats.HistoryConfidence, false));
            Assert.Equal(Vortice.DXGI.Format.R32_Float, D3D11Formats.ToDxgiFormat(TemporalFormats.PreviousDepth, false));
        }

        [Fact]
        public void TheMetalAndVulkanMapsMapEveryHistoryFormatAsAColourTarget()
        {
            Assert.Equal(MTLPixelFormat.RGBA16Float, MetalFormats.ToPixelFormat(TemporalFormats.HistoryColor, false));
            Assert.Equal(MTLPixelFormat.RG16Float, MetalFormats.ToPixelFormat(TemporalFormats.HistoryConfidence, false));
            Assert.Equal(MTLPixelFormat.R32Float, MetalFormats.ToPixelFormat(TemporalFormats.PreviousDepth, false));

            Assert.Equal(VkFormat.R16G16B16A16Sfloat, VulkanFormats.ToVkFormat(TemporalFormats.HistoryColor, false));
            Assert.Equal(VkFormat.R16G16Sfloat, VulkanFormats.ToVkFormat(TemporalFormats.HistoryConfidence, false));
            Assert.Equal(VkFormat.R32Sfloat, VulkanFormats.ToVkFormat(TemporalFormats.PreviousDepth, false));
        }

        [Fact]
        public void TheStagingLayoutsSizeEveryHistoryFormatForReadback()
        {
            Assert.Equal((uint)TemporalFormats.HistoryColorBytesPerPixel, MetalStagingLayout.BytesPerTexel(TemporalFormats.HistoryColor));
            Assert.Equal((uint)TemporalFormats.HistoryConfidenceBytesPerPixel, MetalStagingLayout.BytesPerTexel(TemporalFormats.HistoryConfidence));
            Assert.Equal((uint)TemporalFormats.PreviousDepthBytesPerPixel, MetalStagingLayout.BytesPerTexel(TemporalFormats.PreviousDepth));
            Assert.Equal((uint)TemporalFormats.HistoryColorBytesPerPixel, VulkanStagingLayout.BytesPerTexel(TemporalFormats.HistoryColor));
            Assert.Equal((uint)TemporalFormats.HistoryConfidenceBytesPerPixel, VulkanStagingLayout.BytesPerTexel(TemporalFormats.HistoryConfidence));
            Assert.Equal((uint)TemporalFormats.PreviousDepthBytesPerPixel, VulkanStagingLayout.BytesPerTexel(TemporalFormats.PreviousDepth));
        }

        [Fact]
        public void AQualityHistoryAtA3456By2234DisplayHolds213Megabytes()
        {
            // Two colour and two confidence targets at 3456x2234, two previous depths at 2304x1489 (Quality, 1 / 1.5).
            Assert.Equal(212_742_144L, TemporalFormats.HistoryBytes(3456, 2234, 2304, 1489));
        }

        [Fact]
        public void TheSplitsTargetsAtQualityOnA3456By2234DisplayHold82Megabytes()
        {
            // The byte count a texel is the targets' own formats, and the internal size is the one the scene sizes.
            long perTexel = 0;
            foreach (GpuPixelFormat format in TemporalSplitFormats.Targets) perTexel += MetalStagingLayout.BytesPerTexel(format);
            Assert.Equal(TemporalSplitFormats.BytesPerTexel, perTexel);
            var settings = new KhaozEngine.Render3D.PixelPostProcessSettings();
            settings.Quality.AntiAliasing = KhaozEngine.Render3D.AntiAliasing.Temporal;
            settings.Temporal.Upscale = KhaozEngine.Render3D.TemporalUpscale.Quality;
            (int width, int height) = KhaozEngine.Render3D.Scene3D.ComputeTargetSize(settings, 3456, 2234);
            Assert.Equal((2304, 1489), (width, height));
            Assert.Equal(82_335_744L, TemporalSplitFormats.Bytes(width, height));   // 82.3 MB, the figure the docs cite
        }
    }
}
