using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfControlReadback
{
    internal static string Validate(IGpuDevice device, int id, IGpuTexture host, List<IGpuTexture> colors, IGpuTexture? depth)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] hostBytes = Read(device, host);
        Check(hostBytes, host.Format, [0, 0, 0, 1]); hash.AppendData(hostBytes);
        for (int i = 0; i < colors.Count; i++)
        {
            byte[] bytes = Read(device, colors[i]);
            float factor = id == 12 ? 1f - 1f / 256f : 1f;
            float[] clear = i switch { 0 or 1 => [.02f, .03f, .06f, 0f], 2 => [.02f], _ => [65504f, 65504f] };
            float[] expected = id == 2 || id == 12 && i > 0 ? clear : i switch
            {
                0 => [.25f * factor + (id == 12 ? .02f / 256f : 0),
                    .5f * factor + (id == 12 ? .03f / 256f : 0), .75f * factor + (id == 12 ? .06f / 256f : 0), .5f * factor],
                1 => [.25f, .5f, .75f, 1f],
                2 => [.5f],
                _ => [.125f, .25f]
            };
            Check(bytes, colors[i].Format, expected); hash.AppendData(bytes);
        }
        if (depth is not null && id != 11)
        {
            byte[] bytes = DepthImage(device, depth);
            Check(bytes, GpuPixelFormat.R8G8B8A8UNorm, [1, 0, 0, 1]); hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    // Depth/stencil staging layouts differ by backend. Fetch the depth aspect into an ordinary
    // RGBA8 image outside timed spans instead of assuming a packed depth/stencil CPU layout.
    static byte[] DepthImage(IGpuDevice device, IGpuTexture depth)
    {
        var f = device.Factory;
        using var color = f.CreateTexture(GpuTextureDescription.Texture2D(depth.Width, depth.Height,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var target = f.CreateFramebuffer(null, color);
        using var shader = f.CreateShadersFromSpirv(RendererPerfControlShaders.Source("fullscreen.vert"),
            RendererPerfControlShaders.Source("depth-read.frag"));
        using var layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Depth", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("Point", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
        using var set = f.CreateResourceSet(new(layout, depth, device.PointSampler));
        using var pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            ShaderSet = shader,
            ResourceLayouts = [layout],
            VertexLayouts = [],
            Outputs = target.Outputs,
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.CounterClockwise, true, false),
            Topology = GpuPrimitiveTopology.TriangleList
        });
        using (var commands = f.CreateCommandList())
        {
            using (GpuRecording.Open(device, commands, "renderer-control-depth-validation"))
            {
                commands.SetFramebuffer(target); commands.SetFullScissorRects();
                commands.SetPipeline(pipeline); commands.SetGraphicsResourceSet(0, set); commands.Draw(3);
            }
            device.Submit(commands); device.WaitForIdle();
        }
        return Read(device, color);
    }
    internal static void Check(ReadOnlySpan<byte> bytes, GpuPixelFormat format, ReadOnlySpan<float> expected)
    {
        int stride = RendererPerfRecorder.BytesPerPixel(format);
        int channels = format is GpuPixelFormat.R16G16B16A16Float or GpuPixelFormat.R8G8B8A8UNorm ? 4
            : format == GpuPixelFormat.R16G16Float ? 2 : 1;
        if (bytes.Length == 0 || bytes.Length % stride != 0 || expected.Length < channels)
            throw new InvalidOperationException("The control image is incomplete.");
        for (int pixel = 0; pixel < bytes.Length; pixel += stride)
            for (int channel = 0; channel < channels; channel++)
            {
                float value = format == GpuPixelFormat.R8G8B8A8UNorm ? bytes[pixel + channel] / 255f
                    : format is GpuPixelFormat.R16G16B16A16Float or GpuPixelFormat.R16G16Float
                        ? (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes[(pixel + channel * 2)..]))
                        : BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[pixel..]));
                float tolerance = .003f;
                if (format is GpuPixelFormat.R16G16B16A16Float or GpuPixelFormat.R16G16Float)
                {
                    // Allow an adjacent storage value for repeated blend rounding. Keep the original
                    // tighter cap for large values, including the preserved motion sentinel.
                    Half stored = (Half)expected[channel];
                    tolerance = MathF.Min(tolerance, MathF.Max(
                        MathF.Abs((float)Half.BitIncrement(stored) - expected[channel]),
                        MathF.Abs((float)Half.BitDecrement(stored) - expected[channel])));
                }
                if (!float.IsFinite(value) || MathF.Abs(value - expected[channel]) > tolerance)
                    throw new InvalidOperationException($"The retained {format} output differs at {pixel}/{channel}.");
            }
    }
    static byte[] Read(IGpuDevice device, IGpuTexture texture)
    {
        using var staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(texture.Width, texture.Height,
            texture.Format, GpuTextureUsage.Staging));
        using (var commands = device.Factory.CreateCommandList())
        {
            using (GpuRecording.Open(device, commands, "renderer-control-validation")) commands.CopyTexture(texture, staging);
            device.Submit(commands); device.WaitForIdle();
        }
        MappedData map = device.Map(staging, GpuMapMode.Read);
        try
        {
            int row = checked((int)texture.Width * RendererPerfRecorder.BytesPerPixel(texture.Format));
            if (map.Data == IntPtr.Zero || map.RowPitch < row || map.SizeInBytes < (long)map.RowPitch * texture.Height)
                throw new InvalidDataException("The mapped control image is incomplete.");
            byte[] bytes = new byte[checked(row * (int)texture.Height)];
            unsafe
            {
                for (int y = 0; y < texture.Height; y++)
                    new ReadOnlySpan<byte>((byte*)map.Data + (long)y * map.RowPitch, row).CopyTo(bytes.AsSpan(y * row));
            }
            return bytes;
        }
        finally { device.Unmap(staging); }
    }
}
