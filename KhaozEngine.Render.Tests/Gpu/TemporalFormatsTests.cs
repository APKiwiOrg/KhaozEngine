using System;
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
    /// TASK E1'S DECISION, PINNED. The temporal history colour is half float RGBA, the confidence and stability target
    /// half float RG, and the previous depth single float R. All three are seam members every backend already renders
    /// and samples. R11G11B10 float, the design's first choice, is not a seam member. The reasoning is in the design's
    /// plan amendments. Device-free. The Direct3D 11 map is a Windows-only type, so its fact asserts only on Windows,
    /// and that leg also proves the three formats through the HDR, distortion and outline goldens.
    /// </summary>
    public sealed class TemporalFormatsTests
    {
        [Fact]
        public void TheDirect3D11MapMapsEveryHistoryFormatAsAColourTarget()
        {
            if (!OperatingSystem.IsWindows()) return;   // the map is a Windows-only type
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
    }
}
