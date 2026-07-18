using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.Terrain;
using Xunit;
using SampleDocs = KhaozEngine.Tests.MapDoc.MapDocumentFileTests;

namespace KhaozEngine.Tests.MapEditTool
{
    /// <summary>Headless tests for <see cref="MutationService"/>: placement, spawn, player spawn, and region
    /// mutations, and the validate-revert invariant shared by every verb (apply, validate, revert-and-throw on
    /// error). All mutations run against <see cref="SampleDocs.SampleDoc"/> opened through a fresh session.</summary>
    public class MutationServicePlacementTests
    {
        static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-mapedit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static (MapEditSession session, MutationService mutation) OpenSample(string dir)
        {
            string path = Path.Combine(dir, "zone.map.json");
            MapDocumentFile.Save(SampleDocs.SampleDoc(), path);
            var session = new MapEditSession();
            session.Open(path);
            return (session, new MutationService(session));
        }

        [Fact]
        public void PlacementAdd_AutoId_GroundReported_DocumentGainsPlacement()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                TerrainField field = session.Field();

                MutationResult result = mutation.PlacementAdd("pine_a", 12f, -3.5f);

                Assert.Equal("placement_add", result.Verb);
                Assert.False(result.WorldChanged);
                Assert.Equal("p-pine_a-1", result.Id);
                Assert.Equal(field.SampleHeight(12f, -3.5f), result.GroundY);
                Assert.Contains("pine_a", result.Detail);

                MapPlacement added = session.WithDocument((doc, _) => doc.Placements.Single(p => p.Id == "p-pine_a-1"));
                Assert.Equal("pine_a", added.Kind);
                Assert.Equal(12f, added.X);
                Assert.Equal(-3.5f, added.Z);
                Assert.Null(added.Y);
                Assert.Equal(0f, added.Yaw);
                Assert.Equal(1f, added.Scale);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void PlacementAdd_DuplicateExplicitId_ThrowsAndDocumentUnchanged()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                string before = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                bool dirtyBefore = session.IsDirty;

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    mutation.PlacementAdd("building_inn", 0f, 0f, id: "inn"));

                Assert.StartsWith("mutation rejected:", ex.Message);
                Assert.Contains("duplicate placement id", ex.Message);

                string after = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                Assert.Equal(before, after);
                Assert.Equal(dirtyBefore, session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void PlacementMove_DefaultResnaps_KeepExplicitYPreserves()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                session.Mutate((doc, r) =>
                {
                    doc.Placements.Add(new MapPlacement { Id = "null-y", Kind = "pine_a", X = 0f, Z = 0f });
                    doc.Placements.Add(new MapPlacement { Id = "keep-y", Kind = "pine_a", X = 0f, Z = 0f, Y = 9f });
                    doc.Placements.Add(new MapPlacement { Id = "reset-y", Kind = "pine_a", X = 0f, Z = 0f, Y = 9f });
                    return 0;
                }, worldChanged: false);

                mutation.PlacementMove("null-y", 5f, 6f);
                mutation.PlacementMove("keep-y", 5f, 6f, keepExplicitY: true);
                mutation.PlacementMove("reset-y", 5f, 6f);

                session.WithDocument((doc, _) =>
                {
                    MapPlacement nullY = doc.Placements.Single(p => p.Id == "null-y");
                    Assert.Null(nullY.Y);
                    Assert.Equal(5f, nullY.X);
                    Assert.Equal(6f, nullY.Z);

                    MapPlacement keepY = doc.Placements.Single(p => p.Id == "keep-y");
                    Assert.Equal(9f, keepY.Y);
                    Assert.Equal(5f, keepY.X);

                    MapPlacement resetY = doc.Placements.Single(p => p.Id == "reset-y");
                    Assert.Null(resetY.Y);
                    Assert.Equal(5f, resetY.X);
                    return 0;
                });
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void PlacementRotate_Scale_Rename_Remove_RoundTrip()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult rotate = mutation.PlacementRotate("inn", 2.5f);
                Assert.Equal("placement_rotate", rotate.Verb);
                Assert.False(rotate.WorldChanged);
                Assert.Equal(2.5f, session.WithDocument((doc, _) => doc.Placements.Single(p => p.Id == "inn").Yaw));

                MutationResult scale = mutation.PlacementScale("inn", 2f);
                Assert.Equal("placement_scale", scale.Verb);
                Assert.Equal(2f, session.WithDocument((doc, _) => doc.Placements.Single(p => p.Id == "inn").Scale));

                MutationResult rename = mutation.PlacementRename("inn", "inn-2");
                Assert.Equal("placement_rename", rename.Verb);
                Assert.Equal("inn-2", rename.Id);
                Assert.True(session.WithDocument((doc, _) => doc.Placements.Any(p => p.Id == "inn-2")));
                Assert.False(session.WithDocument((doc, _) => doc.Placements.Any(p => p.Id == "inn")));

                MutationResult remove = mutation.PlacementRemove("inn-2");
                Assert.Equal("placement_remove", remove.Verb);
                Assert.False(session.WithDocument((doc, _) => doc.Placements.Any(p => p.Id == "inn-2")));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SpawnAdd_Move_SetEnabled_Rename_Remove_RoundTrip()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult add = mutation.SpawnAdd("bandit", 3f, 4f);
                Assert.Equal("spawn_add", add.Verb);
                Assert.False(add.WorldChanged);
                Assert.Equal("s-bandit-1", add.Id);
                MapSpawn added = session.WithDocument((doc, _) => doc.Spawns.Single(s => s.Id == "s-bandit-1"));
                Assert.Equal("bandit", added.ArchetypeId);
                Assert.True(added.Enabled);

                MutationResult move = mutation.SpawnMove("s-bandit-1", 10f, 12f);
                Assert.Equal("spawn_move", move.Verb);
                MapSpawn afterMove = session.WithDocument((doc, _) => doc.Spawns.Single(s => s.Id == "s-bandit-1"));
                Assert.Equal(10f, afterMove.X);
                Assert.Equal(12f, afterMove.Z);

                MutationResult disable = mutation.SpawnSetEnabled("s-bandit-1", false);
                Assert.Equal("spawn_set_enabled", disable.Verb);
                Assert.False(session.WithDocument((doc, _) => doc.Spawns.Single(s => s.Id == "s-bandit-1").Enabled));

                MutationResult rename = mutation.SpawnRename("s-bandit-1", "bandit-2");
                Assert.Equal("spawn_rename", rename.Verb);
                Assert.Equal("bandit-2", rename.Id);
                Assert.True(session.WithDocument((doc, _) => doc.Spawns.Any(s => s.Id == "bandit-2")));

                MutationResult remove = mutation.SpawnRemove("bandit-2");
                Assert.Equal("spawn_remove", remove.Verb);
                Assert.False(session.WithDocument((doc, _) => doc.Spawns.Any(s => s.Id == "bandit-2")));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SpawnAdd_ReusesFreedIdGapAfterRemoval()
        {
            // GenerateId always searches from N=1 up, so removing a middle id frees a gap the NEXT auto-id add
            // reuses instead of always appending past the highest N seen so far.
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult a = mutation.SpawnAdd("bandit", 0f, 0f);
                MutationResult b = mutation.SpawnAdd("bandit", 1f, 1f);
                MutationResult c = mutation.SpawnAdd("bandit", 2f, 2f);
                Assert.Equal("s-bandit-1", a.Id);
                Assert.Equal("s-bandit-2", b.Id);
                Assert.Equal("s-bandit-3", c.Id);

                mutation.SpawnRemove("s-bandit-2");

                MutationResult d = mutation.SpawnAdd("bandit", 3f, 3f);
                Assert.Equal("s-bandit-2", d.Id);   // the freed slot, not "s-bandit-4"
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void PlayerSpawnAdd_AutoId_DocumentGainsPlayerSpawn()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult result = mutation.PlayerSpawnAdd(12f, -3.5f, yaw: 0.75f);

                Assert.Equal("player_spawn_add", result.Verb);
                Assert.False(result.WorldChanged);
                Assert.Equal("player-1", result.Id);
                Assert.Contains("player spawn", result.Detail);

                MapPlayerSpawn added = session.WithDocument((doc, _) => doc.PlayerSpawns.Single(s => s.Id == "player-1"));
                Assert.Equal(12f, added.X);
                Assert.Equal(-3.5f, added.Z);
                Assert.Equal(0.75f, added.Yaw);
                Assert.True(added.Enabled);

                // A second auto-id add skips the taken "player-1" and lands on "player-2".
                MutationResult second = mutation.PlayerSpawnAdd(0f, 0f);
                Assert.Equal("player-2", second.Id);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void PlayerSpawnAdd_ReusesFreedIdGapAfterRemoval()
        {
            // Same GenerateId gap-reuse contract as SpawnAdd_ReusesFreedIdGapAfterRemoval, for player spawns.
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult a = mutation.PlayerSpawnAdd(0f, 0f);
                MutationResult b = mutation.PlayerSpawnAdd(1f, 1f);
                MutationResult c = mutation.PlayerSpawnAdd(2f, 2f);
                Assert.Equal("player-1", a.Id);
                Assert.Equal("player-2", b.Id);
                Assert.Equal("player-3", c.Id);

                mutation.PlayerSpawnRemove("player-2");

                MutationResult d = mutation.PlayerSpawnAdd(3f, 3f);
                Assert.Equal("player-2", d.Id);   // the freed slot, not "player-4"
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void PlayerSpawnAdd_DuplicateExplicitId_ThrowsAndDocumentUnchanged()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult first = mutation.PlayerSpawnAdd(0f, 0f, id: "start");
                Assert.Equal("start", first.Id);

                string before = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                bool dirtyBefore = session.IsDirty;

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    mutation.PlayerSpawnAdd(1f, 1f, id: "start"));

                Assert.StartsWith("mutation rejected:", ex.Message);
                Assert.Contains("duplicate player spawn id", ex.Message);

                string after = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                Assert.Equal(before, after);
                Assert.Equal(dirtyBefore, session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void PlayerSpawnMove_SetYaw_SetEnabled_Rename_Remove_RoundTrip()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult add = mutation.PlayerSpawnAdd(3f, 4f);
                Assert.Equal("player-1", add.Id);

                MutationResult move = mutation.PlayerSpawnMove("player-1", 10f, 12f);
                Assert.Equal("player_spawn_move", move.Verb);
                MapPlayerSpawn afterMove = session.WithDocument((doc, _) => doc.PlayerSpawns.Single(s => s.Id == "player-1"));
                Assert.Equal(10f, afterMove.X);
                Assert.Equal(12f, afterMove.Z);

                MutationResult yaw = mutation.PlayerSpawnSetYaw("player-1", 2.1f);
                Assert.Equal("player_spawn_set_yaw", yaw.Verb);
                Assert.Equal(2.1f, session.WithDocument((doc, _) => doc.PlayerSpawns.Single(s => s.Id == "player-1").Yaw));

                MutationResult disable = mutation.PlayerSpawnSetEnabled("player-1", false);
                Assert.Equal("player_spawn_set_enabled", disable.Verb);
                Assert.False(session.WithDocument((doc, _) => doc.PlayerSpawns.Single(s => s.Id == "player-1").Enabled));

                MutationResult rename = mutation.PlayerSpawnRename("player-1", "start-point");
                Assert.Equal("player_spawn_rename", rename.Verb);
                Assert.Equal("start-point", rename.Id);
                Assert.True(session.WithDocument((doc, _) => doc.PlayerSpawns.Any(s => s.Id == "start-point")));
                Assert.False(session.WithDocument((doc, _) => doc.PlayerSpawns.Any(s => s.Id == "player-1")));

                MutationResult remove = mutation.PlayerSpawnRemove("start-point");
                Assert.Equal("player_spawn_remove", remove.Verb);
                Assert.False(session.WithDocument((doc, _) => doc.PlayerSpawns.Any(s => s.Id == "start-point")));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void PlayerSpawnRename_ToTakenId_ThrowsAndDocumentUnchanged()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                mutation.PlayerSpawnAdd(0f, 0f, id: "player-a");
                mutation.PlayerSpawnAdd(1f, 1f, id: "player-b");

                string before = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                bool dirtyBefore = session.IsDirty;

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    mutation.PlayerSpawnRename("player-a", "player-b"));
                Assert.Contains("already exists", ex.Message);

                string after = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                Assert.Equal(before, after);
                Assert.Equal(dirtyBefore, session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void RegionAdd_EditShape_Rename_Remove_RoundTrip()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                var disc = new DiscShapeDoc { CenterX = 1f, CenterZ = 2f, Radius = 5f };
                MutationResult add = mutation.RegionAdd("camp", disc);
                Assert.Equal("region_add", add.Verb);
                Assert.False(add.WorldChanged);
                Assert.IsType<DiscShapeDoc>(session.WithDocument((doc, _) => doc.Regions.Single(r => r.Name == "camp").Shape));

                var rect = new RectShapeDoc { MinX = -5f, MinZ = -5f, MaxX = 5f, MaxZ = 5f };
                MutationResult edit = mutation.RegionEditShape("camp", rect);
                Assert.Equal("region_edit_shape", edit.Verb);
                Assert.IsType<RectShapeDoc>(session.WithDocument((doc, _) => doc.Regions.Single(r => r.Name == "camp").Shape));

                MutationResult rename = mutation.RegionRename("camp", "camp-2");
                Assert.Equal("region_rename", rename.Verb);
                Assert.Equal("camp-2", rename.Id);
                Assert.True(session.WithDocument((doc, _) => doc.Regions.Any(r => r.Name == "camp-2")));

                MutationResult remove = mutation.RegionRemove("camp-2");
                Assert.Equal("region_remove", remove.Verb);
                Assert.False(session.WithDocument((doc, _) => doc.Regions.Any(r => r.Name == "camp-2")));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        /// <summary>Covers the atomicity fix: <see cref="MutationService.RegionEditShape"/> now builds
        /// <see cref="KhaozEngine.MapEditor.EditRegionShapeCommand"/> (region lookup and old-shape capture) inside the choke point's
        /// mutate callback instead of a separate, earlier <see cref="MapEditSession.WithDocument{T}"/> read. A
        /// concurrent-mutation race between the two acquisitions can't be reproduced single-threaded, so this
        /// exercises the observable contract that race would break: forces the whole-document validation to
        /// reject the edit (via an unrelated pre-existing document defect) and asserts revert restores the shape
        /// this call captured, and only this call, proving the read and the revert operate on the same value
        /// from one lock acquisition.</summary>
        [Fact]
        public void RegionEditShape_RejectedEdit_RevertsShapeCapturedInSameCall()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                var disc = new DiscShapeDoc { CenterX = 1f, CenterZ = 2f, Radius = 5f };
                mutation.RegionAdd("camp", disc);

                // Push the document into an invalid state for a reason unrelated to the region shape (a
                // duplicate placement id), so the upcoming edit's apply+validate+revert cycle is exercised
                // honestly: apply succeeds, validate fails on the pre-existing defect, and revert must put
                // back the shape captured by this call's factory, not a shape from an earlier read.
                session.Mutate((doc, _) =>
                {
                    doc.Placements.Add(new MapPlacement { Id = "dup", Kind = "pine_a", X = 0f, Z = 0f });
                    doc.Placements.Add(new MapPlacement { Id = "dup", Kind = "pine_a", X = 1f, Z = 1f });
                    return 0;
                }, worldChanged: false);
                bool dirtyBefore = session.IsDirty;

                var rect = new RectShapeDoc { MinX = -5f, MinZ = -5f, MaxX = 5f, MaxZ = 5f };
                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    mutation.RegionEditShape("camp", rect));

                Assert.StartsWith("mutation rejected:", ex.Message);
                Assert.Contains("duplicate placement id", ex.Message);

                DiscShapeDoc reverted = Assert.IsType<DiscShapeDoc>(
                    session.WithDocument((doc, _) => doc.Regions.Single(r => r.Name == "camp").Shape));
                Assert.Equal(disc.CenterX, reverted.CenterX);
                Assert.Equal(disc.CenterZ, reverted.CenterZ);
                Assert.Equal(disc.Radius, reverted.Radius);
                Assert.Equal(dirtyBefore, session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Mutation_MarksSessionDirty()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                Assert.False(session.IsDirty);

                mutation.PlacementAdd("pine_a", 0f, 0f);

                Assert.True(session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void InvalidMutation_LeavesSessionCleanAndValid()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                bool dirtyBefore = session.IsDirty;

                Assert.Throws<InvalidOperationException>(() =>
                    mutation.PlacementAdd("building_inn", 0f, 0f, id: "inn"));

                Assert.Equal(dirtyBefore, session.IsDirty);
                ValidateResult validation = session.Validate();
                Assert.True(validation.StructuralValid);
                Assert.Empty(validation.StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
