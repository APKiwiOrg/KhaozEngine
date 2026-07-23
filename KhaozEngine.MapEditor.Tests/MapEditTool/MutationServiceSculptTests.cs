using System;
using System.IO;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;
using SampleDocs = KhaozEngine.Tests.MapDoc.MapDocumentFileTests;

namespace KhaozEngine.Tests.MapEditTool
{
    /// <summary>Headless tests for the <c>sculpt_*</c> MCP verbs (T3 of the terrain sculpt layer, #271):
    /// <see cref="MutationService.SculptApply"/>, <see cref="MutationService.SculptFlattenRegion"/>,
    /// <see cref="MutationService.SculptClear"/>, and their read counterpart
    /// <see cref="QueryService.SculptStats"/>. Drives the services through a real session, the same shape
    /// <see cref="MutationServiceFreezeZoneTests"/> uses.</summary>
    public class MutationServiceSculptTests
    {
        static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-mapedit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static (MapEditSession session, MutationService mutation, QueryService query) OpenSample(string dir)
        {
            string path = Path.Combine(dir, "zone.map.json");
            MapDocumentFile.Save(SampleDocs.SampleDoc(), path);
            var session = new MapEditSession();
            session.Open(path);
            return (session, new MutationService(session), new QueryService(session));
        }

        // A document with GentleAmplitude 0 (the same flat-base convention EditorToolSculptTests.Make uses),
        // large enough bounds for many sculpt tiles, and no sculpt layer yet.
        static (MapEditSession session, MutationService mutation, QueryService query) OpenFlat(string dir)
        {
            string path = Path.Combine(dir, "flat.map.json");
            var doc = new MapDocument
            {
                Id = "flat-zone",
                Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f },
            };
            doc.Terrain.GentleAmplitude = 0f;
            MapDocumentFile.Save(doc, path);
            var session = new MapEditSession();
            session.Open(path);
            return (session, new MutationService(session), new QueryService(session));
        }

        // ---- sculpt_apply ---------------------------------------------------------------------------------

        [Fact]
        public void SculptApply_Raise_ProducesTheExactBrushCoreDeltaAtCentre()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenSample(dir);

                // Centre-cell delta for raise is exactly strength * dt (full falloff weight at distance 0),
                // independent of cell size or the analytic base.
                SculptApplyResult result = mutation.SculptApply("raise", 0f, 0f, radius: 2.5f, strength: 4f, dt: 0.5f);

                Assert.True(result.Applied);
                Assert.Equal("raise", result.Brush);
                Assert.True(result.TouchedCellCount > 0);
                Assert.NotNull(result.DeltaMax);
                Assert.Equal(2f, result.DeltaMax!.Value, 5);   // centre cell: 4 * 0.5

                float cellSize = session.WithDocument((doc, _) => doc.TerrainOverrides!.CellSize);
                float centreDelta = session.WithDocument((doc, _) => doc.TerrainOverrides!.GetDelta(0, 0));
                Assert.Equal(2f, centreDelta, 5);
                Assert.Equal(MapTerrainOverrides.DefaultCellSize, cellSize, 5);
                Assert.True(session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptApply_Lower_IsTheRaiseNegation()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenSample(dir);
                mutation.SculptApply("lower", 0f, 0f, 2.5f, 4f, 0.5f);
                float centreDelta = session.WithDocument((doc, _) => doc.TerrainOverrides!.GetDelta(0, 0));
                Assert.Equal(-2f, centreDelta, 5);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptApply_SetHeight_TargetsTheAbsoluteWorldHeight()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenFlat(dir);
                TerrainField field = session.Field();
                float baseHeight = field.SampleHeight(0f, 0f);   // GentleAmplitude 0 -> uniformly the terrain's flat base

                SculptApplyResult result = mutation.SculptApply("set_height", 0f, 0f, radius: 3f, strength: 100f,
                    dt: 1f, targetHeight: baseHeight + 10f);

                Assert.True(result.Applied);
                // Strength/dt saturate the blend, so the composited height at the centre reaches the target exactly.
                float composited = session.Field().SampleHeight(0f, 0f);
                Assert.Equal(baseHeight + 10f, composited, 3);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptApply_SetHeight_WithoutTargetHeight_Throws()
        {
            string dir = NewTempDir();
            try
            {
                (_, MutationService mutation, _) = OpenSample(dir);
                Assert.Throws<ArgumentException>(() => mutation.SculptApply("set_height", 0f, 0f, 2f, 4f, 0.5f));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptApply_UnknownBrush_ThrowsNamingValidValues()
        {
            string dir = NewTempDir();
            try
            {
                (_, MutationService mutation, _) = OpenSample(dir);
                ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                    mutation.SculptApply("bulldoze", 0f, 0f, 2f, 4f, 0.5f));
                Assert.Contains("raise", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptApply_ZeroDt_IsCleanNoOp()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenSample(dir);
                Assert.False(session.IsDirty);

                SculptApplyResult result = mutation.SculptApply("raise", 0f, 0f, 2.5f, 4f, dt: 0f);

                Assert.False(result.Applied);
                Assert.Equal(0, result.TouchedCellCount);
                Assert.Null(result.DeltaMin);
                Assert.Null(result.DeltaMax);
                Assert.False(session.IsDirty);   // a true no-op never marks the session dirty
                Assert.Null(session.WithDocument((doc, _) => doc.TerrainOverrides));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptApply_NonPositiveRadius_IsCleanNoOp()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenSample(dir);
                SculptApplyResult result = mutation.SculptApply("raise", 0f, 0f, radius: 0f, strength: 4f, dt: 0.5f);
                Assert.False(result.Applied);
                Assert.False(session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptApply_ThenAManualNullOfTheCreatedLayer_ReproducesTheOriginalDocument()
        {
            // "Undo integration": SculptApply creates the layer through the same null-when-empty invariant
            // TerrainSculptStrokeCommand's Revert relies on, so a hand rollback (drop the layer it created back
            // to null) reproduces the pre-sculpt document exactly, the same guarantee the command itself gives.
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenSample(dir);
                string before = session.WithDocument((doc, _) => MapDocumentFile.SaveText(doc));

                SculptApplyResult result = mutation.SculptApply("raise", 0f, 0f, 2.5f, 4f, 0.5f);
                Assert.True(result.Applied);
                string after = session.WithDocument((doc, _) => MapDocumentFile.SaveText(doc));
                Assert.NotEqual(before, after);

                session.WithDocument((doc, _) => doc.TerrainOverrides = null);
                string reverted = session.WithDocument((doc, _) => MapDocumentFile.SaveText(doc));
                Assert.Equal(before, reverted);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptApply_OnAPreExistingTile_CapturedPriorGridRestoresExactly()
        {
            // "Undo integration" for a tile that already existed: capturing its grid before the call and manually
            // restoring it afterward reproduces the exact pre-call state, the same per-tile prior/final split
            // TerrainSculptStrokeCommand.Revert performs. Centred well inside tile (0,0)'s world extent [0,16) so
            // both dabs touch that one tile only, not its neighbours.
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenSample(dir);
                mutation.SculptApply("raise", 8f, 8f, 2.5f, 4f, 0.5f);   // creates tile (0,0)

                float[] prior = session.WithDocument((doc, _) =>
                {
                    doc.TerrainOverrides!.TryGetTile(0, 0, out MapSculptTile t);
                    return (float[])t.Deltas.Clone();
                });
                string afterFirst = session.WithDocument((doc, _) => MapDocumentFile.SaveText(doc));

                mutation.SculptApply("raise", 8f, 8f, 2.5f, 4f, 0.5f);   // a second, independent dab on the same tile
                string afterSecond = session.WithDocument((doc, _) => MapDocumentFile.SaveText(doc));
                Assert.NotEqual(afterFirst, afterSecond);

                session.WithDocument((doc, _) =>
                {
                    doc.TerrainOverrides!.PutTile(new MapSculptTile(0, 0, prior));
                    return 0;
                });
                string restored = session.WithDocument((doc, _) => MapDocumentFile.SaveText(doc));
                Assert.Equal(afterFirst, restored);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- sculpt_flatten_region -------------------------------------------------------------------------

        [Fact]
        public void SculptFlattenRegion_EveryCellInTheRectReadsExactlyTheTargetHeight()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenFlat(dir);
                TerrainField before = session.Field();
                float baseHeight = before.SampleHeight(0f, 0f);
                float target = baseHeight + 5f;

                SculptFlattenRegionResult result = mutation.SculptFlattenRegion(-4f, -4f, 4f, 4f, target);

                Assert.True(result.Applied);
                Assert.True(result.TouchedCellCount > 0);
                TerrainField after = session.Field();
                // Sample well inside the region (away from the bilinear blend at its edge): exactly the target.
                Assert.Equal(target, after.SampleHeight(0f, 0f), 3);
                Assert.Equal(target, after.SampleHeight(2f, -2f), 3);
                Assert.Equal(target, after.SampleHeight(-3f, 3f), 3);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptFlattenRegion_AlreadyFlatToTheSameTarget_IsCleanNoOp()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenFlat(dir);
                float target = session.Field().SampleHeight(0f, 0f) + 5f;
                mutation.SculptFlattenRegion(-4f, -4f, 4f, 4f, target);
                Assert.True(session.IsDirty);

                // A second flatten to the same target over the same rect changes nothing further.
                SculptFlattenRegionResult result = mutation.SculptFlattenRegion(-4f, -4f, 4f, 4f, target);
                Assert.False(result.Applied);
                Assert.Equal(0, result.TouchedCellCount);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptFlattenRegion_DegenerateRect_IsCleanNoOp()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenFlat(dir);
                SculptFlattenRegionResult result = mutation.SculptFlattenRegion(4f, 4f, -4f, -4f, 10f);
                Assert.False(result.Applied);
                Assert.False(session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- sculpt_clear -----------------------------------------------------------------------------------

        [Fact]
        public void SculptClear_NoLayer_IsCleanNoOp()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenSample(dir);
                SculptClearResult result = mutation.SculptClear();
                Assert.False(result.Applied);
                Assert.Equal(0, result.TilesRemoved);
                Assert.False(session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptClear_WholeLayer_RemovesEveryTileAndNullsTheLayer()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenSample(dir);
                mutation.SculptApply("raise", 0f, 0f, 2.5f, 4f, 0.5f);
                mutation.SculptApply("raise", 30f, 30f, 2.5f, 4f, 0.5f);   // a second, distant tile
                Assert.NotNull(session.WithDocument((doc, _) => doc.TerrainOverrides));

                SculptClearResult result = mutation.SculptClear();

                Assert.True(result.Applied);
                Assert.True(result.TilesRemoved >= 2);
                Assert.Null(session.WithDocument((doc, _) => doc.TerrainOverrides));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptClear_Region_RemovesOnlyIntersectingTiles()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenSample(dir);
                // Centred well inside each tile's own world extent (16 world units per tile at the default cell
                // size), so each dab touches exactly one tile: (0,0) at [0,16) and (1,1) at [16,32).
                mutation.SculptApply("raise", 8f, 8f, 2.5f, 4f, 0.5f);       // tile (0,0)
                mutation.SculptApply("raise", 24f, 24f, 2.5f, 4f, 0.5f);    // tile (1,1)

                SculptClearResult result = mutation.SculptClear(4f, 4f, 12f, 12f);   // fully inside tile (0,0) only

                Assert.True(result.Applied);
                Assert.Equal(1, result.TilesRemoved);
                MapTerrainOverrides overrides = session.WithDocument((doc, _) => doc.TerrainOverrides!);
                Assert.False(overrides.TryGetTile(0, 0, out _));
                Assert.True(overrides.TryGetTile(1, 1, out _));   // the distant tile survived
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptClear_PartialRectArguments_Throws()
        {
            string dir = NewTempDir();
            try
            {
                (_, MutationService mutation, _) = OpenSample(dir);
                Assert.Throws<ArgumentException>(() => mutation.SculptClear(minX: 0f));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptClear_ThenManualRestoreOfCapturedTiles_ReproducesTheOriginalDocument()
        {
            // Clear round trip: capture the whole layer's tiles before clearing, clear, then manually restore
            // them, reproducing the pre-clear document exactly (the same guarantee TerrainSculptClearCommand's
            // Revert gives, exercised end to end through the service).
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation, _) = OpenSample(dir);
                mutation.SculptApply("raise", 0f, 0f, 2.5f, 4f, 0.5f);
                string before = session.WithDocument((doc, _) => MapDocumentFile.SaveText(doc));
                var captured = new System.Collections.Generic.List<MapSculptTile>();
                session.WithDocument((doc, _) =>
                {
                    foreach (MapSculptTile t in doc.TerrainOverrides!.Tiles)
                        captured.Add(new MapSculptTile(t.TileX, t.TileZ, (float[])t.Deltas.Clone()));
                    return 0;
                });
                float cellSize = session.WithDocument((doc, _) => doc.TerrainOverrides!.CellSize);

                SculptClearResult result = mutation.SculptClear();
                Assert.True(result.Applied);
                Assert.Null(session.WithDocument((doc, _) => doc.TerrainOverrides));

                session.WithDocument((doc, _) =>
                {
                    doc.TerrainOverrides = new MapTerrainOverrides(cellSize);
                    foreach (MapSculptTile t in captured) doc.TerrainOverrides.PutTile(t);
                    return 0;
                });
                string restored = session.WithDocument((doc, _) => MapDocumentFile.SaveText(doc));
                Assert.Equal(before, restored);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- sculpt_stats -------------------------------------------------------------------------------------

        [Fact]
        public void SculptStats_NoLayer_ReportsHasLayerFalse()
        {
            string dir = NewTempDir();
            try
            {
                (_, _, QueryService query) = OpenSample(dir);
                SculptStatsResult stats = query.SculptStats();
                Assert.False(stats.HasLayer);
                Assert.Equal(0, stats.TileCount);
                Assert.Equal(0, stats.TouchedCellCount);
                Assert.Null(stats.DeltaMin);
                Assert.Null(stats.DeltaMax);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SculptStats_ReportsTileCountCellSizeAndDeltaRange()
        {
            string dir = NewTempDir();
            try
            {
                (_, MutationService mutation, QueryService query) = OpenSample(dir);
                mutation.SculptApply("raise", 0f, 0f, 2.5f, 4f, 0.5f);
                mutation.SculptApply("lower", 30f, 30f, 2.5f, 4f, 0.5f);

                SculptStatsResult stats = query.SculptStats();

                Assert.True(stats.HasLayer);
                Assert.Equal(MapTerrainOverrides.DefaultCellSize, stats.CellSize, 5);
                Assert.True(stats.TileCount >= 2);
                Assert.True(stats.TouchedCellCount > 0);
                Assert.NotNull(stats.DeltaMin);
                Assert.NotNull(stats.DeltaMax);
                Assert.True(stats.DeltaMin < 0f);   // the lower dab
                Assert.True(stats.DeltaMax > 0f);   // the raise dab
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
