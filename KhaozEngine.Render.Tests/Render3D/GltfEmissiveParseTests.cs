using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.TileWorld;
using SharpGLTF.Materials;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>glTF emissive read into <see cref="GltfMaterialMaps"/> (issue #1776): emissiveTexture decoded,
/// emissiveFactor multiplied by KHR_materials_emissive_strength (1 when the extension is absent), the same on the
/// multi-material parts path and the single-material path. A zero factor makes the texture invisible per the glTF
/// spec, so it is not decoded, and a material with no emissive reads as no map and a zero factor.</summary>
public sealed class GltfEmissiveParseTests
{
    public enum Emission { MapFactorStrength, FactorOnly, MapZeroFactor, None }

    [Theory]
    [InlineData(Emission.MapFactorStrength)]
    [InlineData(Emission.FactorOnly)]
    [InlineData(Emission.MapZeroFactor)]
    [InlineData(Emission.None)]
    public void Emissive_reads_factor_times_strength_and_the_map_it_scales(Emission emission)
    {
        using var dir = new TempDir();
        byte[] png = MaterialGlbFixture.Png(40, 200, 90);
        var factor = new Vector3(0.5f, 0.25f, 1f);
        const float strength = 4f;
        var material = new MaterialBuilder("glow").WithBaseColor(new Vector4(0.3f, 0.3f, 0.3f, 1f));
        material = emission switch
        {
            Emission.MapFactorStrength => material.WithEmissive(new SharpGLTF.Memory.MemoryImage(png), factor, strength),
            Emission.FactorOnly => material.WithEmissive(factor, 1f),
            Emission.MapZeroFactor => material.WithEmissive(new SharpGLTF.Memory.MemoryImage(png), Vector3.Zero, 1f),
            _ => material,
        };
        string path = MaterialGlbFixture.Write(dir.Path, "glow.glb", material);

        GltfMaterialMaps parts = GltfLoader.LoadPartsWithMaterials(path)[0].Maps;
        GltfMaterialMaps single = GltfLoader.LoadWithMaterial(path).Maps;

        Vector3 expectedFactor = emission switch
        {
            Emission.MapFactorStrength => factor * strength,
            Emission.FactorOnly => factor,
            _ => Vector3.Zero,
        };
        bool expectMap = emission == Emission.MapFactorStrength;
        foreach (GltfMaterialMaps maps in new[] { parts, single })
        {
            Assert.Equal(expectedFactor, maps.EmissiveFactor);
            Assert.Equal(expectMap, maps.Emissive.HasValue);
            if (!expectMap) continue;
            DecodedImage map = maps.Emissive!.Value;
            Assert.Equal(new byte[] { 40, 200, 90, 255 }, map.Rgba[..4]);
        }
    }
}
