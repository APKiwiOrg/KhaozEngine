using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldInteriorsTestsLattice
{
    [Theory]
    [InlineData(1f, 1, 4)]
    [InlineData(1f, -2, 4)]
    [InlineData(1.2f, 1, 5)]
    [InlineData(1.2f, -2, 5)]
    public void EveryTileCellSurvivesBothSidesOfARecentre(float tileSize, int tile, int cellsPerTile)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(2, RegionCoord.Of(tile, tile));
        doc.TileSize = tileSize;
        doc.SetSettings(tile, tile, 0, TileSettings.Indoors);
        var source = new TileWorldInteriors(doc, TileWorldCatalogs.Greybox(), new NoMeshes());
        float cellSize = TileWorldInteriors.CellSizeFor(tileSize);
        foreach (Vector2 focus in new[] { Vector2.Zero, new Vector2(64f) })
        {
            var window = InteriorWindow.Centred(focus, cellSize, InteriorWindow.CellsFor(512f, cellSize));
            InteriorSourceResult collected = source.Collect(window);
            var field = InteriorHeightFieldBuilder.Build(collected.Volumes, window, collected.Encoding);
            int retained = 0;
            double originX = System.Math.Round(window.Min.X / (double)cellSize);
            double originZ = System.Math.Round(window.Min.Y / (double)cellSize);
            for (int z = 0; z < window.Cells; z++)
                for (int x = 0; x < window.Cells; x++)
                    if (field.TryGetSpan(x, z, out _, out _))
                    {
                        Assert.InRange(originX + x, tile * cellsPerTile, (tile + 1) * cellsPerTile - 1);
                        Assert.InRange(originZ + z, -(tile + 1) * cellsPerTile, -tile * cellsPerTile - 1);
                        retained++;
                    }
            Assert.Equal(cellsPerTile * cellsPerTile, retained);
            for (int z = 0; z < cellsPerTile; z++)
                for (int x = 0; x < cellsPerTile; x++)
                    Assert.True(field.Contains(new Vector3(
                        (float)((tile * cellsPerTile + x + .5) * cellSize), 1f,
                        (float)((-(tile + 1) * cellsPerTile + z + .5) * cellSize))));
        }
    }

    sealed class NoMeshes : ITileMeshGeometry
    {
        public bool TryLoad(string meshRef, [NotNullWhen(true)] out ClearanceMesh? mesh, [NotNullWhen(false)] out string? error)
        {
            mesh = null;
            error = "No roofs in this fixture";
            return false;
        }
    }
}
