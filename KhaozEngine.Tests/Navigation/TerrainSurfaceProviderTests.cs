using System;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Navigation;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class TerrainSurfaceProviderTests
{
    // Truly flat (height 0 everywhere): unlike NavGridBakerTests' FlatField, this also zeroes the
    // gentle low-frequency roll (its default GentleAmplitude is nonzero even with HillAmplitude = 0),
    // since these tests assert exact ground heights, not just walkability.
    static TerrainField FlatField()
        => new(new TerrainConfig
        {
            Seed = 1,
            GentleAmplitude = 0f,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f },
            },
        });

    static TerrainField BumpyField()
        => new(new TerrainConfig
        {
            Seed = 7, BiomeBlend = 24f,
            GentleFrequency = 0.03f, GentleAmplitude = 2f,
            DetailFrequency = 0.15f, DetailOctaves = 4,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Mountains, BaseHeight = 0f, HillAmplitude = 6f },
            },
        });

    static PropSurface FlatTop(float y)
    {
        float n = float.NaN;
        return new PropSurface(3, 3, 1f, -1f, -1f, new[] { n, y, n, y, y, y, n, y, n });
    }

    // Scans a region for a cell whose terrain slope fails maxSlopeRadians, mirroring the steep-cell
    // selection in NavGridBakerTests.BakeOverworld_SlopeGate. Deterministic fixed scan order.
    static (float X, float Z) FindSteepCell(TerrainCollision terrain, float maxSlopeRadians)
    {
        for (float z = 0f; z < 40f; z += 0.5f)
        {
            for (float x = 0f; x < 40f; x += 0.5f)
            {
                if (!terrain.IsWalkable(x, z, maxSlopeRadians)) return (x, z);
            }
        }

        throw new InvalidOperationException("Expected at least one steep cell in the scanned region.");
    }

    [Fact]
    public void Ctor_NullTerrain_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TerrainSurfaceProvider(null!, MathF.PI / 2f));
    }

    [Fact]
    public void FlatTerrain_NoProps_ReturnsGroundHeight()
    {
        var terrain = new TerrainCollision(FlatField());
        var provider = new TerrainSurfaceProvider(terrain, MathF.PI / 2f);

        bool ok = provider.TrySample(1f, 1f, out float h, out float hr);

        Assert.True(ok);
        Assert.Equal(0f, h);
        Assert.Equal(float.PositiveInfinity, hr);
    }

    [Fact]
    public void PropTop_RaisesHeight()
    {
        var terrain = new TerrainCollision(FlatField());
        var surfaces = new WorldSurfaces(new[] { new WorldSurface(FlatTop(0.4f), new Vector2(1f, 1f), 1f, 0f, 0f) });
        var provider = new TerrainSurfaceProvider(terrain, MathF.PI / 2f, surfaces);

        bool ok = provider.TrySample(1f, 1f, out float h, out float hr);

        Assert.True(ok);
        Assert.Equal(0.4f, h, 3);
        Assert.Equal(float.PositiveInfinity, hr);
    }

    [Fact]
    public void SteepTerrain_NoProp_ReturnsFalse()
    {
        var terrain = new TerrainCollision(BumpyField());
        const float maxSlope = 0.02f;
        (float x, float z) = FindSteepCell(terrain, maxSlope);
        var provider = new TerrainSurfaceProvider(terrain, maxSlope);

        bool ok = provider.TrySample(x, z, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void PropTop_RescuesSteepTerrain()
    {
        var terrain = new TerrainCollision(BumpyField());
        const float maxSlope = 0.02f;
        (float x, float z) = FindSteepCell(terrain, maxSlope);
        var surfaces = new WorldSurfaces(new[] { new WorldSurface(FlatTop(0.4f), new Vector2(x, z), 1f, 0f, 0f) });
        var provider = new TerrainSurfaceProvider(terrain, maxSlope, surfaces);

        bool ok = provider.TrySample(x, z, out float h, out float hr);

        Assert.True(ok);
        Assert.Equal(0.4f, h, 3);
        Assert.Equal(float.PositiveInfinity, hr);
    }

    [Fact]
    public void SolidCollider_NoSurface_ReturnsFalse()
    {
        var terrain = new TerrainCollision(FlatField());
        var colliders = new WorldColliders(new[] { WorldCollider.Cylinder(new Vector2(5f, 5f), 1.5f) });
        float probeRadius = 0.5f * 0.70710678f;
        var provider = new TerrainSurfaceProvider(terrain, MathF.PI / 2f, colliders: colliders, colliderProbeRadius: probeRadius);

        bool blocked = provider.TrySample(5f, 5f, out _, out _);
        bool far = provider.TrySample(9f, 9f, out _, out _);

        Assert.False(blocked);
        Assert.True(far);
    }

    [Fact]
    public void Deterministic()
    {
        var terrain = new TerrainCollision(BumpyField());
        var colliders = new WorldColliders(new[] { WorldCollider.Cylinder(new Vector2(5f, 5f), 1.5f) });
        var surfaces = new WorldSurfaces(new[] { new WorldSurface(FlatTop(0.4f), new Vector2(2f, 2f), 1f, 0f, 0f) });
        var provider = new TerrainSurfaceProvider(terrain, 0.5f, surfaces, colliders, 0.5f);

        bool ok1 = provider.TrySample(3f, 3f, out float h1, out float hr1);
        bool ok2 = provider.TrySample(3f, 3f, out float h2, out float hr2);

        Assert.Equal(ok1, ok2);
        Assert.Equal(h1, h2);
        Assert.Equal(hr1, hr2);
    }
}
