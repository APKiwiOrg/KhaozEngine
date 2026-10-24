using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.Gpu;

/// <summary>GPU-free village inputs shared by the hosted capture and its deterministic source tests.</summary>
internal sealed class InteriorCutCostScene
{
    internal enum Layout { DenseVillage, TypicalVillage }
    readonly InteriorHeightField _empty, _village;
    internal IReadOnlyList<PropPlacement> Trees { get; }
    internal IReadOnlyList<InteriorVolume> Interiors { get; }

    InteriorCutCostScene(PropPlacement[] trees, InteriorVolume[] interiors, InteriorCutCostScene? sharedFields)
    {
        Trees = trees;
        Interiors = interiors;
        // Both layouts have identical buildings. The hosted capture retains only these two packed fields.
        if (sharedFields is not null)
        {
            _empty = sharedFields._empty;
            _village = sharedFields._village;
            return;
        }
        var window = InteriorWindow.Centred(Vector2.Zero, .25f, InteriorWindow.CellsFor(512f, .25f));
        var encoding = InteriorEncoding.For(0f, 9f);
        _empty = InteriorHeightFieldBuilder.Build([], window, encoding);
        _village = InteriorHeightFieldBuilder.Build(interiors, window, encoding);
    }

    internal static InteriorCutCostScene Create(Layout layout = Layout.DenseVillage, InteriorCutCostScene? sharedFields = null)
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
                Vector2 centre = BuildingCentre(z * 7 + x, 7, 7, 60f);
                float cx = centre.X, cz = centre.Y;
                interiors[z * 7 + x] = new InteriorVolume
                {
                    Outline = [new(cx - 12f, cz - 12f), new(cx + 12f, cz - 12f),
                        new(cx + 12f, cz + 12f), new(cx - 12f, cz + 12f)],
                    FloorY = 0f,
                    CeilingY = 9f,
                };
            }
        if (layout == Layout.TypicalVillage)
            for (int i = 0; i < trees.Length; i++)
            {
                PropPlacement tree = trees[i];
                // Exactly one in fifty trees reaches an interior. The remainder keep their deterministic
                // scatter except where their largest drawn cull sphere could reach a building. Those trees
                // move to the centre of the 36 m lane, clear of the 8-cell summary's conservative fringe.
                float x = tree.X, z = tree.Z;
                if (i % 50 == 0)
                {
                    Vector2 centre = (interiors[(i / 50) % interiors.Length].Outline[0]
                        + interiors[(i / 50) % interiors.Length].Outline[2]) * .5f;
                    x = centre.X;
                    z = centre.Y;
                }
                else
                {
                    float cx = Math.Clamp(MathF.Round(x / 60f), -3f, 3f) * 60f;
                    float cz = Math.Clamp(MathF.Round(z / 60f), -3f, 3f) * 60f;
                    if (MathF.Abs(x - cx) < 29f && MathF.Abs(z - cz) < 29f)
                        x = cx + (x < cx ? -30f : 30f);
                }
                trees[i] = new PropPlacement(tree.Id, x, tree.Y, z, tree.Scale, tree.Yaw, tree.Variant);
            }
        return new InteriorCutCostScene(trees, interiors, sharedFields);
    }

    internal static Vector2 BuildingCentre(int index, int columns, int rows, float spacing) =>
        new((index % columns - (columns - 1) * .5f) * spacing,
            (index / columns - (rows - 1) * .5f) * spacing);

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
