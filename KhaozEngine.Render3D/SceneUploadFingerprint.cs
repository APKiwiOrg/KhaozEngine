using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Render3D;

/// <summary>Hash detached input once, before measurement or upload admission. No payload copies at measurement.</summary>
internal static class SceneUploadFingerprint
{
    internal static string Compute(SceneUploadData data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Value<T>(T value) where T : unmanaged => hash.AppendData(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)));
        void Bytes(ReadOnlySpan<byte> bytes) { Value(bytes.Length); hash.AppendData(bytes); }
        void Image(DecodedImage? image)
        {
            Value(image.HasValue);
            if (image is not { } i) return;
            Value(i.Width); Value(i.Height); Bytes(i.Rgba);
        }
        Value(data.BorrowMesh is not null);
        if (data.BorrowMesh is { } mesh)
        { Bytes(MemoryMarshal.AsBytes(mesh.Vertices.AsSpan())); Bytes(MemoryMarshal.AsBytes(mesh.Indices32.AsSpan())); }
        Value(data.BorrowMaps.HasValue);
        if (data.BorrowMaps is { } maps)
        { Image(maps.Albedo); Image(maps.Normal); Image(maps.Roughness); Value(maps.AlphaCutoff); }
        Bytes(data.BorrowClipData.Span);
        Value(data.Foliage is not null);
        if (data.Foliage is { } f)
        {
            Value(f.Anchor); Value((int)f.TransformSpace);
            Bytes(MemoryMarshal.AsBytes(f.BorrowTransforms));
            Bytes(MemoryMarshal.AsBytes(f.BorrowRanks));
            Bytes(MemoryMarshal.AsBytes(f.BorrowModelIndices));
            Value(f.Models.Count);
            foreach (var model in f.Models)
            {
                Bytes(Encoding.UTF8.GetBytes(model.ModelId)); Value(model.Parts.Count); Value(model.Motion.ToVector4()); Value(model.CastShadows);
                foreach (var part in model.Parts) Value(part);
            }
        }
        Value(data.GroundMaterial is not null);
        if (data.GroundMaterial is { } g)
        {
            Value(g.Width); Value(g.Height); Value(g.BaseSpecularStrength); Value(g.Sampler.HasValue);
            if (g.Sampler is { } sampler) { Value((int)sampler.Filter); Value(sampler.MaximumAnisotropy); Value(sampler.MipLodBias); }
            Value(g.BorrowLayers.Count);
            foreach (var layer in g.BorrowLayers)
            {
                Bytes(layer.AlbedoRgba); Value(layer.Tint); Value(layer.TilesPerMetre); Value(layer.Steep is not null);
                if (layer.Steep is { } steep) { Value(steep.CompanionSlot); Value(steep.FromDegrees); Value(steep.ToDegrees); }
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
