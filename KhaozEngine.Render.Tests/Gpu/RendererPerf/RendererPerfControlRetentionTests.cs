using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfControlRetentionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    public void Eight_layer_readback_rejects_every_missing_draw_after_storage_quantization(int layers, bool roundEachDraw)
    {
        float[] source = [.25f, .5f, .75f, .5f];
        float[] pixel = [.02f, .03f, .06f, 0];
        if (roundEachDraw)
            for (int channel = 0; channel < pixel.Length; channel++) pixel[channel] = (float)(Half)pixel[channel];
        for (int layer = 0; layer < layers; layer++)
            for (int channel = 0; channel < pixel.Length; channel++)
            {
                pixel[channel] = source[channel] * .5f + pixel[channel] * .5f;
                if (roundEachDraw) pixel[channel] = (float)(Half)pixel[channel];
            }
        Half[] stored = Array.ConvertAll(pixel, value => (Half)value);
        byte[] bytes = MemoryMarshal.AsBytes(stored.AsSpan()).ToArray();
        // Eight source-alpha blends over the fixed clear, derived independently of the readback builder.
        float[] expected = [.2491015625f, .4981640625f, .7473046875f, .498046875f];
        if (layers == 8)
            RendererPerfControlReadback.Check(bytes, GpuPixelFormat.R16G16B16A16Float, expected);
        else
            Assert.Throws<InvalidOperationException>(() =>
                RendererPerfControlReadback.Check(bytes, GpuPixelFormat.R16G16B16A16Float, expected));
    }
}
