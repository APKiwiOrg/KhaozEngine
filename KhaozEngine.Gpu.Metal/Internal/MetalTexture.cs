using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SEAM'S <see cref="IGpuTexture"/>, WHICH IS TWO DIFFERENT OBJECTS BEHIND ONE INTERFACE. A non-staging
    /// texture is a <see cref="MTLStorageMode.Private"/> <c>MTLTexture</c> (M-M2). A
    /// <see cref="GpuTextureUsage.Staging"/> texture is a <see cref="MTLStorageMode.Shared"/> <c>MTLBuffer</c>
    /// carrying the software subresource layout of M-C5, with no <c>MTLTexture</c> at all, and that is the
    /// incumbent's own shape rather than a simplification of it.
    ///
    /// <para><b>THE STAGING BUFFER'S SIZE IS THE HIGHEST-RISK NUMBER IN THIS FILE and it is not computed here.</b>
    /// <see cref="MetalStagingLayout"/> owns every byte of that arithmetic and a device-free table test pins it
    /// against the incumbent's own formulas, because every golden reads back through it and a different answer
    /// garbles all 36 at once.</para>
    ///
    /// <para><b>NO VIEW IS CREATED, FOR ANY USAGE (M-M10).</b> <see cref="MetalViewPolicy"/> carries the whole
    /// argument: the seam cannot narrow a texture by mip, layer or format, so every case is the branch where the
    /// incumbent uses the target's own texture, and the package declares no view factory at all.</para>
    ///
    /// <para><b>CREATION ISSUES NO COMMAND BUFFER (M-M9), and there is no creation-time clear either.</b> Phase
    /// 3 answered the undefined-initial-contents question with a deliberate clear, and here it is answered by
    /// parity instead. The incumbent did not clear, the 36 committed <c>metal</c> goldens are green under that,
    /// and adding a clear would change what a render target reads before anything writes it.</para>
    /// </summary>
    internal sealed class MetalTexture : IGpuTexture, IMetalOwnedResource, IMetalBindable
    {
        readonly IDeviceLiveness _liveness;
        readonly MTLTexture _texture;
        readonly MTLBuffer _stagingBuffer;
        readonly IntPtr _stagingContents;
        readonly IDisposable? _memoryLease;
        readonly IMetalResourceNative? _native;

        bool _disposed;

        MetalTexture(IDeviceLiveness liveness, MTLTexture texture, MTLBuffer stagingBuffer,
            IntPtr stagingContents, in GpuTextureDescription description, in MetalTextureViewPlan plan,
            IMetalResourceNative? native = null, MetalMemoryDiagnostics? memory = null)
        {
            _liveness = liveness;
            _texture = texture;
            _stagingBuffer = stagingBuffer;
            _stagingContents = stagingContents;

            Width = description.Width;
            Height = description.Height;
            MipLevels = description.MipLevels;
            ArrayLayers = description.ArrayLayers;
            SampleCount = description.SampleCount;
            Format = description.Format;
            Usage = description.Usage;
            Plan = plan;
            _native = native;
            _memoryLease = memory?.TrackTexture(description);
        }

        /// <inheritdoc/>
        public uint Width { get; }

        /// <inheritdoc/>
        public uint Height { get; }

        /// <inheritdoc/>
        public uint MipLevels { get; }

        /// <inheritdoc/>
        public uint SampleCount { get; }

        /// <inheritdoc/>
        public GpuPixelFormat Format { get; }

        /// <summary>The array layer count. Not on the seam's interface and needed by every subresource
        /// calculation, so it is carried rather than re-derived.</summary>
        internal uint ArrayLayers { get; }

        /// <summary>The usage the texture was created with.</summary>
        internal GpuTextureUsage Usage { get; }

        /// <inheritdoc/>
        public IDeviceLiveness Owner => _liveness;

        /// <summary>The creation plan, decided once (M-M10).</summary>
        internal MetalTextureViewPlan Plan { get; }

        /// <summary>True for a staging texture, which has a <see cref="StagingBuffer"/> and no
        /// <see cref="Handle"/>.</summary>
        internal bool IsStaging => Plan.Staging;

        /// <summary>The native texture, or a nil handle on a staging texture and after disposal.</summary>
        internal MTLTexture Handle => _disposed ? default : _texture;

        /// <summary>The staging buffer, or a nil handle on a non-staging texture and after disposal.</summary>
        internal MTLBuffer StagingBuffer => _disposed ? default : _stagingBuffer;

        /// <summary>The staging buffer's persistently mapped base pointer, taken once at creation.</summary>
        internal IntPtr StagingContents => _disposed ? IntPtr.Zero : _stagingContents;

        /// <inheritdoc/>
        /// <remarks>The guarded <see cref="Handle"/>, read at the bind rather than copied into a resource set at
        /// its creation. See <see cref="IMetalBindable"/>.</remarks>
        IntPtr IMetalBindable.BindHandle => Handle.Handle;

        /// <inheritdoc/>
        /// <remarks>Null always: a texture has no uniform ring, and a resource set binds one whole.</remarks>
        MetalUniformRing? IMetalBindable.BindRing => null;

        /// <summary>The shape <see cref="MetalStagingLayout"/> computes against. Meaningful on a staging texture
        /// only, and correct on any texture, because the arithmetic is a function of the description.</summary>
        internal MetalStagingShape Shape => new(Width, Height, MipLevels, ArrayLayers, Format);

        /// <summary>Create one: a Private <c>MTLTexture</c>, or a Shared <c>MTLBuffer</c> for a staging
        /// texture.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalTexture Create(MTLDevice device, IDeviceLiveness liveness,
            in GpuTextureDescription description, MetalMemoryDiagnostics? memory = null)
            => Create(new MetalResourceNative(device), liveness, description, memory);

        internal static MetalTexture Create(IMetalResourceNative native, IDeviceLiveness liveness,
            in GpuTextureDescription description, MetalMemoryDiagnostics? memory = null)
        {
            MetalTextureViewPlan plan = MetalViewPolicy.ForTexture(
                description.Usage, description.ArrayLayers, description.SampleCount, description.IsArray);

            return plan.Staging
                ? CreateStaging(native, liveness, description, plan, memory)
                : CreateDeviceTexture(native, liveness, description, plan, memory);
        }

        static MetalTexture CreateStaging(IMetalResourceNative native, IDeviceLiveness liveness,
            in GpuTextureDescription description, in MetalTextureViewPlan plan, MetalMemoryDiagnostics? memory)
        {
            var shape = new MetalStagingShape(description.Width, description.Height, description.MipLevels,
                description.ArrayLayers, description.Format);

            // The incumbent walked every mip level and multiplied by the layer count. This is that walk, with the
            // ceiling the incumbent silently wrapped at turned into a named refusal.
            ulong total = MetalStagingLayout.TotalBytes(shape);

            MTLBuffer buffer = native.NewBuffer((nuint)total);
            if (buffer.IsNull)
            {
                throw new InvalidOperationException(
                    "The native Metal device would not allocate the " + total
                    + "-byte Shared buffer behind a staging texture. A staging texture is an MTLBuffer on this "
                    + "backend (M-C5), so this is an allocation failure rather than an unsupported texture "
                    + "shape.");
            }

            try
            {
                IntPtr contents = native.Contents(buffer);
                if (contents == IntPtr.Zero)
                    throw new InvalidOperationException(
                        "A Shared MTLBuffer behind a staging texture answered a null -contents pointer, which cannot "
                        + "happen for the storage mode this backend allocates it with.");
                return new MetalTexture(liveness, default, buffer, contents, description, plan, native, memory);
            }
            catch { native.Release(buffer.Handle); throw; }
        }

        static MetalTexture CreateDeviceTexture(IMetalResourceNative native, IDeviceLiveness liveness,
            in GpuTextureDescription description, in MetalTextureViewPlan plan, MetalMemoryDiagnostics? memory)
        {
            MTLTexture texture = native.NewTexture(description, plan);
            if (texture.IsNull)
                throw new InvalidOperationException("The native Metal device refused a texture of " + description.Width
                    + " by " + description.Height + " in " + description.Format + " with usage " + description.Usage
                    + " at " + description.SampleCount + " samples. -newTextureWithDescriptor: answers nil for "
                    + "a descriptor the device cannot satisfy as well as for an allocation failure.");
            try { return new MetalTexture(liveness, texture, default, IntPtr.Zero, description, plan, native, memory); }
            catch { native.Release(texture.Handle); throw; }
        }

        /// <summary>One subresource's layout in the staging buffer. Refuses non-staging textures,
        /// which have no software layout to map.</summary>
        internal MetalSubresourceLayout SubresourceLayout(uint mipLevel, uint arrayLayer)
        {
            if (!IsStaging)
            {
                throw new InvalidOperationException(
                    "That native Metal texture is not a staging texture, so it has no software subresource "
                    + "layout: it is a Private MTLTexture with no CPU-visible memory behind it at all. Copy it "
                    + "into a staging texture and map that.");
            }

            return MetalStagingLayout.For(Shape, mipLevel, arrayLayer);
        }

        /// <summary>
        /// The mapped view of subresource 0, which is what <c>IGpuDevice.Map(IGpuTexture, ...)</c> hands back: the
        /// staging buffer's base pointer advanced by the subresource offset, that subresource's row pitch and its
        /// size. Reproduces <c>MTLGraphicsDevice.MapTexture</c> field for field.
        /// </summary>
        internal unsafe MappedData Mapped(uint subresource)
        {
            MetalStagingLayout.MipLevelAndArrayLayer(subresource, MipLevels, out uint mipLevel, out uint layer);
            MetalSubresourceLayout layout = SubresourceLayout(mipLevel, layer);

            return new MappedData(
                (IntPtr)((byte*)StagingContents + layout.Offset),
                (uint)layout.RowPitch,
                (uint)layout.Size);
        }

        /// <summary>
        /// Release the texture, or the staging buffer, once, and never on a dead device (M-F6). No retire list is
        /// needed for the same reason <see cref="MetalBuffer"/> needs none (M-H3).
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _memoryLease?.Dispose();

            if (_liveness.IsDead) return;
            if (_native is not null) { _native.Release(IsStaging ? _stagingBuffer.Handle : _texture.Handle); return; }
            if (!KhaozEngineMetal.IsPlatformSupported) return;

            ReleaseOnMacOs();
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ReleaseOnMacOs()
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            // Exactly one of the two exists, which is the incumbent's own if/else in DisposeCore.
            if (!_stagingBuffer.IsNull) _stagingBuffer.Release();
            else _texture.Release();
        }
    }
}
