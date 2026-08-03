using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// One resolved binding inside a <see cref="D3D11ResourceSet"/>: the layout-relative register, the stages that
    /// see it, and the resource itself with any buffer window ALREADY resolved.
    /// <para>
    /// The buffer fields are populated for every buffer kind, whether the caller bound a bare
    /// <see cref="IGpuBuffer"/> or a <see cref="GpuBufferRange"/>. A bare buffer resolves to the whole buffer, and
    /// the two then travel the same path, so the bind flush has one shape to handle rather than two.
    /// </para>
    /// </summary>
    internal readonly struct D3D11BoundResource
    {
        internal D3D11BoundResource(GpuResourceKind kind, D3D11RegisterSlot slot, GpuShaderStages stages,
            bool dynamic, IGpuBindableResource resource, IGpuBuffer? buffer, uint offsetBytes, uint sizeBytes)
        {
            Kind = kind;
            Slot = slot;
            Stages = stages;
            Dynamic = dynamic;
            Resource = resource;
            Buffer = buffer;
            OffsetBytes = offsetBytes;
            SizeBytes = sizeBytes;
        }

        /// <summary>The declared kind, which is what picks the bind call.</summary>
        internal GpuResourceKind Kind { get; }
        /// <summary>The LAYOUT-RELATIVE register. The pipeline base is added when the set is bound.</summary>
        internal D3D11RegisterSlot Slot { get; }
        /// <summary>The stages this binding is visible to, which is what the per-stage fan-out reads.</summary>
        internal GpuShaderStages Stages { get; }
        /// <summary>Whether a per-draw byte offset is added to <see cref="OffsetBytes"/> at bind time.</summary>
        internal bool Dynamic { get; }
        /// <summary>The bound resource as handed in: a texture, a sampler, a buffer or a buffer range.</summary>
        internal IGpuBindableResource Resource { get; }
        /// <summary>The buffer behind <see cref="Resource"/> for a buffer kind, else null.</summary>
        internal IGpuBuffer? Buffer { get; }
        /// <summary>Window start in bytes, resolved AT SET CREATION. Zero for a bare buffer.</summary>
        internal uint OffsetBytes { get; }
        /// <summary>Window size in bytes, resolved AT SET CREATION. The whole buffer for a bare buffer.</summary>
        internal uint SizeBytes { get; }

        /// <summary>True when the window is the entire buffer, which lets a constant-buffer bind take the plain
        /// path rather than the offset one.</summary>
        internal bool IsFullRange => Buffer is not null && OffsetBytes == 0 && SizeBytes == Buffer.SizeInBytes;
    }

    /// <summary>
    /// The constants arithmetic every offset constant-buffer bind goes through, reproduced from the incumbent
    /// exactly. Direct3D 11 counts constant-buffer windows in CONSTANTS of 16 bytes, not in bytes, and
    /// <c>*SetConstantBuffers1</c> takes both numbers, so the conversion has to sit somewhere both the set and the
    /// bind flush can reach. It is arithmetic with no device in it, so it is here and it is tested.
    /// </summary>
    internal static class D3D11ConstantRange
    {
        /// <summary>Bytes per constant. A Direct3D 11 constant is one float4.</summary>
        internal const uint ConstantSizeBytes = 16;

        /// <summary>
        /// The smallest window Direct3D 11 accepts, in bytes. A shorter range is rounded UP to this rather than
        /// rejected, matching the incumbent. Rounding up can name constants past the caller's window, which is safe
        /// because the shader only ever reads the fields its own block declares, and the buffer itself is always at
        /// least this large in practice.
        /// </summary>
        internal const uint MinimumRangeBytes = 256;

        /// <summary>The window start expressed in constants.</summary>
        internal static uint FirstConstant(uint offsetBytes) => offsetBytes / ConstantSizeBytes;

        /// <summary>The window size expressed in constants, after the minimum is applied.</summary>
        internal static uint ConstantCount(uint sizeBytes)
            => (sizeBytes < MinimumRangeBytes ? MinimumRangeBytes : sizeBytes) / ConstantSizeBytes;
    }

    /// <summary>
    /// <see cref="IGpuResourceSet"/> for the native Direct3D 11 backend: a layout plus its resources, with every
    /// binding resolved ONCE, HERE, at creation.
    /// <para>
    /// THE RESOLUTION IS THE POINT. A <see cref="GpuBufferRange"/> inside the description is unpacked into a
    /// buffer plus an offset plus a size at SET creation and never at draw time, and the register each resource
    /// binds to is read off the layout at the same moment. Draw time is left with an array to walk. That is the
    /// same reasoning as the eager views of decision X1: work that can be done once at load time does not belong
    /// on the path a corrupted context makes fail, and an allocation there is a per-draw cost paid forever.
    /// </para>
    /// <para>
    /// There is no native object here either. Direct3D 11 has no descriptor-set primitive, so a set is a CPU-side
    /// record, which is why this type takes no device and needs no liveness gate. The DYNAMIC offset stays out of
    /// the record deliberately: it is a per-draw value supplied to the bind call, and baking it in would make one
    /// set per draw, which is the shape decision U3 exists to keep the uniform ring from forcing.
    /// </para>
    /// </summary>
    internal sealed class D3D11ResourceSet : IGpuResourceSet
    {
        readonly D3D11BoundResource[] _bindings;

        internal D3D11ResourceSet(in GpuResourceSetDescription description)
        {
            if (description.Layout is not D3D11ResourceLayout layout)
            {
                throw new ArgumentException(
                    "A resource set for the native Direct3D 11 backend needs a layout this backend created. A "
                    + "layout from another backend carries another backend's register numbering.",
                    nameof(description));
            }

            IGpuBindableResource[] resources = description.Resources ?? Array.Empty<IGpuBindableResource>();
            if (resources.Length != layout.ElementCount)
            {
                throw new ArgumentException(
                    "A resource set binds exactly one resource per layout element, in declaration order. The "
                    + $"layout declares {layout.ElementCount} and {resources.Length} were bound, so the register "
                    + "assignment would silently shift and the shader would read the wrong resource.",
                    nameof(description));
            }

            Layout = layout;
            _bindings = new D3D11BoundResource[resources.Length];
            for (int i = 0; i < resources.Length; i++) _bindings[i] = Resolve(layout, i, resources[i]);
        }

        /// <summary>The layout this set satisfies, which carries the register numbering.</summary>
        internal D3D11ResourceLayout Layout { get; }

        /// <summary>The resolved bindings, in layout declaration order.</summary>
        internal ReadOnlySpan<D3D11BoundResource> Bindings => _bindings;

        /// <summary>True once disposed. Nothing native is released, because nothing native was created.</summary>
        internal bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;

        static D3D11BoundResource Resolve(D3D11ResourceLayout layout, int index, IGpuBindableResource resource)
        {
            GpuResourceLayoutElement element = layout.Elements[index];
            D3D11RegisterSlot slot = layout.SlotAt(index);

            switch (element.Kind)
            {
                case GpuResourceKind.UniformBuffer:
                case GpuResourceKind.StructuredBufferReadOnly:
                case GpuResourceKind.StructuredBufferReadWrite:
                {
                    (IGpuBuffer buffer, uint offset, uint size) = ResolveBuffer(element, resource);
                    return new D3D11BoundResource(element.Kind, slot, element.Stages, element.Dynamic,
                        resource, buffer, offset, size);
                }

                case GpuResourceKind.TextureReadOnly:
                case GpuResourceKind.TextureReadWrite:
                    Require<IGpuTexture>(element, resource);
                    return new D3D11BoundResource(element.Kind, slot, element.Stages, element.Dynamic,
                        resource, null, 0, 0);

                case GpuResourceKind.Sampler:
                    Require<IGpuSampler>(element, resource);
                    return new D3D11BoundResource(element.Kind, slot, element.Stages, element.Dynamic,
                        resource, null, 0, 0);

                default:
                    throw new ArgumentOutOfRangeException(nameof(resource), element.Kind,
                        "Unmapped GpuResourceKind in a resource set.");
            }
        }

        // A bare buffer means the whole buffer. A range means exactly what it says, resolved now.
        static (IGpuBuffer Buffer, uint Offset, uint Size) ResolveBuffer(
            in GpuResourceLayoutElement element, IGpuBindableResource resource)
        {
            switch (resource)
            {
                case GpuBufferRange range when range.Buffer is not null:
                {
                    uint size = range.Size == 0 ? range.Buffer.SizeInBytes - range.Offset : range.Size;
                    if (range.Offset + size > range.Buffer.SizeInBytes)
                    {
                        throw new ArgumentException(
                            $"The buffer range bound at '{element.Name}' runs past the end of its buffer "
                            + $"({range.Offset} + {size} bytes into {range.Buffer.SizeInBytes}). Resolving the "
                            + "window here rather than at draw time is what makes that sayable at all.",
                            nameof(resource));
                    }
                    return (range.Buffer, range.Offset, size);
                }

                case IGpuBuffer buffer:
                    return (buffer, 0, buffer.SizeInBytes);

                default:
                    throw new ArgumentException(
                        $"'{element.Name}' is declared as {element.Kind}, so it needs an IGpuBuffer or a "
                        + $"GpuBufferRange. A {Describe(resource)} was bound.",
                        nameof(resource));
            }
        }

        static void Require<T>(in GpuResourceLayoutElement element, IGpuBindableResource resource) where T : class
        {
            if (resource is T) return;
            throw new ArgumentException(
                $"'{element.Name}' is declared as {element.Kind}, so it needs an {typeof(T).Name}. A "
                + $"{Describe(resource)} was bound, which would take the wrong register file and bind nothing "
                + "the shader reads.",
                nameof(resource));
        }

        static string Describe(IGpuBindableResource? resource) => resource is null ? "null" : resource.GetType().Name;
    }
}
