using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <see cref="IGpuBuffer"/> for the native Direct3D 11 backend: one <c>ID3D11Buffer</c> plus the EAGER views
    /// its declared usage earns, created here at construction and never on the draw path (decision X1).
    /// <para>
    /// A UNIFORM BUFFER IS RING-BACKED, AND ITS IDENTITY STILL NEVER CHANGES (decisions U1 and U3). The native
    /// buffer is created <c>DYNAMIC</c> plus <c>CPU_ACCESS_WRITE</c> at its 256-aligned size times the frame count
    /// rather than at the size the caller asked for, and <see cref="Ring"/> is what every write to it goes
    /// through. <see cref="SizeInBytes"/> stays the LOGICAL size the seam asked for, because that is the number a
    /// resource set's pinned <see cref="GpuBufferRange"/> was resolved against and the number a range check has to
    /// use. Nothing above this type ever learns the buffer is larger.
    /// </para>
    /// <para>
    /// STRUCTURED BUFFERS ARE RAW, AND THAT IS DECISION C2 RATHER THAN A SHORTCUT. Both structured kinds get a
    /// <c>DEFAULT</c>-usage buffer with <c>BufferAllowRawViews</c> and a FULL-RANGE byte-address view, and
    /// <see cref="GpuBufferDescription.StructureByteStride"/> stays advisory. SPIRV-Cross emits a GLSL storage
    /// block as a <c>ByteAddressBuffer</c> or an <c>RWByteAddressBuffer</c>, so a stride-shaped structured view
    /// would not be what the compiled shader reads. Keeping this identical to the incumbent was why the ocean
    /// compute kernels keep working.
    /// </para>
    /// <para>
    /// FULL RANGE, not a per-binding window. The incumbent cached one view per distinct offset and size pair,
    /// which is the lazy-creation shape decision X1 exists to remove. The seam never binds a sub-range of a
    /// structured buffer (only a uniform buffer takes a <see cref="GpuBufferRange"/>, and a constant-buffer bind
    /// carries its window in the bind call rather than in a view), so one view over the whole buffer is the entire
    /// requirement.
    /// </para>
    /// <para>
    /// Disposal is gated on <see cref="DeviceLiveness"/>, decision X3: destroying the device already freed
    /// every child object, so a wrapper disposed afterwards must do nothing rather than release twice.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11Buffer : IGpuBuffer, ID3D11RingBacked, ID3D11BindableViews, ID3D11MappableResource
    {
        readonly DeviceLiveness _liveness;
        readonly D3D11RingAllocator _rings;

        /// <param name="device">The device the buffer and its views are created on.</param>
        /// <param name="context">The immediate context a ring-backed buffer maps itself through.</param>
        /// <param name="liveness">The device's liveness token, so a disposal after device death is a no-op.</param>
        /// <param name="rings">The device's one ring allocator, for the segment count and the write path.</param>
        /// <param name="description">What the seam asked for.</param>
        /// <param name="loss">The device's device-loss latch, or null on a path that has none. It reaches exactly
        /// one place from here: the ring's mapping mechanism, whose <c>Map</c> is a decision G3 check site
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/500).</param>
        internal D3D11Buffer(ID3D11Device device, ID3D11DeviceContext context, DeviceLiveness liveness,
            D3D11RingAllocator rings, in GpuBufferDescription description, D3D11DeviceLossLatch? loss = null)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(liveness);
            ArgumentNullException.ThrowIfNull(rings);

            _liveness = liveness;
            _rings = rings;
            SizeInBytes = description.SizeInBytes;
            Usage = description.Usage;
            StructureByteStride = description.StructureByteStride;
            Views = D3D11ViewPolicy.ForBuffer(description.Usage);

            Validate(description, Views);

            Buffer = CreateBufferWindows(device, description, Views, rings.FramesInFlight);
            if (Views.Ring)
                Ring = new D3D11UniformRing(rings, new D3D11BufferRingMemory(context, Buffer, loss), SizeInBytes);
            if (Views.ShaderResource) ShaderResourceView = CreateRawSrvWindows(device, Buffer, SizeInBytes);
            if (Views.UnorderedAccess) UnorderedAccessView = CreateRawUavWindows(device, Buffer, SizeInBytes);
        }

        /// <summary>The LOGICAL size, which is what the seam asked for. A ring-backed buffer's native allocation
        /// is larger and nothing outside this type may use that number: a range, a dynamic offset and a write
        /// offset are all against this one.</summary>
        public uint SizeInBytes { get; }

        /// <inheritdoc/>
        /// <remarks>Non-null for exactly the uniform buffers (decision U1). Every write to a ring-backed buffer
        /// goes through it, and no write to any other buffer does.</remarks>
        public D3D11UniformRing? Ring { get; }

        /// <summary>The declared usage, kept because the bind path needs to know a buffer is ring-backed or
        /// structured without re-deriving it.</summary>
        internal GpuBufferUsage Usage { get; }

        /// <summary>The advisory per-element stride. Recorded, never used to shape a view. See the type remarks.</summary>
        internal uint StructureByteStride { get; }

        /// <summary>Which views this buffer carries, decided by <see cref="D3D11ViewPolicy"/>.</summary>
        internal D3D11BufferViewPlan Views { get; }

        /// <summary>The native buffer.</summary>
        internal ID3D11Buffer Buffer { get; }

        /// <summary>The full-range RAW shader resource view, or null when the usage earns none.</summary>
        internal ID3D11ShaderResourceView? ShaderResourceView { get; }

        /// <summary>The full-range RAW unordered access view, or null when the usage earns none.</summary>
        internal ID3D11UnorderedAccessView? UnorderedAccessView { get; }

        /// <summary>True once disposed, whether or not anything native was released.</summary>
        internal bool IsDisposed { get; private set; }

        // ---- ID3D11BindableViews: the 'b' file takes the BUFFER, the other two take a view if it has one ----
        //
        // The constant-buffer member hands back the buffer itself rather than a view, because Direct3D 11 binds a
        // constant buffer as a resource with a window rather than through a view object. A ring-backed buffer
        // hands back the same one native buffer for every frame segment (decision U1): the segment is the
        // first-constant addend the bind computes, never a different resource.

        /// <inheritdoc/>
        object? ID3D11BindableViews.BufferObject => Buffer;

        /// <inheritdoc/>
        object? ID3D11BindableViews.ShaderResourceViewObject => ShaderResourceView;

        /// <inheritdoc/>
        object? ID3D11BindableViews.UnorderedAccessViewObject => UnorderedAccessView;

        /// <inheritdoc/>
        object? ID3D11BindableViews.SamplerStateObject => null;

        // ---- ID3D11MappableResource: what a staging Map needs, answered by the resource ----

        /// <inheritdoc/>
        object ID3D11MappableResource.MapTarget => Buffer;

        /// <inheritdoc/>
        /// <remarks>Staging is the readback case. DYNAMIC and the ring's dynamic buffers are accepted too because
        /// they genuinely carry CPU write access, which keeps the map path's refusal a refusal of the impossible
        /// rather than a divergence from the incumbent.</remarks>
        bool ID3D11MappableResource.IsMappable => Views.Staging || Views.Dynamic || Views.Ring;

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (_liveness.IsDead) return;   // the device already freed every child object

            // THE UNMAP COMES FIRST, and it is not tidiness. Releasing a mapped resource leaves the runtime
            // holding a pointer into memory that no longer belongs to anyone, and leaving a disposed ring in the
            // allocator's registry would have the next submit unmap it a second time.
            if (Ring is not null) _rings.Forget(Ring);

            ShaderResourceView?.Dispose();
            UnorderedAccessView?.Dispose();
            Buffer.Dispose();
        }

        // Everything a wrong answer here corrupts silently rather than fails, so it is checked at creation where
        // the message can name the buffer's own description.
        static void Validate(in GpuBufferDescription description, in D3D11BufferViewPlan views)
        {
            if (description.SizeInBytes == 0)
                throw new ArgumentException("A zero-byte GPU buffer cannot be created.", nameof(description));

            if ((description.Usage & GpuBufferUsage.UniformBuffer) != 0 && description.SizeInBytes % 16 != 0)
            {
                throw new ArgumentException(
                    $"A uniform buffer's size must be a multiple of 16 bytes, and {description.SizeInBytes} is not. "
                    + "Direct3D 11 counts constant-buffer windows in 16-byte constants.",
                    nameof(description));
            }

            if (views.RawViews && description.SizeInBytes % 4 != 0)
            {
                throw new ArgumentException(
                    $"A structured buffer is bound through a RAW byte-address view, so its size must be a multiple "
                    + $"of 4 bytes, and {description.SizeInBytes} is not.",
                    nameof(description));
            }
        }

        // A RING-BACKED BUFFER IS THE ONLY ONE CREATED AT A SIZE THE CALLER DID NOT ASK FOR: its 256-aligned
        // stride times the frame count, DYNAMIC plus CPU write, so a per-frame write is a memcpy into a mapped
        // segment instead of the incumbent's blocking staging map. Everything else is unchanged.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11Buffer CreateBufferWindows(ID3D11Device device, in GpuBufferDescription description,
            in D3D11BufferViewPlan views, int framesInFlight)
        {
            uint sizeInBytes = views.Ring
                ? D3D11UniformRing.TotalBytesFor(description.SizeInBytes, framesInFlight)
                : description.SizeInBytes;

            var d = new BufferDescription((int)sizeInBytes, D3D11Formats.ToBindFlags(views.Bind),
                ResourceUsage.Default);

            if (views.RawViews) d.MiscFlags = ResourceOptionFlags.BufferAllowRawViews;
            if (views.Indirect) d.MiscFlags |= ResourceOptionFlags.DrawIndirectArguments;

            if (views.Ring || views.Dynamic)
            {
                d.Usage = ResourceUsage.Dynamic;
                d.CPUAccessFlags = CpuAccessFlags.Write;
            }
            else if (views.Staging)
            {
                d.Usage = ResourceUsage.Staging;
                d.CPUAccessFlags = CpuAccessFlags.Read | CpuAccessFlags.Write;
            }

            return device.CreateBuffer(d);
        }

        // R32_Typeless plus the Raw flag, counted in 4-byte elements. That pair IS what makes it a byte-address
        // view, which is what the cross-compiled HLSL declares.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11ShaderResourceView CreateRawSrvWindows(ID3D11Device device, ID3D11Buffer buffer, uint sizeInBytes)
        {
            var d = new ShaderResourceViewDescription(buffer, Format.R32_Typeless, 0, (int)(sizeInBytes / 4),
                BufferExtendedShaderResourceViewFlags.Raw);
            return device.CreateShaderResourceView(buffer, d);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11UnorderedAccessView CreateRawUavWindows(ID3D11Device device, ID3D11Buffer buffer, uint sizeInBytes)
        {
            var d = new UnorderedAccessViewDescription(buffer, Format.R32_Typeless, 0, (int)(sizeInBytes / 4),
                BufferUnorderedAccessViewFlags.Raw);
            return device.CreateUnorderedAccessView(buffer, d);
        }
    }
}
