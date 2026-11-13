using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using GltfMaterial = SharpGLTF.Schema2.Material;

namespace KhaozEngine.Render3D
{
    // Material map reading: which material a single-material load reads, how each channel's image is named and
    // decoded once per identity, and the alpha and emissive metadata that ride beside the maps.
    public static partial class GltfLoader
    {
        /// <summary>The first logical material that references at least one of the auto-read texture channels
        /// (BaseColor / Normal / MetallicRoughness, or an Emissive map with a non-zero factor, the only kind that is
        /// read), or the first primitive's material, or null.
        /// Single-material assumption mirrors the loaders flattening all primitives into one mesh: the auto-read path
        /// is a convenience for the common one-material asset, not a multi-material atlas system.</summary>
        static GltfMaterial? FirstTexturedMaterial(ModelRoot root)
        {
            foreach (var m in root.LogicalMaterials)
                if (HasAnyAutoReadTexture(m)) return m;
            // No textured material: fall back to the first primitive's material so an explicit-but-untextured
            // material still resolves (ReadMaterialMaps then just returns all-absent).
            return root.LogicalMeshes.SelectMany(me => me.Primitives).Select(p => p.Material).FirstOrDefault(m => m != null);
        }

        static bool HasAnyAutoReadTexture(GltfMaterial? mat)
        {
            if (mat == null) return false;
            return ChannelImage(mat, "BaseColor") != null
                || ChannelImage(mat, "Normal") != null
                || ChannelImage(mat, "MetallicRoughness") != null
                || (ChannelImage(mat, "Emissive") != null && ReadEmissiveFactor(mat) != Vector3.Zero);
        }

        /// <summary>Decode a material's baseColor / normal / metallicRoughness / emissive textures to raw RGBA8 and
        /// read its alpha cutoff and emissive factor. Each absent or undecodable channel becomes a null map
        /// (graceful degrade, never a throw). metallicRoughness is passed through unchanged (the shader reads
        /// roughness from .g); normal stays tangent-space RGB. <paramref name="assetDirectory"/> is the folder an
        /// external image resolves against, null for a glb read from memory.</summary>
        static GltfMaterialMaps ReadMaterialMaps(GltfMaterial? mat, string? assetDirectory)
        {
            if (mat == null) return default;   // all-null (alpha cutoff 0 = OPAQUE, no emissive)
            Vector3 emissiveFactor = ReadEmissiveFactor(mat);
            return new GltfMaterialMaps(
                DecodeChannel(mat, "BaseColor", assetDirectory),
                DecodeChannel(mat, "Normal", assetDirectory),
                DecodeChannel(mat, "MetallicRoughness", assetDirectory),
                ReadAlphaCutoff(mat),
                emissiveFactor == Vector3.Zero ? null : DecodeChannel(mat, "Emissive", assetDirectory),
                emissiveFactor);
        }

        /// <summary>The alpha-cutout threshold for a glTF material: <c>0</c> for OPAQUE (the spec default when
        /// alphaMode is absent, so nothing is clipped and the render is byte-identical to the pre-cutout path),
        /// else the material's <c>alphaCutoff</c> (default 0.5) for MASK. BLEND is out of scope for the mesh pass,
        /// so it is treated as MASK (its cutoff, default 0.5) rather than left as translucent - a foliage card
        /// still reads as its silhouette. A MASK material with cutoff 0 collapses to OPAQUE (nothing clipped),
        /// which is why 0 doubles as the "no clip" sentinel downstream.</summary>
        static float ReadAlphaCutoff(GltfMaterial mat) =>
            mat.Alpha == SharpGLTF.Schema2.AlphaMode.OPAQUE ? 0f : mat.AlphaCutoff;

        /// <summary>The material's emissiveFactor times its KHR_materials_emissive_strength. SharpGLTF maps the
        /// extension onto the Emissive channel as the "EmissiveStrength" parameter and answers 1 when it is absent,
        /// so an asset without it keeps its factor. Every material carries an Emissive channel, black by default.</summary>
        static Vector3 ReadEmissiveFactor(GltfMaterial mat)
        {
            MaterialChannel? channel = mat.FindChannel("Emissive");
            if (channel is not { } emissive) return Vector3.Zero;
            Vector4 color = emissive.Color;
            return new Vector3(color.X, color.Y, color.Z) * emissive.GetFactor("EmissiveStrength");
        }

        /// <summary>The image behind a material channel's texture, or null if the channel has no texture, no primary
        /// image, or an empty or unresolved one (e.g. an external file that did not resolve relative to the glb).</summary>
        static MemoryImage? ChannelImage(GltfMaterial mat, string key)
        {
            var content = mat.FindChannel(key)?.Texture?.PrimaryImage?.Content;
            return content is { IsValid: true } image ? image : (MemoryImage?)null;
        }

        /// <summary>Decode one material channel's texture through the shared image cache: an identity another load
        /// already decoded comes back without a second decode. Null when the channel has no usable image OR the
        /// bytes fail to decode (a corrupt/unsupported image is treated as absent, so the mesh just renders without
        /// that map).</summary>
        static DecodedImage? DecodeChannel(GltfMaterial mat, string key, string? assetDirectory)
        {
            if (ChannelImage(mat, key) is not { } image) return null;
            MaterialImageSource? source = MaterialImageCache.Shared.Resolve(ImageIdentity(image, assetDirectory), image.Content.Span);
            return source is null ? null : new DecodedImage(source);
        }

        /// <summary>The residency identity of a loaded image (see <see cref="DecodedImage.Identity"/>). SharpGLTF
        /// reports an external image's source path relative to the glb's folder, so it is resolved there. An image
        /// with no source path is embedded and named by the hash of its encoded bytes.</summary>
        internal static string ImageIdentity(MemoryImage image, string? assetDirectory)
        {
            string? source = image.SourcePath;
            if (string.IsNullOrEmpty(source))
                return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(image.Content.Span));
            string full = Path.IsPathRooted(source) || assetDirectory is null
                ? Path.GetFullPath(source)
                : Path.GetFullPath(Path.Combine(assetDirectory, source));
            return "file:" + full;
        }

        /// <summary>The folder a glb's external images resolve against.</summary>
        static string? AssetDirectory(string path) => Path.GetDirectoryName(Path.GetFullPath(path));
    }
}
