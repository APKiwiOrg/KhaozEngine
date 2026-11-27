using KhaozEngine.Gpu.Core.Interop;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe class CoreSampler(CoreGpuDevice owner, khaoz_gpu_sampler* handle)
    : CoreResource(owner, handle), IGpuSampler
{
    protected override khaoz_result Release(void* value) => KhaozNative.khaoz_gpu_sampler_destroy((khaoz_gpu_sampler*)value);
}
