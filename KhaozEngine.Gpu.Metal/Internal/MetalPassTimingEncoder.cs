using System;

namespace KhaozEngine.Gpu.Metal.Internal;

internal interface IMetalPassTimingEncoderApi
{
    void AttachRender(IntPtr descriptor, IntPtr samples, int first);
    void AttachEncoder(IntPtr descriptor, IntPtr samples, ulong first, ulong last);
    IntPtr Open(MetalEncoderKind kind, IntPtr commandBuffer, IntPtr descriptor);
}

internal static class MetalPassTimingEncoder
{
    internal static IntPtr Begin<T>(T api, MetalEncoderKind kind, IntPtr buffer, IntPtr descriptor,
        PassTimingResources resources, int first) where T : IMetalPassTimingEncoderApi
    {
        if (kind == MetalEncoderKind.Render)
        {
            try
            {
                api.AttachRender(descriptor, resources.Samples, first);
                return api.Open(kind, buffer, descriptor);
            }
            finally { api.AttachRender(descriptor, IntPtr.Zero, 0); }
        }
        if (resources.Samples == IntPtr.Zero) return api.Open(kind, buffer, IntPtr.Zero);
        IntPtr pass = kind == MetalEncoderKind.Compute ? resources.Compute : resources.Blit;
        try
        {
            api.AttachEncoder(pass, resources.Samples, (ulong)first, (ulong)(first + 1));
            return api.Open(kind, buffer, pass);
        }
        finally { api.AttachEncoder(pass, IntPtr.Zero, ulong.MaxValue, ulong.MaxValue); }
    }
}
