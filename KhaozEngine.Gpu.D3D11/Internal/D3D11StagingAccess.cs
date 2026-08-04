using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE STAGING MAP PATH: the seam's four <c>IGpuDevice.Map</c> and <c>Unmap</c> members turned into
    /// <c>ID3D11DeviceContext.Map</c> and <c>Unmap</c>, under the submit lock for the duration of that call and
    /// nothing longer (decision W4). This is the readback half of the backend, and it is what
    /// <c>GpuReadback.ToRgba</c> and <c>GpuReadback.ReadBuffer</c> stand on.
    /// <para>
    /// EVERYTHING THAT CAN BE WRONG WITHOUT A GPU IS DEVICE-FREE, which is the split every type in this package
    /// takes, and after the seam extraction that includes THIS TYPE. The registry and the arithmetic are
    /// <see cref="D3D11StagingMaps"/>: the double-map refusal, the unmap-without-map refusal, the row pitch, and
    /// what a failed HRESULT means. What is left here is the ORDER of those steps and the lock around each native
    /// call, and the native calls themselves sit behind <see cref="ID3D11StagingMemory"/>. So this type is
    /// constructible off Windows with a fake seam, and the lock clause below is an assertion rather than prose.
    /// </para>
    ///
    /// <para><b>THE LOCK IS TAKEN FOR THE MAP CALL AND NOTHING LONGER, and that is the whole of the staging clause
    /// of the threading contract.</b> It is NOT held across the caller's read: a readback that held it from
    /// <c>Map</c> to <c>Unmap</c> would block every submit for as long as a consumer walked the pixels, which is
    /// the frame-long hold this design exists to delete. So two calls take the lock twice, and between them the
    /// mapped pointer is the caller's alone. Both halves are pinned by a fake recording
    /// <c>Monitor.IsEntered</c> per call, the same shape <c>FakeD3D11Ring</c> and <c>FakeD3D11FenceTimeline</c>
    /// use for their own seams.</para>
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
    /// <para><b>THE DEVICE-LOSS CHECK SITE OF DECISION G3 IS DEFINED HERE and the device wires it.</b>
    /// <see cref="D3D11DeviceLossLatch"/> names the staging map as one of its sites, and this is the
    /// shape: the latch arrives OPTIONAL through the constructor, the site string is the constant
    /// <see cref="D3D11StagingMaps.MapSite"/>, and the HRESULT the seam's map answers goes straight to
    /// <see cref="D3D11StagingMaps.RequireMapped"/>, which asks the latch before it builds anything. It is the
    /// HRESULT form rather than #489's fault form because Vortice's <c>Map</c> returns a result rather than
    /// throwing, which is exactly the shape G3 named for this site. A null latch, which is every device-free
    /// test, skips the attribution and still throws.</para>
    ///
    /// <para><b>WHAT THE DEVICE CONSTRUCTS</b> is
    /// <c>new D3D11StagingAccess(new D3D11ContextStagingMemory(context), submitLock, latch)</c>. The immediate
    /// context is named by the Windows implementation rather than by this type, so the Vortice reference stops one
    /// class earlier and nothing else about the call site changes.</para>
    /// </summary>
    internal sealed class D3D11StagingAccess
    {
        readonly ID3D11StagingMemory _memory;
        readonly object _submitLock;
        readonly D3D11DeviceLossLatch? _loss;

        /// <param name="memory">The four native calls. <see cref="D3D11ContextStagingMemory"/> over the device's
        /// one immediate context in production, a recording fake in the lock tests.</param>
        /// <param name="submitLock">The device's single submit lock (decision W4). Not created here, for the same
        /// reason the ring allocator and the fence subsystem do not create theirs: there is exactly one of it and
        /// it belongs to the device.</param>
        /// <param name="loss">The device's device-loss latch, or null on a path that has none. See the type
        /// remarks for the check-site shape.</param>
        internal D3D11StagingAccess(ID3D11StagingMemory memory, object submitLock,
            D3D11DeviceLossLatch? loss = null)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
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
        internal MappedData Map(IGpuBuffer staging, GpuMapMode mode)
        {
            ID3D11MappableResource buffer = RequireBuffer(staging);
            RequireMappable(buffer.IsMappable, "buffer");

            lock (_submitLock)
            {
                Maps.Open(staging, mode);
                try
                {
                    int hresult = _memory.MapBuffer(buffer.MapTarget, mode, out IntPtr data);
                    D3D11StagingMaps.RequireMapped(hresult, _loss);
                    return D3D11StagingMaps.ForBuffer(data, staging.SizeInBytes);
                }
                catch
                {
                    // ROLL THE REGISTRY BACK. A map that failed left nothing mapped, so leaving the record would
                    // make the caller's next attempt look like a double map and refuse it for the wrong reason.
                    Maps.Close(staging);
                    throw;
                }
            }
        }

        /// <summary>Release a staging buffer's mapping.</summary>
        internal void Unmap(IGpuBuffer staging)
        {
            ID3D11MappableResource buffer = RequireBuffer(staging);

            lock (_submitLock)
            {
                Maps.Close(staging);
                _memory.UnmapBuffer(buffer.MapTarget);
            }
        }

        /// <summary>
        /// Map a staging TEXTURE at subresource 0. The row pitch is whatever the runtime chose, which is commonly
        /// larger than the packed row width, and <see cref="MappedData.SizeInBytes"/> follows it rather than the
        /// texture's own byte count. See <see cref="D3D11StagingMaps.ForTexture"/> for why that distinction is the
        /// reason the seam carries a row pitch at all.
        /// </summary>
        internal MappedData Map(IGpuTexture staging, GpuMapMode mode)
        {
            ID3D11MappableResource texture = RequireTexture(staging);
            RequireMappable(texture.IsMappable, "texture");

            lock (_submitLock)
            {
                Maps.Open(staging, mode);
                try
                {
                    int hresult = _memory.MapTexture(texture.MapTarget, mode, out IntPtr data,
                        out uint rowPitchBytes);
                    D3D11StagingMaps.RequireMapped(hresult, _loss);
                    return D3D11StagingMaps.ForTexture(data, rowPitchBytes, staging.Height);
                }
                catch
                {
                    Maps.Close(staging);
                    throw;
                }
            }
        }

        /// <summary>Release a staging texture's mapping.</summary>
        internal void Unmap(IGpuTexture staging)
        {
            ID3D11MappableResource texture = RequireTexture(staging);

            lock (_submitLock)
            {
                Maps.Close(staging);
                _memory.UnmapTexture(texture.MapTarget);
            }
        }

        // THE CAST IS TO THE CAPABILITY SEAM, NOT TO D3D11Buffer, and that is what keeps this refusal device-free.
        // A cast to the concrete type would be a cast to a Windows-only one, so no test off Windows could reach
        // past it and the whole map path would be Windows residue again. The message is unchanged: what a caller
        // got wrong is handing this backend another backend's buffer.
        static ID3D11MappableResource RequireBuffer(IGpuBuffer? staging)
            => staging as ID3D11MappableResource
                ?? throw new ArgumentException(
                    $"A {(staging is null ? "null" : staging.GetType().Name)} was handed to the native Direct3D 11 "
                    + "backend as a staging buffer. A buffer this backend created carries the ID3D11Buffer a map "
                    + "names, and a buffer from another backend carries another backend's.", nameof(staging));

        static ID3D11MappableResource RequireTexture(IGpuTexture? staging)
            => staging as ID3D11MappableResource
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
    }
}
