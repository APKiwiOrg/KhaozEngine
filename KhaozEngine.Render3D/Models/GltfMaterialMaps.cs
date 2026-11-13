using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>The optional textures auto-read from a glTF material by <see cref="GltfLoader.LoadWithMaterial(string)"/> /
    /// <see cref="GltfLoader.LoadSkinnedWithMaterial"/>, each decoded to raw RGBA8 (the loader has no GPU device, so
    /// it never creates a <see cref="Scene3D.TextureHandle"/>; the upload stays in <see cref="Scene3D"/>). Any map a
    /// material doesn't reference - or whose image is missing/external-unresolved/undecodable - is left
    /// <c>null</c> (absent, never a throw), so a material with no textures yields an all-null
    /// <see cref="GltfMaterialMaps"/>. <see cref="Roughness"/> is glTF's packed ORM-style texture passed
    /// through unchanged (the model shader samples <c>.g</c> for roughness); <see cref="Normal"/> is the
    /// tangent-space RGB normal map unchanged. <see cref="AlphaCutoff"/> carries the material's alpha-cutout
    /// threshold (0 = OPAQUE, no clip). <see cref="Emissive"/> and <see cref="EmissiveFactor"/> carry the material's
    /// glow. Feed the bundle to <see cref="Scene3D.LoadSurfaceMaps"/> (or the
    /// <see cref="Scene3D.LoadMesh(GltfMesh,GltfMaterialMaps)"/> overload) to upload only the present maps.
    /// Loader images are shared by identity, see <see cref="DecodedImage"/>.</summary>
    public readonly struct GltfMaterialMaps
    {
        /// <summary>Decoded baseColor (albedo) texture, or <c>null</c> if the material has none.</summary>
        public DecodedImage? Albedo { get; }
        /// <summary>Decoded tangent-space normal map (RGB unchanged), or <c>null</c> if the material has none.</summary>
        public DecodedImage? Normal { get; }
        /// <summary>Decoded glTF metallic-roughness texture, passed through unchanged (roughness in <c>.g</c>), or
        /// <c>null</c> if the material has none.</summary>
        public DecodedImage? Roughness { get; }
        /// <summary>The alpha-cutout threshold read from the glTF material's alphaMode: <c>0</c> for OPAQUE (the
        /// default when absent, so no fragment is clipped and the render is unchanged), else the material's
        /// <c>alphaCutoff</c> (default 0.5 per spec) for MASK. glTF BLEND is out of scope and treated as MASK
        /// (its cutoff, default 0.5). The model fragment discards a texel whose sampled baseColor alpha is below
        /// this value, so a leaf-card / foliage texture reads as its silhouette instead of a solid quad.</summary>
        public float AlphaCutoff { get; }
        /// <summary>Decoded glTF emissiveTexture (its RGB is used, as stored, the same gamma-space convention as
        /// albedo), or <c>null</c>. It is read only when <see cref="EmissiveFactor"/> is non-zero, because a zero
        /// factor makes it invisible under the glTF spec.</summary>
        public DecodedImage? Emissive { get; }
        /// <summary>The glTF emissiveFactor multiplied by KHR_materials_emissive_strength (1 when the extension is
        /// absent): an HDR colour that scales <see cref="Emissive"/>, or is the flat glow itself when there is no
        /// map. Zero (the glTF default) means the material does not glow.</summary>
        public Vector3 EmissiveFactor { get; }

        public GltfMaterialMaps(DecodedImage? albedo, DecodedImage? normal, DecodedImage? roughness, float alphaCutoff = 0f)
            : this(albedo, normal, roughness, alphaCutoff, null, Vector3.Zero)
        {
        }

        /// <summary>A bundle with the material's emissive map and factor as well. See <see cref="Emissive"/> and
        /// <see cref="EmissiveFactor"/>.</summary>
        public GltfMaterialMaps(DecodedImage? albedo, DecodedImage? normal, DecodedImage? roughness, float alphaCutoff,
            DecodedImage? emissive, Vector3 emissiveFactor)
        {
            Albedo = albedo; Normal = normal; Roughness = roughness; AlphaCutoff = alphaCutoff;
            Emissive = emissive; EmissiveFactor = emissiveFactor;
        }

        /// <summary>True when the material referenced (and the loader decoded) no textures at all. (The alpha-cutout
        /// threshold and the emissive factor are metadata, not textures, so they do not affect emptiness.)</summary>
        public bool IsEmpty => Albedo is null && Normal is null && Roughness is null && Emissive is null;

        /// <summary>The RGBA8 bytes of every present map, from their dimensions, so accounting never forces a shared
        /// image to decode again.</summary>
        internal long DecodedBytes => Bytes(Albedo) + Bytes(Normal) + Bytes(Roughness) + Bytes(Emissive);

        static long Bytes(DecodedImage? image) => image is { } value ? 4L * value.Width * value.Height : 0L;

        /// <summary>A copy for an input an upload retains, with every map, the cutoff and the emissive, immune to later
        /// edits by its caller. An image that owns a pixel array is copied, and a copy keeps the image's identity,
        /// which its unchanged content still names, so a scene still shares its texture with every other part, glb
        /// and upload path naming that image (issue #1776). A caller-built image has no identity and stays
        /// independent. A shared loader image is kept as it is: its one decode is read-only for every user, and
        /// copying it would defeat the sharing.</summary>
        internal GltfMaterialMaps Snapshot() =>
            new(Snapshot(Albedo), Snapshot(Normal), Snapshot(Roughness), AlphaCutoff, Snapshot(Emissive), EmissiveFactor);

        static DecodedImage? Snapshot(DecodedImage? image) =>
            image is not { } value ? null
            : value.Source is not null ? value
            : value.Identity is { } identity ? new DecodedImage((byte[])value.Rgba.Clone(), value.Width, value.Height, identity)
            : new DecodedImage((byte[])value.Rgba.Clone(), value.Width, value.Height);
    }
}
