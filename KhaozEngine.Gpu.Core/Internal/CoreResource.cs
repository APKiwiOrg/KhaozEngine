using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu.Core.Interop;

namespace KhaozEngine.Gpu.Core.Internal;

internal abstract unsafe class CoreResource(CoreGpuDevice owner, void* handle) : IDisposable
{
    private void* _handle = handle;

    internal CoreGpuDevice Owner => owner;

    protected void* NativeHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(owner.IsDisposed || _handle == null, this);
            return _handle;
        }
    }

    internal static T Require<T>(CoreGpuDevice device, object resource, string parameter, string kind,
        [CallerMemberName] string operation = "") where T : CoreResource
    {
        ArgumentNullException.ThrowIfNull(resource, parameter);
        if (resource is T typed && ReferenceEquals(typed.Owner, device))
            return typed;

        var reason = resource is T
            ? GpuResourceOwnershipRefusal.DifferentDevice
            : GpuResourceOwnershipRefusal.ForeignBackend;
        throw new GpuResourceOwnershipArgumentException(reason, $"Use a {kind} from this Vulkan device.", parameter)
        {
            ExpectedBackend = GpuBackendKind.VulkanNative,
            ExpectedType = typeof(T),
            ActualType = resource.GetType(),
            Context = operation,
            ResourceKind = kind,
        };
    }

    public void Dispose()
    {
        lock (owner.Gate)
        {
            void* released = _handle;
            _handle = null;
            if (released != null && !owner.IsDisposed)
                NativeResult.ThrowIfFailed(Release(released), "Release Vulkan resource");
        }
    }

    protected abstract khaoz_result Release(void* value);
}
