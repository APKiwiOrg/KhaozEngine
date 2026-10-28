using System;
using System.Collections.Generic;
using KhaozEngine.Render2D;
using SharpGLTF.Schema2;

namespace KhaozEngine.Render3D;

public static partial class GltfLoader
{
    /// <summary>Opt-in complete decode with one shared reservation covering model, weld, images and copies.</summary>
    public static IReadOnlyList<GltfMeshPart> LoadPartsWithMaterialsStrict(ReadOnlyMemory<byte> glb, string identity,
        GltfDecodeBudget budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var admission = GltfDecodeAdmission.Measure(glb, budget);
        var capacity = admission.Capacity;
        if (!admission.Allowed || !budget.TryReserve(admission.OwnedBytes, admission.PeakBytes - admission.OwnedBytes, out capacity))
            throw new GltfDecodeRefusal(GltfDecodeStatus.CapacityExceeded, capacity?.Message ?? "glTF decode allowance exceeded for " + identity) { Capacity = capacity };
        bool success = false;
        try
        {
            var result = TranslateModelErrors(identity,
                () => BuildParts(LoadModel(glb, identity), identity, material => StrictMaps(material, budget), true));
            success = true;
            return result;
        }
        finally
        {
            budget.ReleaseTemporary(admission.PeakBytes - admission.OwnedBytes);
            if (!success) budget.ReleaseOwned(admission.OwnedBytes);
        }
    }

    static GltfMaterialMaps StrictMaps(SharpGLTF.Schema2.Material? material, GltfDecodeBudget budget)
    {
        // Admission refuses textured channels and material extensions that these three maps cannot represent.
        if (material is null) return default;
        return new(Decode("BaseColor"), Decode("Normal"), Decode("MetallicRoughness"), ReadAlphaCutoff(material));
        DecodedImage? Decode(string key)
        {
            if (material.FindChannel(key)?.Texture is null) return null;
            var bytes = ChannelTexture(material, key);
            if (bytes is null) throw new GltfDecodeRefusal(GltfDecodeStatus.Invalid, "Referenced material image is unavailable.");
            try
            {
                ImageRgba image = ImageRgba.DecodeBounded(bytes.Value.Span, budget.Limits.MaxPeakBytes, budget.Limits.MaxImagePixels, budget.Limits);
                return new(image.Pixels, image.Width, image.Height);
            }
            catch (ImageDecodeRefusal ex) { throw new GltfDecodeRefusal((GltfDecodeStatus)ex.Status, ex.Message); }
        }
    }
}
