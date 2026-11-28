using System;
using KhaozEngine.Gpu.Core.Interop;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe class CoreShaderSet(CoreGpuDevice owner, khaoz_gpu_shader_set* handle)
    : CoreResource(owner, handle), IGpuShaderSet
{
    internal khaoz_gpu_shader_set* Handle => (khaoz_gpu_shader_set*)NativeHandle;

    internal static CoreShaderSet Create(CoreGpuDevice device, string vertGlsl, string fragGlsl)
    {
        ArgumentNullException.ThrowIfNull(vertGlsl, "vertexGlsl");
        ArgumentNullException.ThrowIfNull(fragGlsl, "fragmentGlsl");
        const string label = "shader pair";
        var replacement = CoreShaderOverrides.Override?.Invoke(vertGlsl, fragGlsl);
        byte[] vertex = replacement?.Vertex ?? SpirvFrontEnd.ToSpirv(vertGlsl, GpuShaderStages.Vertex, label);
        byte[] fragment = replacement?.Fragment ?? SpirvFrontEnd.ToSpirv(fragGlsl, GpuShaderStages.Fragment, label);
        lock (device.Gate)
        fixed (byte* vertexBytes = vertex, fragmentBytes = fragment)
        {
            khaoz_gpu_shader_set* shaders;
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_shader_set_create(device.Handle,
                (uint*)vertexBytes, (uint)vertex.Length / sizeof(uint),
                (uint*)fragmentBytes, (uint)fragment.Length / sizeof(uint), &shaders),
                nameof(IGpuResourceFactory.CreateShadersFromSpirv));
            return new CoreShaderSet(device, shaders);
        }
    }

    protected override khaoz_result Release(void* value)
        => KhaozNative.khaoz_gpu_shader_set_destroy((khaoz_gpu_shader_set*)value);
}
