using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// WHAT IS CURRENTLY MAPPED, AND THE ARITHMETIC A MAP ANSWERS WITH, with no device under either. The seam's
    /// <c>IGpuDevice.Map</c> and <c>Unmap</c> pair is the readback path (<c>GpuReadback</c> wraps the whole
    /// staging-copy-map-unmap sequence), and everything about it that can be wrong without a GPU lives here: which
    /// subresource a map names, what <see cref="MappedData.RowPitch"/> and <see cref="MappedData.SizeInBytes"/>
    /// mean for a texture versus a buffer, and the two refusals a caller can earn.
    /// <para>
    /// THE TWO REFUSALS ARE WORTH HAVING RATHER THAN LEAVING TO THE RUNTIME. Direct3D 11 answers a second
    /// <c>Map</c> of an already-mapped subresource with a failed HRESULT and a debug-layer message, and an
    /// <c>Unmap</c> of something that was never mapped with nothing at all. Both are ordering mistakes in the
    /// CALLER, and both are silent in a release build, so a readback that quietly returns the previous frame's
    /// pixels is the shape they take in the field. Refusing by name here makes each one a named exception on every
    /// platform and a plain <c>[Fact]</c> on this machine.
    /// </para>
    /// <para>
    /// A BUFFER'S ROW PITCH IS ITS SIZE, which is what the seam already documents (<c>IGpuDevice.Map</c>: "RowPitch
    /// is meaningless for a buffer, it equals the size"). It is answered that way rather than as zero because
    /// <c>GpuReadback.ReadBuffer</c> and the Veldrid path both read it, and a zero would turn a stride into a
    /// division by nothing.
    /// </para>
    /// <para>
    /// ONE PER DEVICE, and NOT thread-safe by itself. The submit lock is what serializes it: decision W4 has
    /// staging <c>Map</c> and <c>Unmap</c> take that lock for the duration of the map call and nothing longer, so
    /// two threads mapping two staging resources are serialized against each other and against a replay. See
    /// <see cref="D3D11StagingAccess"/>, which is where that lock is taken, and the package README's threading
    /// section for the contract.
    /// </para>
    /// </summary>
    internal sealed class D3D11StagingMaps
    {
        readonly Dictionary<object, GpuMapMode> _open = new(ReferenceEqualityComparer.Instance);

        /// <summary>How many mappings are open right now. Zero at every frame boundary in a correct consumer, and
        /// the number a leak check reads.</summary>
        internal int OpenCount => _open.Count;

        /// <summary>Whether <paramref name="resource"/> is mapped right now.</summary>
        internal bool IsMapped(object resource) => _open.ContainsKey(resource);

        /// <summary>
        /// Take the bookkeeping for a map of <paramref name="resource"/>, refusing a second one. Called INSIDE the
        /// submit lock and BEFORE the native <c>Map</c>, so a refusal costs no driver call at all.
        /// </summary>
        internal void Open(object resource, GpuMapMode mode)
        {
            ArgumentNullException.ThrowIfNull(resource);

            if (_open.TryGetValue(resource, out GpuMapMode existing))
            {
                throw new InvalidOperationException(
                    $"This staging resource is already mapped for {existing} on the native Direct3D 11 backend, so "
                    + $"a second map for {mode} would fail in the runtime and return a null pointer. Unmap the "
                    + "first mapping before taking another. A readback is Map, read, Unmap, and holding one open "
                    + "across a frame also blocks every copy into it.");
            }

            _open.Add(resource, mode);
        }

        /// <summary>
        /// Release the bookkeeping for a map, refusing an unmap of something that was never mapped. Called INSIDE
        /// the submit lock and BEFORE the native <c>Unmap</c>, for the same reason.
        /// </summary>
        internal void Close(object resource)
        {
            ArgumentNullException.ThrowIfNull(resource);

            if (_open.Remove(resource)) return;

            throw new InvalidOperationException(
                "This staging resource is not mapped on the native Direct3D 11 backend, so there is nothing to "
                + "unmap. Direct3D 11 ignores the call silently, which is how an unbalanced pair turns into a "
                + "readback that returns the previous contents rather than into a failure anyone sees.");
        }

        /// <summary>
        /// FORGET EVERY OPEN MAPPING, for device teardown and for the device-loss latch. Neither can unmap
        /// anything: after the device is gone the mappings do not exist, and re-issuing an <c>Unmap</c> against a
        /// dead device is exactly the release-against-freed-memory that decision X3's liveness token exists to
        /// stop. Answering how many were open is what lets teardown say so.
        /// </summary>
        internal int Forget()
        {
            int open = _open.Count;
            _open.Clear();
            return open;
        }

        /// <summary>
        /// THE MAPPED WINDOW FOR A TEXTURE: the pointer the runtime handed back, the row pitch it chose, and the
        /// total size that pitch implies for the subresource's rows.
        /// <para>
        /// THE SIZE IS PITCH TIMES HEIGHT rather than the texture's own byte count, and the difference is the whole
        /// reason <see cref="MappedData.RowPitch"/> is on the seam at all. Direct3D 11 pads each row of a mapped
        /// staging texture up to its own alignment, so a 300-pixel-wide RGBA texture is commonly handed back at a
        /// 1280-byte pitch rather than 1200, and a reader that walked it as packed rows would skew the image by
        /// four pixels per row. <c>GpuReadback</c> already unpacks by pitch, which is what makes that correct.
        /// </para>
        /// </summary>
        internal static MappedData ForTexture(IntPtr data, uint rowPitchBytes, uint height)
            => new(data, rowPitchBytes, checked(rowPitchBytes * Math.Max(height, 1u)));

        /// <summary>
        /// THE MAPPED WINDOW FOR A BUFFER: a flat byte range, with the row pitch answering the size. See the type
        /// remarks for why it is the size rather than zero.
        /// </summary>
        internal static MappedData ForBuffer(IntPtr data, uint sizeInBytes) => new(data, sizeInBytes, sizeInBytes);

        /// <summary>
        /// <c>D3D11CalcSubresource</c> for the mip 0, layer 0 subresource every staging map on this seam names.
        /// The seam's <c>Map(IGpuTexture, GpuMapMode)</c> carries no mip and no layer, exactly as
        /// <c>ResolveTexture</c> carries none, so subresource zero is the whole of what can be asked for and the
        /// constant is named rather than written as a literal at the call.
        /// </summary>
        internal const int Subresource = 0;

        /// <summary>The site name the device-loss latch records for this path. A constant rather than a literal at
        /// the call, so the string in a telemetry session header and the string a test asserts are one thing.
        /// </summary>
        internal const string MapSite = "a staging Map";

        /// <summary>
        /// DECISION G3'S SECOND CHECK SITE, AND WHY IT IS IN THE DEVICE-FREE HALF. <c>ID3D11DeviceContext::Map</c>
        /// hands failure back as an HRESULT rather than as a throw, so a caller that ignored it would read through
        /// whatever pointer the failed call left behind, which is null, and the readback would come out as an
        /// empty image with nothing logged. This is the one place that result is interpreted.
        /// <para>
        /// THE LATCH IS ASKED FIRST AND BEFORE ANYTHING ELSE AT ALL, which is the immediacy clause of G3 rather
        /// than an ordering preference: <c>DXGI_ERROR_DEVICE_REMOVED</c> is sticky, so the reason is only
        /// meaningful at the first site that notices, and building an exception message ahead of the check would
        /// put a call between the fault and <c>GetDeviceRemovedReason</c>.
        /// </para>
        /// <para>
        /// A NULL LATCH SKIPS THE CHECK AND STILL THROWS. That is the state until the device row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/497) wires one, and it is the honest degradation: a
        /// failed map is still a failed map, and the only thing missing is the attribution.
        /// </para>
        /// </summary>
        internal static void RequireMapped(int hresult, D3D11DeviceLossLatch? loss)
        {
            if (!D3D11DeviceLossCodes.IsFailure(hresult)) return;

            bool lost = loss?.Check(hresult, MapSite) ?? false;

            throw new InvalidOperationException(
                $"Mapping a staging resource on the native Direct3D 11 backend failed with "
                + $"{D3D11DeviceLossCodes.Token(hresult)}. "
                + (lost
                    ? "The device has been LOST, and the reason is in this session's telemetry header. Nothing "
                    + "further on this device will succeed."
                    : "The mapped pointer a failed map hands back is null, so this throws rather than letting a "
                    + "reader walk it and report an empty readback."));
        }
    }
}
