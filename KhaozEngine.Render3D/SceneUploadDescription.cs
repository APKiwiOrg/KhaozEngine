using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>Borrowed, already-admitted inputs. Keep their owner alive and their bytes unchanged until preparation.
/// Measurement reads these descriptors without detaching payloads. Preparation claims before detaching them.</summary>
public sealed record SceneUploadDescription(GltfMesh? Mesh, GltfMaterialMaps? Maps, SceneFoliageDescription? Foliage,
    ReadOnlyMemory<byte> ClipData, SceneGroundDescription? GroundMaterial = null)
{
    internal SceneUploadData Detach()
    {
        SceneFoliageSource? foliage = Foliage is not { } f ? null : new(f.Anchor, f.Transforms, f.Ranks, f.ModelIndices, f.Models, f.TransformSpace);
        SceneGroundMaterialData? ground = null;
        if (GroundMaterial is { } g)
        {
            var layers = g.Layers.Select(l => new TileGroundLayerImage
            {
                AlbedoRgba = l.AlbedoRgba ?? White(g.Width, g.Height),
                Tint = l.Tint,
                TilesPerMetre = l.TilesPerMetre,
                Steep = l.Steep
            }).ToArray();
            ground = new(g.Width, g.Height, layers, g.BaseSpecularStrength, g.Sampler);
        }
        return new(Mesh, Maps, foliage, ClipData, ground);
    }
    static byte[] White(int width, int height)
    {
        var bytes = new byte[checked(width * height * 4)]; Array.Fill(bytes, (byte)255); return bytes;
    }
}

public sealed record SceneFoliageDescription(RenderAnchor Anchor, ReadOnlyMemory<Matrix4x4> Transforms,
    ReadOnlyMemory<float> Ranks, ReadOnlyMemory<int> ModelIndices, IReadOnlyList<SceneFoliageModelBinding> Models,
    SceneFoliageTransformSpace TransformSpace = SceneFoliageTransformSpace.World);

/// <summary>A null albedo describes a white layer at the set's dimensions, without allocating its pixels.</summary>
public sealed record SceneGroundLayerDescription(byte[]? AlbedoRgba, Color Tint, float TilesPerMetre, TileGroundSteepLayer? Steep = null);
public sealed record SceneGroundDescription(int Width, int Height, IReadOnlyList<SceneGroundLayerDescription> Layers,
    float BaseSpecularStrength = .15f, TerrainSamplerConfig? Sampler = null);
