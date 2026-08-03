using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE STAGING MAP PATH: the seam's four <c>IGpuDevice.Map</c> and <c>Unmap</c> members turned into
    /// <c>ID3D11DeviceContext.Map</c> and <c>Unmap</c>, under the submit lock for the duration of that call and
    /// nothing longer (decision W4). This is the readback half of the backend, and it is what
    /// <c>GpuReadback.ToRgba</c> and <c>GpuReadback.ReadBuffer</c> stand on.
    /// <para>
    /// EVERYTHING THAT CAN BE WRONG WITHOUT A GPU IS IN <see cref="D3D11StagingMaps"/>, which is the split every
    /// type in this package takes: the double-map refusal, the unmap-without-map refusal, and the row-pitch
    /// arithmetic that turns a padded staging texture into a <see cref="MappedData"/> a reader can walk. What is
    /// left here is the two native calls, the lock around each of them, and the device-loss check site.
    /// </para>
    ///
    /// <para><b>THE LOCK IS TAKEN FOR THE MAP CALL AND NOTHING LONGER, and that is the whole of the staging clause
    /// of the threading contract.</b> It is NOT held across the caller's read: a readback that held it from
    /// <c>Map</c> to <c>Unmap</c> would block every submit for as long as a consumer walked the pixels, which is
    /// the frame-long hold this design exists to delete. So two calls take the lock twice, and between them the
    /// mapped pointer is the caller's alone.</para>
    ///
    /// <para><b>THE MAP ITSELF CAN BLOCK WHILE HOLDING IT, and that is the one place the "nothing waits under the
    /// submit lock" rule is knowingly paid rather than enforced.</b> <c>Map(READ)</c> on the immediate context is
    /// defined to wait until the GPU is done with the resource, which is exactly what makes a readback correct
    /// without an explicit drain (section 10.4 names this as one of the three reasons the incumbent's empty
    /// <c>WaitForIdle</c> never caused a known bug). The wait is bounded by the work already submitted against
    /// THAT resource rather than by a frame, and the alternative (mapping with <c>DO_NOT_WAIT</c> and spinning
    /// outside the lock) trades a bounded wait for a spin that can starve. The two members that refuse a caller
    /// holding this lock (<c>WaitForIdle</c> and the ring's <c>BeginFrame</c>) are unbounded in a way this is not.
    /// The package README's threading section says so in the contract rather than leaving it to this file.</para>
    ///
    /// <para><b>THE DEVICE-LOSS CHECK SITE OF DECISION G3 IS DEFINED HERE and wired by the device row
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/497).</b> <see cref="D3D11DeviceLossLatch"/> already names
    /// the staging map as its second site and says the call site belongs to this row, so this row defines the
    /// shape: the latch arrives OPTIONAL through the constructor, the site string is the constant
    /// <see cref="D3D11StagingMaps.MapSite"/>, and the HRESULT the map returns goes straight to
    /// <see cref="D3D11StagingMaps.RequireMapped"/>, which asks the latch before it builds anything. It is the
    /// HRESULT form rather than #489's fault form because Vortice's <c>Map</c> returns a result rather than
    /// throwing, which is exactly the shape G3 named for this site. Wiring it is one argument at the construction
    /// site, and until then a null latch skips the attribution and still throws.</para>
    /// </summary>
    internal sealed class D3D11StagingAccess
    {
        readonly ID3D11DeviceContext _context;
        readonly object _submitLock;
        readonly D3D11DeviceLossLatch? _loss;

        /// <param name="context">The device's one immediate context. Borrowed, never disposed here.</param>
        /// <param name="submitLock">The device's single submit lock (decision W4). Not created here, for the same
        /// reason the ring allocator and the fence subsystem do not create theirs: there is exactly one of it and
        /// it belongs to the device.</param>
        /// <param name="loss">The device's device-loss latch, or null until the device row wires one. See the type
        /// remarks for the check-site shape.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal D3D11StagingAccess(ID3D11DeviceContext context, object submitLock,
            D3D11DeviceLossLatch? loss = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _submitLock = submitLock ?? throw new ArgumentNullException(nameof(submitLock));
            _loss = loss;
        }

        /// <summary>The open-mapping registry, which is where both refusals live and what a leak check reads.
        /// </summary>
        internal D3D11StagingMaps Maps { get; } = new();

        /// <summary>
        /// Map a staging BUFFER. <see cref="MappedData.RowPitch"/> is the size, per the seam, and the window is
        /// the whole buffer: Direct3D 11 maps a buffer as one flat range with no subresource to choose.
        /// </summary>
        [SupportedOSPlatform("windows")]
        internal MappedData Map(IGpuBuffer staging, GpuMapMode mode)
        {
            D3D11Buffer buffer = RequireBuffer(staging);
            RequireMappable(buffer.Views.Staging || buffer.Views.Dynamic || buffer.Views.Ring, "buffer");

            lock (_submitLock)
            {
                Maps.Open(buffer, mode);
                try
                {
                    IntPtr data = MapWindows(buffer.Buffer, mode, out _);
                    return D3D11StagingMaps.ForBuffer(data, buffer.SizeInBytes);
                }
                catch
                {
                    // ROLL THE REGISTRY BACK. A map that failed left nothing mapped, so leaving the record would
                    // make the caller's next attempt look like a double map and refuse it for the wrong reason.
                    Maps.Close(buffer);
                    throw;
                }
            }
        }

        /// <summary>Release a staging buffer's mapping.</summary>
        [SupportedOSPlatform("windows")]
        internal void Unmap(IGpuBuffer staging)
        {
            D3D11Buffer buffer = RequireBuffer(staging);

            lock (_submitLock)
            {
                Maps.Close(buffer);
                UnmapWindows(buffer.Buffer);
            }
        }

        /// <summary>
        /// Map a staging TEXTURE at subresource 0. The row pitch is whatever the runtime chose, which is commonly
        /// larger than the packed row width, and <see cref="MappedData.SizeInBytes"/> follows it rather than the
        /// texture's own byte count. See <see cref="D3D11StagingMaps.ForTexture"/> for why that distinction is the
        /// reason the seam carries a row pitch at all.
        /// </summary>
        [SupportedOSPlatform("windows")]
        internal MappedData Map(IGpuTexture staging, GpuMapMode mode)
        {
            D3D11Texture texture = RequireTexture(staging);
            RequireMappable(texture.Views.Staging, "texture");

            lock (_submitLock)
            {
                Maps.Open(texture, mode);
                try
                {
                    IntPtr data = MapWindows(texture.DeviceTexture, mode, out uint rowPitch);
                    return D3D11StagingMaps.ForTexture(data, rowPitch, texture.Height);
                }
                catch
                {
                    Maps.Close(texture);
                    throw;
                }
            }
        }

        /// <summary>Release a staging texture's mapping.</summary>
        [SupportedOSPlatform("windows")]
        internal void Unmap(IGpuTexture staging)
        {
            D3D11Texture texture = RequireTexture(staging);

            lock (_submitLock)
            {
                Maps.Close(texture);
                UnmapWindows(texture.DeviceTexture);
            }
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

        static D3D11Buffer RequireBuffer(IGpuBuffer? staging)
            => staging as D3D11Buffer
                ?? throw new ArgumentException(
                    $"A {(staging is null ? "null" : staging.GetType().Name)} was handed to the native Direct3D 11 "
                    + "backend as a staging buffer. A buffer this backend created carries the ID3D11Buffer a map "
                    + "names, and a buffer from another backend carries another backend's.", nameof(staging));

        static D3D11Texture RequireTexture(IGpuTexture? staging)
            => staging as D3D11Texture
                ?? throw new ArgumentException(
                    $"A {(staging is null ? "null" : staging.GetType().Name)} was handed to the native Direct3D 11 "
                    + "backend as a staging texture. A texture this backend created carries the ID3D11Texture2D a "
                    + "map names, and a texture from another backend carries another backend's.", nameof(staging));

        // A DEFAULT-usage resource has no CPU access flags at all, so Direct3D 11 fails the map and hands back a
        // null pointer, and a caller that read through it would walk unmapped memory. Refused by name instead, and
        // the refusal names the usage bit rather than the D3D flag, because the usage bit is what the caller
        // passed. Dynamic and the ring's dynamic buffers are accepted because they genuinely are mappable, which
        // keeps this a refusal of the impossible rather than a divergence from the incumbent.
        static void RequireMappable(bool mappable, string what)
        {
            if (mappable) return;

            throw new ArgumentException(
                $"This {what} was not created with GpuBufferUsage.Staging (or GpuTextureUsage.Staging), so it has "
                + "no CPU access and cannot be mapped. Direct3D 11 fails the map and hands back a null pointer, "
                + "which reads as an empty readback rather than as a failure. Copy into a staging resource of its "
                + "own first, which is what GpuReadback does.");
        }

        // THE ONE NATIVE MAP, and the whole of what this type carries that a device-free test cannot reach. The
        // result is HANDED STRAIGHT ON rather than interpreted here: what a failed HRESULT means, and asking the
        // device-loss latch about it, is D3D11StagingMaps.RequireMapped, which is device-free and therefore
        // testable. The row pitch is read only after that returns, because a failed map fills nothing in.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        IntPtr MapWindows(ID3D11Resource resource, GpuMapMode mode, out uint rowPitch)
        {
            // A local, never a field: a SharpGen value-type FIELD would resolve the interop assembly the moment
            // this type is loaded, and the load-path guard asserts process-wide that nothing pulls it in off
            // Windows.
            var result = _context.Map(resource, D3D11StagingMaps.Subresource, ToMapMode(mode), MapFlags.None,
                out MappedSubresource mapped);

            D3D11StagingMaps.RequireMapped(result.Code, _loss);

            rowPitch = (uint)mapped.RowPitch;
            return mapped.DataPointer;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void UnmapWindows(ID3D11Resource resource) => _context.Unmap(resource, D3D11StagingMaps.Subresource);
    }
}
