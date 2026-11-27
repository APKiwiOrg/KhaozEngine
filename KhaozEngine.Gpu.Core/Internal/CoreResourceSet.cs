using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Core.Interop;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe class CoreResourceSet(CoreGpuDevice owner, khaoz_gpu_resource_set* handle,
    CoreResourceLayout layout, CoreResourceSet.UniformWindow[] uniforms)
    : CoreResource(owner, handle), IGpuResourceSet
{
    internal readonly record struct UniformWindow(uint Binding, uint Offset, uint Size, uint Capacity, bool Dynamic);

    internal khaoz_gpu_resource_set* Handle => (khaoz_gpu_resource_set*)NativeHandle;
    internal CoreResourceLayout Layout => layout;
    internal bool HasDynamicElement { get; } = Array.Exists(uniforms, uniform => uniform.Dynamic);

    internal static CoreResourceSet Create(CoreGpuDevice device, in GpuResourceSetDescription description)
    {
        lock (device.Gate)
        {
            var layout = RequireOwned<CoreResourceLayout>(device, description.Layout, "layout", "resource layout",
                "a resource set");
            var layoutHandle = layout.Handle;
            IGpuBindableResource[] resources = description.Resources ?? [];
            var elements = layout.Elements;
            if (resources.Length != elements.Length)
                throw new GpuResourceSetException(GpuResourceSetRefusal.ResourceCountMismatch,
                    "A resource set needs one resource per layout element.", nameof(description))
                {
                    DeclaredCount = elements.Length,
                    SuppliedCount = resources.Length,
                };

            var bindings = new khaoz_gpu_resource_binding[resources.Length];
            var uniforms = new List<UniformWindow>();
            for (int i = 0; i < bindings.Length; i++)
                bindings[i] = Resolve(device, elements[i], resources[i], i, uniforms);

            var windows = uniforms.ToArray();
            fixed (khaoz_gpu_resource_binding* data = bindings)
            {
                khaoz_gpu_resource_set* set;
                NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_resource_set_create(device.Handle,
                    layoutHandle, data, (uint)bindings.Length, &set), nameof(IGpuResourceFactory.CreateResourceSet));
                return new CoreResourceSet(device, set, layout, windows);
            }
        }
    }

    internal void ValidateOffsets(uint slot, uint callerOffset)
    {
        foreach (var uniform in uniforms)
        {
            ulong applied = uniform.Dynamic ? callerOffset : 0u;
            VulkanRingStride.RequireBindWindowFits(uniform.Offset, applied, uniform.Size, uniform.Capacity);
            ulong offset = uniform.Offset + applied;
            ulong alignment = Owner.UniformOffsetAlignment;
            if (offset % alignment != 0)
                throw new GpuDynamicBindingRangeException(GpuDynamicBindingRefusal.OffsetMisaligned,
                    "dynamicUniform", offset, "The Vulkan uniform binding offset is misaligned.")
                {
                    Set = slot,
                    Binding = uniform.Binding,
                    ComposedOffset = offset,
                    AlignmentBytes = alignment,
                    RingBase = 0,
                    RangeOffset = uniform.Offset,
                    DynamicOffset = applied,
                    RangeBytes = uniform.Size,
                    SegmentStride = uniform.Capacity,
                };
        }
    }

    private static khaoz_gpu_resource_binding Resolve(CoreGpuDevice device, in GpuResourceLayoutElement element,
        IGpuBindableResource? resource, int binding, List<UniformWindow> uniforms)
    {
        if (resource is null)
            throw new GpuResourceSetException(GpuResourceSetRefusal.ResourceMissing,
                "A resource set binding is missing its resource.", nameof(resource))
            {
                ElementName = element.Name,
                Binding = binding,
            };

        switch (element.Kind)
        {
            case GpuResourceKind.UniformBuffer:
            case GpuResourceKind.StructuredBufferReadOnly:
            case GpuResourceKind.StructuredBufferReadWrite:
                return ResolveBuffer(device, element, resource, binding, uniforms);
            case GpuResourceKind.TextureReadOnly:
            case GpuResourceKind.TextureReadWrite:
                return ResolveTexture(device, element, resource, binding);
            default:
                return new khaoz_gpu_resource_binding
                {
                    sampler = RequireBinding<CoreSampler>(device, resource, element, binding).Handle,
                };
        }
    }

    private static khaoz_gpu_resource_binding ResolveBuffer(CoreGpuDevice device, in GpuResourceLayoutElement element,
        IGpuBindableResource resource, int binding, List<UniformWindow> uniforms)
    {
        object? source = resource is GpuBufferRange window ? window.Buffer : resource;
        var buffer = RequireBinding<CoreBuffer>(device, source, element, binding);
        uint offset = resource is GpuBufferRange range ? range.Offset : 0;
        uint size = resource is GpuBufferRange view ? view.Size : buffer.SizeInBytes;
        if (size == 0 || offset > buffer.SizeInBytes || size > buffer.SizeInBytes - offset)
            throw new GpuResourceSetException(GpuResourceSetRefusal.BufferWindowOutsideResource,
                "The resource set binding window exceeds its buffer.", nameof(buffer))
            {
                ElementName = element.Name,
                Binding = binding,
                RangeOffset = offset,
                RangeBytes = size,
                BufferBytes = buffer.SizeInBytes,
            };

        if (element.Kind == GpuResourceKind.UniformBuffer)
        {
            if ((buffer.Usage & GpuBufferUsage.UniformBuffer) == 0)
                throw new VulkanResourceSetRingException(VulkanResourceSetRingRefusal.UniformBufferNotRingBacked,
                    "A Vulkan uniform binding requires a buffer created with uniform usage.", nameof(resource))
                {
                    ElementName = element.Name,
                    Binding = binding,
                    ResourceKind = element.Kind,
                    ActualUsage = buffer.Usage,
                };
            uniforms.Add(new UniformWindow((uint)binding, offset, size, buffer.SizeInBytes, element.Dynamic));
        }

        else if ((buffer.Usage & GpuBufferUsage.UniformBuffer) != 0)
        {
            throw new VulkanResourceSetRingException(VulkanResourceSetRingRefusal.RingBufferUsedAsStorage,
                "A Vulkan uniform buffer cannot be used as a structured buffer.", nameof(resource))
            {
                ElementName = element.Name,
                Binding = binding,
                ResourceKind = element.Kind,
                ActualUsage = buffer.Usage,
            };
        }

        return new khaoz_gpu_resource_binding { buffer = buffer.Handle, offset = offset, range = size };
    }

    private static khaoz_gpu_resource_binding ResolveTexture(CoreGpuDevice device, in GpuResourceLayoutElement element,
        IGpuBindableResource resource, int binding)
    {
        var texture = RequireBinding<CoreTexture>(device, resource, element, binding);
        var required = element.Kind == GpuResourceKind.TextureReadOnly
            ? GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps
            : GpuTextureUsage.Storage;
        if ((texture.Usage & required) == 0)
            throw new GpuResourceSetException(texture.IsStaging ? GpuResourceSetRefusal.StagingTextureCannotBind
                    : GpuResourceSetRefusal.TextureUsageMissing,
                "The texture lacks the usage required by the resource layout.", nameof(resource))
            {
                ElementName = element.Name,
                Binding = binding,
                ResourceKind = element.Kind,
                ActualUsage = texture.Usage,
                RequiredUsage = required,
            };
        return new khaoz_gpu_resource_binding { texture = texture.Handle };
    }

    private static T RequireBinding<T>(CoreGpuDevice device, object? resource,
        in GpuResourceLayoutElement element, int binding) where T : CoreResource
    {
        if (resource is T typed)
            return Require<T>(device, typed, "resource", "resource set");

        throw GpuResourceSetMismatch.Refusal(resource, element, binding, GpuBackendKind.VulkanNative,
            typeof(T), "The resource does not match the layout element.", nameof(resource));
    }

    protected override khaoz_result Release(void* value)
        => KhaozNative.khaoz_gpu_resource_set_destroy((khaoz_gpu_resource_set*)value);
}
