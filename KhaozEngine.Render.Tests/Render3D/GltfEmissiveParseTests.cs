using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.TileWorld;
using SharpGLTF.Materials;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>glTF emissive read into <see cref="GltfMaterialMaps"/> (issue #1776): emissiveTexture decoded,
/// emissiveFactor multiplied by KHR_materials_emissive_strength (1 when the extension is absent), the same through
/// every loader entry point that reads material maps, one case each. A zero factor makes the texture invisible per the
/// glTF spec, so it is not decoded, and a material with no emissive reads as no map and a zero factor.</summary>
public sealed class GltfEmissiveParseTests
{
    public enum Emission { MapFactorStrength, FactorOnly, MapZeroFactor, None }

    public enum Entry { RigidParts, RigidSingle, SkinnedParts, SkinnedSingle }

    public static TheoryData<Emission, Entry> Cases()
    {
        var cases = new TheoryData<Emission, Entry>();
        foreach (Emission emission in Enum.GetValues<Emission>())
            foreach (Entry entry in Enum.GetValues<Entry>())
                cases.Add(emission, entry);
        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emissive_reads_factor_times_strength_and_the_map_it_scales(Emission emission, Entry entry)
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
        string path = MaterialGlbFixture.Write(dir.Path, "glow.glb", material,
            rigged: entry is Entry.SkinnedParts or Entry.SkinnedSingle);

        GltfMaterialMaps maps = Load(path, entry);

        Vector3 expectedFactor = emission switch
        {
            Emission.MapFactorStrength => factor * strength,
            Emission.FactorOnly => factor,
            _ => Vector3.Zero,
        };
        Assert.Equal(expectedFactor, maps.EmissiveFactor);
        bool expectMap = emission == Emission.MapFactorStrength;
        Assert.Equal(expectMap, maps.Emissive.HasValue);
        if (expectMap)
            Assert.Equal(new byte[] { 40, 200, 90, 255 }, maps.Emissive!.Value.Rgba[..4]);
    }

    static GltfMaterialMaps Load(string path, Entry entry) => entry switch
    {
        Entry.RigidParts => GltfLoader.LoadPartsWithMaterials(path)[0].Maps,
        Entry.RigidSingle => GltfLoader.LoadWithMaterial(path).Maps,
        Entry.SkinnedParts => GltfLoader.LoadSkinnedPartsWithMaterials(path)[0].Maps,
        Entry.SkinnedSingle => GltfLoader.LoadSkinnedWithMaterial(path).Maps,
        _ => throw new ArgumentOutOfRangeException(nameof(entry)),
    };
}
