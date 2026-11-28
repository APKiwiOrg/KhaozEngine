using KhaozEngine.Gpu.Core.Interop;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe class CoreFence(CoreGpuDevice owner, khaoz_gpu_fence* handle)
    : CoreResource(owner, handle), IGpuFence
{
    internal khaoz_gpu_fence* Handle => (khaoz_gpu_fence*)NativeHandle;

    public bool Signaled
    {
        get
        {
            lock (Owner.Gate)
            {
                uint signaled;
                NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_fence_signaled(Handle, &signaled), nameof(Signaled));
                return signaled != 0;
            }
        }
    }

    public void Reset()
    {
        lock (Owner.Gate)
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_fence_reset(Handle), nameof(Reset));
    }

    protected override khaoz_result Release(void* value) => KhaozNative.khaoz_gpu_fence_destroy((khaoz_gpu_fence*)value);
}
