using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Render3D;

/// <summary>Hash borrowed or detached input without payload copies, including virtual white ground layers.</summary>
internal static class SceneUploadFingerprint
{
    internal static string Compute(SceneUploadDescription data)
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
        Value(data.Mesh is not null);
        if (data.Mesh is { } mesh)
        { Bytes(MemoryMarshal.AsBytes(mesh.Vertices.AsSpan())); Bytes(MemoryMarshal.AsBytes(mesh.Indices32.AsSpan())); }
        Value(data.Maps.HasValue);
        if (data.Maps is { } maps)
        { Image(maps.Albedo); Image(maps.Normal); Image(maps.Roughness); Value(maps.AlphaCutoff); }
        Bytes(data.ClipData.Span);
        Value(data.Foliage is not null);
        if (data.Foliage is { } f)
        {
            Value(f.Anchor); Value((int)f.TransformSpace);
            Bytes(MemoryMarshal.AsBytes(f.Transforms.Span));
            Bytes(MemoryMarshal.AsBytes(f.Ranks.Span));
            Bytes(MemoryMarshal.AsBytes(f.ModelIndices.Span));
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
            Value(g.Layers.Count);
            Span<byte> white = stackalloc byte[1024]; white.Fill(255);
            foreach (var layer in g.Layers)
            {
                if (layer.AlbedoRgba is { } pixels) Bytes(pixels);
                else
                {
                    int remaining = checked(g.Width * g.Height * 4); Value(remaining);
                    while (remaining > 0) { int count = Math.Min(remaining, white.Length); hash.AppendData(white[..count]); remaining -= count; }
                }
                Value(layer.Tint); Value(layer.TilesPerMetre); Value(layer.Steep is not null);
                if (layer.Steep is { } steep) { Value(steep.CompanionSlot); Value(steep.FromDegrees); Value(steep.ToDegrees); }
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
