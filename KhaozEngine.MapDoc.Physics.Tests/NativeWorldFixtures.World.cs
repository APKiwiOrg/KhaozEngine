using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Built world fixtures. Every <c>BuildX()</c> builds fixture <c>X()</c> with <see cref="Options"/>.</summary>
internal static partial class NativeWorldFixtures
{
    /// <summary>The resolver-1 options the legacy fixture documents resolve with.</summary>
    internal static readonly MapResolveOptions LegacyResolveOptions =
        new("native-world-fixtures", 1, "native-world-fixture-options");

    /// <summary>The world build options every fixture builds with: resolver 2 and the default chunk policy.</summary>
    internal static MapWorldBuildOptions Options() =>
        new(ResolveOptions, "native-world-fixture-policy", new MapTerrainChunkPolicy());

    static partial void WorldAssets(Action<string, PhysicsShape, Vector3, Vector3> solid)
    {
        // Seventy 0.1 m posts in a 7 m row, one more leaf than the feature query captures.
        solid("parapet-70", Compound(Enumerable.Range(0, 70)
                .Select(i => ((PhysicsShape)Box(0.1f, 1f, 0.2f), new Vector3(-3.45f + 0.1f * i, 0.5f, 0f))).ToArray()),
            new Vector3(-3.5f, 0f, -0.1f), new Vector3(3.5f, 1f, 0.1f));

        // Two 1 m boxes 100 m apart. Each leaf is small in its own frame although both sit far from the origin.
        solid("spread-boxes", Compound(
                (Box(1f, 1f, 1f), new Vector3(-50f, 0.5f, 0f)),
                (Box(1f, 1f, 1f), new Vector3(50f, 0.5f, 0f))),
            new Vector3(-50.5f, 0f, -0.5f), new Vector3(50.5f, 1f, 0.5f));

        // A 1 m tall crate, tall enough that its envelope is its collider with no raise.
        solid("tall-crate", Compound((Box(0.6f, 1f, 0.6f), new Vector3(0f, 0.5f, 0f))),
            new Vector3(-0.3f, 0f, -0.3f), new Vector3(0.3f, 1f, 0.3f));

        // The tall crate's box at the same local pose, plus a post 20 m behind it and 2 m to the side, so its envelope
        // reaches two 16 m cells further back than the tall crate's.
        solid("tall-crate-with-rear", Compound(
                (Box(0.6f, 1f, 0.6f), new Vector3(0f, 0.5f, 0f)),
                (Box(0.2f, 1f, 0.2f), new Vector3(2f, 0.5f, -20f))),
            new Vector3(-0.3f, 0f, -20.1f), new Vector3(2.1f, 1f, 0.3f));

        // A 2 m by 2 m upright mesh wall in the plane z = 0, spanning x -1 to 1 and y 0 to 2.
        solid("mesh-wall", new TriangleMeshShape(new[]
            {
                new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(-1f, 2f, 0f), new Vector3(1f, 2f, 0f),
            }, new[] { 0, 2, 1, 1, 2, 3 }),
            new Vector3(-1f, 0f, 0f), new Vector3(1f, 2f, 0f));

        // A 0.1 m radius, 2 m post leaning 45 degrees toward +x, its axis turned from local Y onto (1, 1, 0). Its base
        // centre is at the placement origin, so the rim of its base dips 0.07 m below it.
        solid("leaning-post", new CompoundShape(new[]
            {
                new CompoundChild(new CylinderShape(0.1f, 2f),
                    new Pose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 4f))),
            }),
            new Vector3(-0.08f, -0.08f, -0.1f), new Vector3(1.5f, 1.5f, 0.1f));

        // A 64 m square slab 1 m tall whose minimum corner is at the placement origin, so placed on a seam it fills
        // exactly one 64 m storage tile.
        solid("seam-box", Compound((Box(64f, 1f, 64f), new Vector3(32f, 0.5f, 32f))),
            Vector3.Zero, new Vector3(64f, 1f, 64f));
    }

    internal static MapBuiltWorld BuildStackedCave() => Build(StackedCave());

    /// <summary>The 100 m square building centred on the origin, so it reaches into the four 64 m tiles around
    /// it.</summary>
    internal static MapBuiltWorld BuildLargeBuilding() => Build(Placed("large-building"));

    /// <summary>The flat floor, whose patch slots end on the x 0 and z 0 seams, the crate straddling those seams at
    /// the origin and the seam box filling storage tile (1, -1) from its minimum corner at (64, -64). Storage bounds
    /// span tiles -2 to 1 on both axes.</summary>
    internal static MapBuiltWorld BuildSeamAligned()
    {
        NativeFixture f = Resolve(FloorSurfaces(),
            OnFloor("crate", "crate", 0f, 0f),
            OnFloor("seam-box", "seam-box", 64f, -64f));
        f.Document.Bounds = new() { MinX = -128, MinZ = -128, MaxX = 128, MaxZ = 128 };
        return Build(f);
    }

    /// <summary>Four 2 by 2 tile windows that together cover tiles -2 to 1 on both axes of
    /// <see cref="BuildSeamAligned"/>, overlapping nowhere.</summary>
    internal static MapTileRect[] TilingWindows() => new MapTileRect[]
    {
        new(new(-2, -2), new(-1, -1)), new(new(0, -2), new(1, -1)),
        new(new(-2, 0), new(-1, 1)), new(new(0, 0), new(1, 1)),
    };

    /// <summary>The tree, a 70 leaf parapet, the 150 m long wall, the crate and the spread boxes on the flat
    /// floor.</summary>
    internal static NativeFixture TreeAndWideCompound() => Resolve(FloorSurfaces(),
        OnFloor("tree", "tree", -4f, -4f),
        OnFloor("parapet-70", "parapet-70", 0f, 4f),
        OnFloor("long-wall", "long-wall", 0f, -6f),
        OnFloor("crate", "crate", 4f, 4f),
        OnFloor("spread-boxes", "spread-boxes", 0f, 0f));

    internal static MapBuiltWorld BuildTreeAndWideCompound() => Build(TreeAndWideCompound());

    /// <summary>The crate document as resolver 1: no surfaces and analytic support at y 0.</summary>
    internal static NativeFixture LegacyCrate()
    {
        NativeFixture f = Crate();
        f.Document.ResolverIdentity = new(1, 1);
        f.Document.SupportRecipe = MapSupportRecipe.LegacyXzCallbackV1;
        f.Document.Surfaces = new MapSurfaceSet();
        MapResolvedDocument resolved = MapResolver.Resolve(f.Document, f.Assets, (_, _) => 0f, LegacyResolveOptions);
        return new NativeFixture(f.Document, f.Assets, resolved, MapScopedSurfaces.CompleteView(new MapSurfaceSet()));
    }

    /// <summary>The legacy crate with resolver-1 options and no legacy support height.</summary>
    internal static MapBuiltWorld BuildLegacyWithoutHeight()
    {
        NativeFixture f = LegacyCrate();
        return MapWorldBuilder.Build(f.Document, f.Assets, Options() with { Resolve = LegacyResolveOptions });
    }

    /// <summary>The legacy crate with one sculpt tile at tile (0, 0), 0.5 m cells, whose cell (0, 0) delta is
    /// <paramref name="delta"/>, built with resolver-1 options and <paramref name="height"/>.</summary>
    internal static MapBuiltWorld BuildLegacyCrateWithSculpt(float delta, Func<float, float, float> height)
    {
        NativeFixture f = LegacyCrate();
        f.Document.TerrainOverrides = new MapTerrainOverrides(0.5f);
        f.Document.TerrainOverrides.SetDelta(0, 0, delta);
        return MapWorldBuilder.Build(f.Document, f.Assets,
            Options() with { Resolve = LegacyResolveOptions, LegacySupportHeight = height });
    }

    /// <summary>The analytic resolver-1 slope <see cref="BuildLegacySlope"/> stands on, rising along both +x and
    /// +z.</summary>
    internal static float LegacySlopeHeight(float x, float z) => 0.25f + 0.1763f * x + 0.0875f * z;

    /// <summary>The legacy crate built with resolver-1 options over <see cref="LegacySlopeHeight"/>. The crate keeps
    /// its authored y 0.</summary>
    internal static MapBuiltWorld BuildLegacySlope()
    {
        NativeFixture f = LegacyCrate();
        return MapWorldBuilder.Build(f.Document, f.Assets,
            Options() with { Resolve = LegacyResolveOptions, LegacySupportHeight = LegacySlopeHeight });
    }

    internal static MapBuiltWorld BuildTallCornerCell() => Build(TallCornerCell());

    /// <summary>The resolver-2 crate built with resolver-1 options.</summary>
    internal static MapBuiltWorld BuildWithResolverMismatch()
    {
        NativeFixture f = Crate();
        return MapWorldBuilder.Build(f.Document, f.Assets, Options() with { Resolve = LegacyResolveOptions });
    }

    /// <summary>The crate with its placement scale replaced.</summary>
    internal static MapBuiltWorld BuildWithPlacementScale(float scale)
    {
        NativeFixture f = Crate();
        f.Document.Placements[0].Scale = scale;
        return Build(f);
    }

    /// <summary>The crate moved to <paramref name="x"/>, with storage bounds widened to contain it.</summary>
    internal static MapBuiltWorld BuildWithPlacementAt(float x)
    {
        NativeFixture f = Crate();
        f.Document.Bounds = new() { MinX = -64, MinZ = -64, MaxX = x + 64f, MaxZ = 64 };
        f.Document.Placements[0].X = x;
        return Build(f);
    }

    /// <summary>The crate plus a second crate one 32 m tile east, saved tiled and loaded through a window over tile
    /// (0, 0) only.</summary>
    internal static MapBuiltWorld BuildPartialWindow()
    {
        NativeFixture f = Crate();
        f.Document.TileSize = 32f;
        f.Document.Placements.Add(OnFloor("far-crate", "crate", 40f, 0f));
        string directory = Path.Combine(Path.GetTempPath(), "native-world-partial-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveTiled(f.Document, directory);
            MapDocument window = MapDocumentFile.LoadTiled(directory, new MapTileRect(new(0, 0), new(0, 0)));
            return MapWorldBuilder.Build(window, f.Assets, Options());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The 6 m tree standing at the origin on the flat floor.</summary>
    internal static MapBuiltWorld BuildTree() => Build(Placed("tree"));

    internal static MapBuiltWorld BuildCrate() => Build(Crate());

    /// <summary>Two tall crates at the origin whose front boxes are bit-identical, so a ray along +z meets both at one
    /// distance. b-crate also carries a post 20 m behind, so a ray starting there reaches b-crate's envelope in an
    /// earlier grid cell than a-crate's.</summary>
    internal static MapBuiltWorld BuildTwinCrates() => Build(Resolve(FloorSurfaces(),
        OnFloor("b-crate", "tall-crate-with-rear", 0f, 0f),
        OnFloor("a-crate", "tall-crate", 0f, 0f)));

    /// <summary>The mesh wall at the origin on the flat floor.</summary>
    internal static MapBuiltWorld BuildMeshWall() => Build(Placed("mesh-wall"));

    /// <summary>The leaning post at the origin on the flat floor.</summary>
    internal static MapBuiltWorld BuildLeaningPost() => Build(Placed("leaning-post"));

    /// <summary>Crates <c>crate-iii-jjj</c> at (i x spacing, 0, j x spacing), with storage bounds widened to contain
    /// them.</summary>
    internal static MapBuiltWorld BuildCrateField(int columns, int rows, float spacingMetres)
    {
        NativeFixture f = Crate();
        f.Document.Bounds = new()
        {
            MinX = -64,
            MinZ = -64,
            MaxX = (columns - 1) * spacingMetres + 64f,
            MaxZ = (rows - 1) * spacingMetres + 64f,
        };
        f.Document.Placements.Clear();
        for (int i = 0; i < columns; i++)
            for (int j = 0; j < rows; j++)
                f.Document.Placements.Add(OnFloor(FormattableString.Invariant($"crate-{i:000}-{j:000}"), "crate",
                    i * spacingMetres, j * spacingMetres));
        return Build(f);
    }

    /// <summary>The crate moved to (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>), with storage
    /// bounds widened to contain it.</summary>
    internal static MapBuiltWorld BuildCrateAt(float x, float y, float z)
    {
        NativeFixture f = Crate();
        f.Document.Bounds = new()
        {
            MinX = Math.Min(-64f, x - 64f),
            MinZ = Math.Min(-64f, z - 64f),
            MaxX = Math.Max(64f, x + 64f),
            MaxZ = Math.Max(64f, z + 64f),
        };
        f.Document.Placements[0].X = x;
        f.Document.Placements[0].Y = y;
        f.Document.Placements[0].Z = z;
        return Build(f);
    }

    static MapBuiltWorld Build(NativeFixture f) => MapWorldBuilder.Build(f.Document, f.Assets, Options());
}
