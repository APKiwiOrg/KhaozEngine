using System;

namespace KhaozEngine.Gpu.Internal;

/// <summary>Distinguishes a foreign native adapter from a resource of the wrong logical binding kind.</summary>
internal static class GpuResourceSetMismatch
{
    internal static ArgumentException Refusal(object? resource, in GpuResourceLayoutElement element,
        int binding, GpuBackendKind backend, Type expectedType, string message, string paramName)
    {
        bool matchesKind = element.Kind switch
        {
            GpuResourceKind.UniformBuffer or GpuResourceKind.StructuredBufferReadOnly
                or GpuResourceKind.StructuredBufferReadWrite => resource is IGpuBuffer,
            GpuResourceKind.TextureReadOnly or GpuResourceKind.TextureReadWrite => resource is IGpuTexture,
            GpuResourceKind.Sampler => resource is IGpuSampler,
            _ => false,
        };
        if (matchesKind)
        {
            return new GpuResourceOwnershipArgumentException(GpuResourceOwnershipRefusal.ForeignBackend,
                message, paramName)
            {
                ExpectedBackend = backend,
                ActualType = resource?.GetType(),
                ExpectedType = expectedType,
                ResourceKind = element.Kind.ToString(),
                ElementName = element.Name,
                ResourceIndex = binding,
                Context = "resource set",
            };
        }

        return new GpuResourceSetException(GpuResourceSetRefusal.ResourceKindMismatch, message, paramName)
        {
            ElementName = element.Name,
            Binding = binding,
            ResourceKind = element.Kind,
            ActualType = resource?.GetType(),
            ExpectedType = expectedType,
        };
    }
}
