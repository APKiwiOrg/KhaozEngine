using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <see cref="IGpuBuffer"/> for the native Direct3D 11 backend: one <c>ID3D11Buffer</c> plus the EAGER views
    /// its declared usage earns, created here at construction and never on the draw path (decision X1).
    /// <para>
    /// STRUCTURED BUFFERS ARE RAW, AND THAT IS DECISION C2 RATHER THAN A SHORTCUT. Both structured kinds get a
    /// <c>DEFAULT</c>-usage buffer with <c>BufferAllowRawViews</c> and a FULL-RANGE byte-address view, and
    /// <see cref="GpuBufferDescription.StructureByteStride"/> stays advisory. SPIRV-Cross emits a GLSL storage
    /// block as a <c>ByteAddressBuffer</c> or an <c>RWByteAddressBuffer</c>, so a stride-shaped structured view
    /// would not be what the compiled shader reads. Keeping this identical to the incumbent is why the ocean
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
    /// Disposal is gated on <see cref="D3D11DeviceLiveness"/>, decision X3: destroying the device already freed
    /// every child object, so a wrapper disposed afterwards must do nothing rather than release twice.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11Buffer : IGpuBuffer
    {
        readonly D3D11DeviceLiveness _liveness;

        internal D3D11Buffer(ID3D11Device device, D3D11DeviceLiveness liveness, in GpuBufferDescription description)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(liveness);

            _liveness = liveness;
            SizeInBytes = description.SizeInBytes;
            Usage = description.Usage;
            StructureByteStride = description.StructureByteStride;
            Views = D3D11ViewPolicy.ForBuffer(description.Usage);

            Validate(description, Views);

            Buffer = CreateBufferWindows(device, description, Views);
            if (Views.ShaderResource) ShaderResourceView = CreateRawSrvWindows(device, Buffer, SizeInBytes);
            if (Views.UnorderedAccess) UnorderedAccessView = CreateRawUavWindows(device, Buffer, SizeInBytes);
        }

        /// <inheritdoc/>
        public uint SizeInBytes { get; }

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

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (_liveness.IsDead) return;   // the device already freed every child object

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

        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11Buffer CreateBufferWindows(ID3D11Device device, in GpuBufferDescription description,
            in D3D11BufferViewPlan views)
        {
            var d = new BufferDescription((int)description.SizeInBytes, D3D11Formats.ToBindFlags(views.Bind),
                ResourceUsage.Default);

            if (views.RawViews) d.MiscFlags = ResourceOptionFlags.BufferAllowRawViews;
            if (views.Indirect) d.MiscFlags |= ResourceOptionFlags.DrawIndirectArguments;

            if (views.Dynamic)
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
