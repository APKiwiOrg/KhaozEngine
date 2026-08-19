using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The scene's create-upload-and-mip sequences, together in one type because they share the property that
    /// makes them delicate: each builds an expensive GPU resource BEFORE it opens the command list that finishes
    /// it, and that open can be refused.
    /// <para>
    /// A mipped load opens a transient recording through <see cref="GpuRecording"/>, so calling one mid-frame
    /// throws <see cref="GpuNestedRecordingException"/>
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/424">#424</see>). The throw lands after the
    /// texture exists and before the scene has taken ownership of it, which would leave a live GPU texture nothing
    /// can ever free. That matters because of what the refusal is FOR: it is meant to be recoverable, a host
    /// catches it, moves the call into the frame's pre-record phase and carries on, and a host that retries a
    /// streaming load every frame would leak a texture per attempt. So every failure path here frees what it
    /// built, and the caller owns nothing at all when one of these throws.
    /// </para>
    /// </summary>
    internal static class TextureUploads
    {
        /// <summary>Create an RGBA8 texture, upload level 0 from <paramref name="rgba"/>, and generate the mip
        /// chain when <paramref name="mips"/> asks for more than one level. The caller owns the result.</summary>
        internal static IGpuTexture CreateMipped(IGpuDevice gd, byte[] rgba, uint w, uint h, uint mips, string owner)
        {
            // A full mip chain is what stops distant model/prop surfaces from aliasing into "pixely" sparkle when
            // the camera moves (level 0 alone point-minifies at range). The model pass samples through the
            // trilinear LinearSampler, which then has real mips to blend between. A 1-level texture (a 1x1
            // default, or a policy that asked for none) skips the generate so those stay byte-identical, and
            // opens no command list at all, which is what keeps an unmipped load legal mid-frame.
            GpuTextureUsage usage = GpuTextureUsage.Sampled | (mips > 1 ? GpuTextureUsage.GenerateMipmaps : 0);
            IGpuTexture tex = gd.Factory.CreateTexture(
                new GpuTextureDescription(w, h, GpuPixelFormat.R8G8B8A8UNorm, usage, mips));
            try
            {
                gd.UpdateTexture(tex, rgba, 0, 0, w, h);
                if (mips > 1) GenerateMips(gd, owner, tex, null);
            }
            catch
            {
                tex.Dispose();
                throw;
            }
            return tex;
        }

        /// <summary>Create the splat material's two mipped RGBA8 texture arrays (albedo + tangent-space normal,
        /// one layer per <paramref name="layers"/> entry), upload every layer's level 0, and generate both mip
        /// chains in ONE transient command list. Either both come back or neither does.</summary>
        internal static (IGpuTexture Albedo, IGpuTexture Normal) CreateSplatArrays(
            IGpuDevice gd, uint w, uint h, uint mips, IReadOnlyList<SplatLayerImage> layers, string owner)
        {
            const GpuTextureUsage usage = GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps;
            IGpuResourceFactory f = gd.Factory;
            IGpuTexture albedo = f.CreateTexture(GpuTextureDescription.Texture2DArray(
                w, h, GpuPixelFormat.R8G8B8A8UNorm, usage, (uint)layers.Count, mips));
            IGpuTexture normal;
            try
            {
                normal = f.CreateTexture(GpuTextureDescription.Texture2DArray(
                    w, h, GpuPixelFormat.R8G8B8A8UNorm, usage, (uint)layers.Count, mips));
            }
            catch
            {
                albedo.Dispose();
                throw;
            }

            try
            {
                for (int L = 0; L < layers.Count; L++)
                {
                    gd.UpdateTexture(albedo, layers[L].AlbedoRgba, 0, 0, w, h, mipLevel: 0, arrayLayer: (uint)L);
                    gd.UpdateTexture(normal, layers[L].NormalRgba, 0, 0, w, h, mipLevel: 0, arrayLayer: (uint)L);
                }
                GenerateMips(gd, owner, albedo, normal);
            }
            catch
            {
                albedo.Dispose();
                normal.Dispose();
                throw;
            }
            return (albedo, normal);
        }

        /// <summary>Create ONE mipped RGBA8 texture array (one layer per <paramref name="layers"/> entry), upload
        /// every layer's level 0, and generate the mip chain in one transient command list. The albedo-only sibling
        /// of <see cref="CreateSplatArrays"/>, for the tile-ground pass, which ships no normal maps. Same failure
        /// contract: a refused mid-frame open (#424) frees the array, so the caller owns nothing when this
        /// throws.</summary>
        internal static IGpuTexture CreateAlbedoArray(
            IGpuDevice gd, uint w, uint h, uint mips, IReadOnlyList<TileGroundLayerImage> layers, string owner)
        {
            const GpuTextureUsage usage = GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps;
            IGpuTexture albedo = gd.Factory.CreateTexture(GpuTextureDescription.Texture2DArray(
                w, h, GpuPixelFormat.R8G8B8A8UNorm, usage, (uint)layers.Count, mips));
            try
            {
                for (int L = 0; L < layers.Count; L++)
                    gd.UpdateTexture(albedo, layers[L].AlbedoRgba, 0, 0, w, h, mipLevel: 0, arrayLayer: (uint)L);
                GenerateMips(gd, owner, albedo, null);
            }
            catch
            {
                albedo.Dispose();
                throw;
            }
            return albedo;
        }

        // One transient list for the whole generate, opened through the seam's register so a mid-frame call
        // refuses by name instead of resetting the frame's device state (#424). Submitted and drained here: the
        // chain has to exist before the first draw that samples it.
        static void GenerateMips(IGpuDevice gd, string owner, IGpuTexture first, IGpuTexture? second)
        {
            using IGpuCommandList cl = gd.Factory.CreateCommandList();
            using (GpuRecording.Open(gd, cl, owner))
            {
                cl.GenerateMipmaps(first);
                if (second is not null) cl.GenerateMipmaps(second);
            }
            gd.Submit(cl);
            gd.WaitForIdle();
        }
    }
}
