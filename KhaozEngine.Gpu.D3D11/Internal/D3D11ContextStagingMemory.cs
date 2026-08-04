using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE SHIPPED <see cref="ID3D11StagingMemory"/>: <c>ID3D11DeviceContext.Map</c> and <c>Unmap</c> at
    /// subresource 0, and nothing else. Four native calls, which is the entire point of the interface it
    /// implements, and the same shape <see cref="D3D11BufferRingMemory"/> takes for the ring's two.
    /// <para>
    /// THE <c>MappedSubresource</c> NEVER BECOMES A FIELD. It is a Vortice VALUE TYPE, and a field of one would
    /// make the CLR resolve the interop assembly merely to compute this type's layout, which puts Vortice on the
    /// load path of every macOS and Linux test run. It is a local inside a guarded body, and what leaves is an
    /// <see cref="IntPtr"/>, a <see cref="uint"/> and an <c>HRESULT</c> as a plain <see cref="int"/>. That is the
    /// package's standing rule and this is one of the places it would be easiest to break.
    /// </para>
    /// <para>
    /// THE CONTEXT IS BORROWED AND SO IS EVERY RESOURCE. The device owns its immediate context,
    /// <see cref="D3D11Buffer"/> and <see cref="D3D11Texture"/> own their handles, so this type releases nothing
    /// and its whole state is one reference.
    /// </para>
    /// <para>
    /// THE SUBMIT LOCK IS THE CALLER'S. <see cref="D3D11StagingAccess"/> takes it around each of these calls and
    /// nothing longer (decision W4), so nothing here has to think about it. That placement is what the lock tests
    /// drive through the seam rather than through this type.
    /// </para>
    /// <para>
    /// A FAILED MAP ANSWERS ITS HRESULT AND NOTHING ELSE. The pointer and the pitch are read only after the
    /// result is known good, because a failed map fills neither in, and what a failure MEANS (including decision
    /// G3's latch) is <see cref="D3D11StagingMaps.RequireMapped"/> on the device-free side.
    /// </para>
    /// </summary>
    internal sealed class D3D11ContextStagingMemory : ID3D11StagingMemory
    {
        readonly ID3D11DeviceContext _context;

        /// <summary>Build the map mechanism for one device. The context is borrowed, never disposed here.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal D3D11ContextStagingMemory(ID3D11DeviceContext context)
            => _context = context ?? throw new ArgumentNullException(nameof(context));

        /// <inheritdoc/>
        public int MapBuffer(object buffer, GpuMapMode mode, out IntPtr data)
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("staging map");

            return MapWindows(buffer, mode, out data, out _);
        }

        /// <inheritdoc/>
        public void UnmapBuffer(object buffer)
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("staging map");

            UnmapWindows(buffer);
        }

        /// <inheritdoc/>
        public int MapTexture(object texture, GpuMapMode mode, out IntPtr data, out uint rowPitchBytes)
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("staging map");

            return MapWindows(texture, mode, out data, out rowPitchBytes);
        }

        /// <inheritdoc/>
        public void UnmapTexture(object texture)
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("staging map");

            UnmapWindows(texture);
        }

        /// <summary>
        /// The Direct3D map mode one seam mode asks for. Read-write is the general form and read is the readback
        /// one, and <see cref="GpuMapMode.Write"/> maps <c>WRITE_DISCARD</c> rather than plain <c>WRITE</c>,
        /// matching the incumbent: a plain write map on a DYNAMIC resource stalls until the GPU is finished with
        /// it, and discard is the form every write-mapping caller in the engine actually wants.
        /// <para>
        /// WINDOWS-ONLY BY ATTRIBUTE even though nothing about the switch needs a device, because its RETURN type
        /// is a Vortice value type: a device-free caller would resolve the interop assembly merely by calling it,
        /// which is what the package's load-path guard asserts never happens. The attribute turns that into a
        /// build error in a project that is not Windows-only rather than a load nobody notices.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("windows")]
        internal static MapMode ToMapMode(GpuMapMode mode) => mode switch
        {
            GpuMapMode.Read => MapMode.Read,
            GpuMapMode.Write => MapMode.WriteDiscard,
            GpuMapMode.ReadWrite => MapMode.ReadWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode,
                "Unmapped GpuMapMode in a native Direct3D 11 staging map."),
        };

        // THE ONE NATIVE MAP, and the whole of what this type carries that a device-free test cannot reach.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        int MapWindows(object resource, GpuMapMode mode, out IntPtr data, out uint rowPitchBytes)
        {
            // A local, never a field: a SharpGen value-type FIELD would resolve the interop assembly the moment
            // this type is loaded, and the load-path guard asserts process-wide that nothing pulls it in off
            // Windows.
            var result = _context.Map(RequireResource(resource), D3D11StagingMaps.Subresource, ToMapMode(mode),
                MapFlags.None, out MappedSubresource mapped);

            // THE POINTER AND THE PITCH ARE READ ONLY ONCE THE RESULT IS KNOWN GOOD, because a failed map fills
            // neither in. The result itself goes back untouched: interpreting it (and asking the device-loss
            // latch, decision G3) is D3D11StagingMaps.RequireMapped, which is device-free and therefore testable.
            if (D3D11DeviceLossCodes.IsFailure(result.Code))
            {
                data = IntPtr.Zero;
                rowPitchBytes = 0u;
                return result.Code;
            }

            data = mapped.DataPointer;
            rowPitchBytes = (uint)mapped.RowPitch;
            return result.Code;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void UnmapWindows(object resource)
            => _context.Unmap(RequireResource(resource), D3D11StagingMaps.Subresource);

        // The one cast, at the Windows boundary. A resource this backend created answers ID3D11MappableResource
        // with its own native handle, so anything failing here is a wiring mistake inside the package rather than
        // a caller's, and it says so in those terms.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static ID3D11Resource RequireResource(object resource)
            => resource as ID3D11Resource
                ?? throw new ArgumentException(
                    $"A {(resource is null ? "null" : resource.GetType().Name)} reached the native Direct3D 11 "
                    + "staging map as a map target. ID3D11MappableResource.MapTarget answers the ID3D11Resource a "
                    + "Map names, so this is a wiring mistake in the backend rather than in the caller.",
                    nameof(resource));
    }
}
