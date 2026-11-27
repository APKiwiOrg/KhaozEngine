using System;
using KhaozEngine.Gpu.Core.Interop;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe class CoreResourceLayout(CoreGpuDevice device, khaoz_gpu_resource_layout* handle,
    GpuResourceLayoutElement[] elements, VulkanDescriptorBinding[] bindings)
    : CoreResource(device, handle), IGpuResourceLayout
{
    internal khaoz_gpu_resource_layout* Handle => (khaoz_gpu_resource_layout*)NativeHandle;
    internal ReadOnlySpan<GpuResourceLayoutElement> Elements => elements;
    internal ReadOnlySpan<VulkanDescriptorBinding> Bindings => bindings;
    internal int DynamicUniformCount => VulkanDescriptorPolicy.DynamicUniformCount(bindings);

    internal static CoreResourceLayout Create(CoreGpuDevice device, in GpuResourceLayoutDescription description)
    {
        GpuResourceLayoutElement[] elements = description.Elements is null ? [] : [.. description.Elements];
        var bindings = VulkanDescriptorPolicy.BindingsFor(new GpuResourceLayoutDescription(elements));
        var native = new khaoz_gpu_resource_element[elements.Length];
        for (int i = 0; i < native.Length; i++)
        {
            var element = elements[i];
            native[i] = new khaoz_gpu_resource_element
            {
                kind = (uint)element.Kind,
                stages = (uint)element.Stages,
                dynamic_offset = element.Dynamic ? 1u : 0,
            };
        }

        lock (device.Gate)
        fixed (khaoz_gpu_resource_element* data = native)
        {
            khaoz_gpu_resource_layout* layout;
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_resource_layout_create(
                device.Handle, data, (uint)native.Length, &layout), nameof(IGpuResourceFactory.CreateResourceLayout));
            return new CoreResourceLayout(device, layout, elements, bindings);
        }
    }

    protected override khaoz_result Release(void* value)
        => KhaozNative.khaoz_gpu_resource_layout_destroy((khaoz_gpu_resource_layout*)value);
}
