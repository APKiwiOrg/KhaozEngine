using System;
using System.Globalization;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE DEVICE-LEVEL UPLOAD'S SUBRESOURCE BOUND, CHECKED AT THE CALL (#695).
    ///
    /// <para><b>D3D11 ITSELF NEVER SAYS NO.</b> <c>ID3D11DeviceContext.UpdateSubresource</c> takes a flat
    /// subresource index, and an index past the end of the resource is not an <c>HRESULT</c> failure: the retail
    /// runtime drops the call, and only the debug layer (which the golden legs do not arm) prints anything. So a
    /// layer the texture does not have arrives as SILENCE on this backend, while native Metal refuses the same
    /// call by name. That divergence is the whole defect: the seam exists so a caller can develop against one
    /// backend and ship on another, and a phantom layer that throws on macOS and no-ops on Windows breaks exactly
    /// that promise.
    /// </para>
    ///
    /// <para><b>THE BOUND IS SLICES, NOT LOGICAL LAYERS.</b> A cubemap reports its
    /// <see cref="D3D11Texture.ArrayLayers"/> in CUBES and carries six subresource slices per cube, and the
    /// subresource arithmetic the upload and the emitter's copies both use takes the SLICE the caller means. So
    /// the number to check against is <see cref="ArraySlices"/>, which is the same <c>ArraySize</c> the resource
    /// is created with. It lives here rather than inline at the creation site so that the bound and the number
    /// the resource actually has are ONE source, and so that both are reachable without a device: neither this
    /// backend nor Vulkan runs on the machine most of this engine is written on, and the only cheap proof
    /// available before a matrix dispatch is a device-free test of the arithmetic the device path calls.
    /// </para>
    /// </summary>
    internal static class D3D11UploadBounds
    {
        /// <summary>
        /// The REAL subresource slice count a description asks for: the layer count (at least one, which is how
        /// <see cref="D3D11Texture"/> reads a zero) times six on a cubemap, whose layers count cubes.
        /// </summary>
        /// <param name="description">The seam's texture description.</param>
        internal static uint ArraySlices(in GpuTextureDescription description)
        {
            uint layers = description.ArrayLayers == 0 ? 1 : description.ArrayLayers;

            return (description.Usage & GpuTextureUsage.Cubemap) != 0 ? layers * 6 : layers;
        }

        /// <summary>
        /// Refuse an array layer the destination does not have, by the name of the parameter that carried it.
        /// </summary>
        /// <param name="arrayLayer">The destination array slice the caller asked for.</param>
        /// <param name="arraySlices">How many slices the destination actually has, cubemap faces expanded.</param>
        internal static void RequireArrayLayer(uint arrayLayer, uint arraySlices)
        {
            if (arrayLayer < arraySlices) return;

            throw new ArgumentOutOfRangeException(nameof(arrayLayer), arrayLayer,
                "Array layer "
                + arrayLayer.ToString(CultureInfo.InvariantCulture)
                + " is outside a native Direct3D 11 texture with "
                + arraySlices.ToString(CultureInfo.InvariantCulture)
                + " array "
                + (arraySlices == 1 ? "slice." : "slices.")
                + " UpdateSubresource would drop the write without an error, so the bytes would land nowhere and "
                + "the caller would never hear about it.");
        }
    }
}
