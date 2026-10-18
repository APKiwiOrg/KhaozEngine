using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.Gpu;

/// <summary>GPU-free village inputs shared by the hosted capture and its deterministic source tests.</summary>
internal sealed class InteriorCutCostScene
{
    readonly InteriorHeightField _empty, _village;
    internal IReadOnlyList<PropPlacement> Trees { get; }
    internal IReadOnlyList<InteriorVolume> Interiors { get; }

    InteriorCutCostScene(PropPlacement[] trees, InteriorVolume[] interiors)
    {
        Trees = trees;
        Interiors = interiors;
        var window = InteriorWindow.Centred(Vector2.Zero, .25f, InteriorWindow.CellsFor(512f, .25f));
        var encoding = InteriorEncoding.For(0f, 9f);
        _empty = InteriorHeightFieldBuilder.Build([], window, encoding);
        _village = InteriorHeightFieldBuilder.Build(interiors, window, encoding);
    }

    internal static InteriorCutCostScene Create()
    {
        var trees = new PropPlacement[3600];
        uint seed = 0x1C07_5EED;
        float Next() { seed = seed * 1664525u + 1013904223u; return (seed >> 8) / (float)(1 << 24); }
        for (int z = 0; z < 60; z++)
            for (int x = 0; x < 60; x++)
                trees[z * 60 + x] = new PropPlacement("tree", -250f + (x + Next()) * (500f / 60f), 0f,
                    -250f + (z + Next()) * (500f / 60f), .8f + Next() * .4f, Next() * 6.283185f, 0);
        var interiors = new InteriorVolume[49];
        for (int z = 0; z < 7; z++)
            for (int x = 0; x < 7; x++)
            {
                float cx = (x - 3) * 60f, cz = (z - 3) * 60f;
                interiors[z * 7 + x] = new InteriorVolume
                {
                    Outline = [new(cx - 12f, cz - 12f), new(cx + 12f, cz - 12f),
                        new(cx + 12f, cz + 12f), new(cx - 12f, cz + 12f)],
                    FloorY = 0f,
                    CeilingY = 9f,
                };
            }
        return new InteriorCutCostScene(trees, interiors);
    }

    internal float SunAngleFor(int frame) => frame * (.2f / 60f);
    internal bool CutsFor(InteriorCutCostPerfGpuTests.Mode mode) =>
        mode is InteriorCutCostPerfGpuTests.Mode.EmptyField or InteriorCutCostPerfGpuTests.Mode.VillageInteriors;
    internal InteriorHeightField? FieldFor(InteriorCutCostPerfGpuTests.Mode mode) => mode switch
    {
        InteriorCutCostPerfGpuTests.Mode.EmptyField => _empty,
        InteriorCutCostPerfGpuTests.Mode.VillageInteriors => _village,
        _ => null,
    };
}
