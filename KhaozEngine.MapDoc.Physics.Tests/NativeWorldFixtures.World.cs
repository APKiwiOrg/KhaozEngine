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
    }

    internal static MapBuiltWorld BuildStackedCave() => Build(StackedCave());

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

    /// <summary>Two crates at the origin, so a ray meets both at one distance. b-crate is authored first.</summary>
    internal static MapBuiltWorld BuildTwinCrates() => Build(Resolve(FloorSurfaces(),
        OnFloor("b-crate", "crate", 0f, 0f),
        OnFloor("a-crate", "crate", 0f, 0f)));

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
