using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Dungeon;
using KhaozEngine.MapDoc;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    public class DungeonStampTests
    {
        static DungeonConfig SingleFloorConfig() => new()
        {
            MaxFloors = 1,
            RoomCountTarget = 8,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 0,
        };

        static DungeonConfig FloorsConfig() => new()
        {
            MaxFloors = 3,
            RoomCountTarget = 16,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 0,
        };

        // Same scan pattern as DungeonMapDocEmitterTests.MultiFloorLayout: the first seed in this range whose
        // growth reaches an upper floor, so cross-floor (stair) assertions never pass vacuously.
        static DungeonLayout MultiFloorLayout()
        {
            for (ulong seed = 11; seed <= 60; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(FloorsConfig(), seed);
                if (layout.Rooms.Any(r => r.Floor > 0))
                {
                    return layout;
                }
            }

            throw new Xunit.Sdk.XunitException("No seed in 11..60 grew onto an upper floor.");
        }

        private static IEnumerable<Vector3> BoxCorners(BoxShape box, Pose pose)
        {
            foreach (float sx in new[] { -1f, 1f })
            {
                foreach (float sy in new[] { -1f, 1f })
                {
                    foreach (float sz in new[] { -1f, 1f })
                    {
                        var local = new Vector3(sx * box.HalfExtents.X, sy * box.HalfExtents.Y, sz * box.HalfExtents.Z);
                        yield return pose.Position + Vector3.Transform(local, pose.Orientation);
                    }
                }
            }
        }

        private static bool ContainsPoint(BoxShape box, Pose pose, Vector3 world, float eps = 1e-3f)
        {
            // Identity-transform boxes here are all axis-aligned (orientation is either exactly identity or a
            // pure yaw about world Y with no translation surprises), so a simple centered-extent test in the
            // box's own local frame (undoing translation and rotation) is exact.
            Vector3 local = Vector3.Transform(world - pose.Position, Quaternion.Conjugate(pose.Orientation));
            return MathF.Abs(local.X) <= box.HalfExtents.X + eps
                && MathF.Abs(local.Y) <= box.HalfExtents.Y + eps
                && MathF.Abs(local.Z) <= box.HalfExtents.Z + eps;
        }

        [Fact]
        public void WallBoxes_ExactlyCoverWallCells()
        {
            DungeonLayout layout = DungeonGenerator.Generate(SingleFloorConfig(), 9UL);
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f); // identity transform

            DungeonStampResult result = DungeonStamp.Build(layout, kit, plot);

            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;

            var wallBoxes = result.Statics
                .Where(s => s.Shape is BoxShape b && MathF.Abs(b.HalfExtents.Y - floorHeight * 0.5f) < 1e-3f)
                .Select(s => ((BoxShape)s.Shape, s.Pose))
                .ToList();

            Assert.NotEmpty(wallBoxes);

            int wallCellCount = 0;
            for (int f = 0; f < layout.Floors; f++)
            {
                for (int z = 0; z < layout.Depth; z++)
                {
                    for (int x = 0; x < layout.Width; x++)
                    {
                        if (layout.GetCell(x, z, f) != DungeonCellKind.Wall)
                        {
                            continue;
                        }

                        wallCellCount++;

                        var tile = new DungeonTile(x, z, f);
                        (float tx, float ty, float tz) = plot.TileCenter(tile, cell, floorHeight);
                        var point = new Vector3(tx, ty, tz);

                        int containing = wallBoxes.Count(wb => ContainsPoint(wb.Item1, wb.Item2, point));
                        Assert.Equal(1, containing);
                    }
                }
            }

            Assert.True(wallCellCount > 0);

            float totalVolume = wallBoxes.Sum(wb =>
                8f * wb.Item1.HalfExtents.X * wb.Item1.HalfExtents.Y * wb.Item1.HalfExtents.Z);
            float expectedVolume = wallCellCount * cell * cell * floorHeight;
            Assert.Equal(expectedVolume, totalVolume, 3);
        }

        [Fact]
        public void FloorSlabs_UnderEveryWalkableCell()
        {
            DungeonLayout layout = DungeonGenerator.Generate(SingleFloorConfig(), 9UL);
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f); // identity transform

            DungeonStampResult result = DungeonStamp.Build(layout, kit, plot);

            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;

            // Floor slabs are thin (halfY == 0.1) boxes whose local up axis stays world-up: the stair ramp is
            // also thin but pitched, so its rotated up axis is not (0, 1, 0).
            var slabBoxes = result.Statics
                .Where(s => s.Shape is BoxShape b && MathF.Abs(b.HalfExtents.Y - 0.1f) < 1e-3f
                    && Vector3.Transform(Vector3.UnitY, s.Pose.Orientation).Y > 0.99f)
                .Select(s => ((BoxShape)s.Shape, s.Pose))
                .ToList();

            Assert.NotEmpty(slabBoxes);

            int sampled = 0;
            for (int f = 0; f < layout.Floors; f++)
            {
                for (int z = 0; z < layout.Depth; z++)
                {
                    for (int x = 0; x < layout.Width; x++)
                    {
                        DungeonCellKind kind = layout.GetCell(x, z, f);
                        if (!DungeonLayout.IsWalkable(kind)
                            || kind == DungeonCellKind.StairLower
                            || kind == DungeonCellKind.StairUpper)
                        {
                            continue;
                        }

                        var tile = new DungeonTile(x, z, f);
                        (float tx, _, float tz) = plot.TileCenter(tile, cell, floorHeight);
                        float floorY = plot.BaseY + f * floorHeight;
                        var point = new Vector3(tx, floorY - 0.05f, tz);

                        int containing = slabBoxes.Count(sb => ContainsPoint(sb.Item1, sb.Item2, point));
                        Assert.Equal(1, containing);
                        sampled++;
                    }
                }
            }

            Assert.True(sampled > 0);
        }

        [Fact]
        public void StairBox_SpansFloors()
        {
            DungeonLayout layout = MultiFloorLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f); // identity transform

            DungeonStampResult result = DungeonStamp.Build(layout, kit, plot);

            var stairEdges = layout.Edges.Where(e => e.Kind == DungeonEdgeKind.Stair).ToList();
            Assert.NotEmpty(stairEdges);

            // Stair ramp boxes are the thin (halfY == 0.1) boxes whose rotated up axis is NOT world-up
            // (pitched). BuildStairRamps walks layout.Edges in order (via PieceMapper.EnumerateStairRuns), so
            // the nth ramp box pairs with the nth stair edge, same convention as the emitter's stair placements.
            var stairBoxes = result.Statics
                .Where(s => s.Shape is BoxShape b && MathF.Abs(b.HalfExtents.Y - 0.1f) < 1e-3f
                    && Vector3.Transform(Vector3.UnitY, s.Pose.Orientation).Y < 0.99f)
                .Select(s => ((BoxShape)s.Shape, s.Pose))
                .ToList();

            Assert.Equal(stairEdges.Count, stairBoxes.Count);

            DungeonEdge firstStair = stairEdges[0];
            (BoxShape Shape, Pose Pose) firstBox = stairBoxes[0];

            int lowerFloor = firstStair.Path[0].Floor;
            float expectedMinY = plot.BaseY + lowerFloor * layout.FloorHeightMeters;
            float expectedMaxY = plot.BaseY + (lowerFloor + 1) * layout.FloorHeightMeters;

            float minY = float.MaxValue;
            float maxY = float.MinValue;
            foreach (Vector3 corner in BoxCorners(firstBox.Shape, firstBox.Pose))
            {
                minY = MathF.Min(minY, corner.Y);
                maxY = MathF.Max(maxY, corner.Y);
            }

            Assert.True(MathF.Abs(minY - expectedMinY) < 0.3f,
                $"expected min Y near {expectedMinY}, got {minY}");
            Assert.True(MathF.Abs(maxY - expectedMaxY) < 0.3f,
                $"expected max Y near {expectedMaxY}, got {maxY}");
        }

        [Fact]
        public void Props_MatchEmitterCounts()
        {
            DungeonLayout layout = MultiFloorLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(3f, -2f, 1f, MathF.PI / 5f);
            var target = new MapDocument();

            DungeonMapDocEmitter.Emit(layout, kit, plot, target);
            DungeonStampResult stamp = DungeonStamp.Build(layout, kit, plot);

            Assert.NotEmpty(target.Placements);
            Assert.Equal(target.Placements.Count, stamp.Props.Count);

            // Per-kind breakdown for extra confidence the two sinks agree piece-by-piece, not just in total.
            // Every kind must actually occur (MultiFloorLayout guarantees rooms, walls, doors, and a stair
            // run, hence a landing too), so no per-kind comparison can pass vacuously as 0 == 0.
            foreach (string kitId in new[] { "dungeon_floor", "dungeon_wall", "dungeon_doorframe", "dungeon_stair", "dungeon_landing" })
            {
                int placementCount = target.Placements.Count(p => p.Kind == kitId);
                int propCount = stamp.Props.Count(p => p.KitId == kitId);
                Assert.True(placementCount > 0, $"no '{kitId}' placements: the per-kind comparison would be vacuous");
                Assert.Equal(placementCount, propCount);
            }
        }

        [Fact]
        public void StaticPoses_ComposePlotYaw()
        {
            // Pins the pose CONVENTION under a real plot rotation, not just at identity: a re-flipped
            // plot-yaw sign or a swapped pitch/yaw composition order reproduces identity-transform output
            // exactly, so only a non-90-degree yaw can catch it (the Task 12 emitter failure mode).
            DungeonLayout layout = MultiFloorLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(4f, -6f, 2f, MathF.PI / 3f);

            DungeonStampResult result = DungeonStamp.Build(layout, kit, plot);

            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;

            // (a) Wall boxes carry the symmetric-piece orientation, -plot.YawRadians in the engine-wide
            // Quaternion.CreateFromAxisAngle(UnitY, yaw) convention. Identify a real wall box by containment
            // of a known Wall cell sampled at MID-height: TileCenter's Y is the floor line, which is a shared
            // face between vertically stacked wall boxes, so a floor-level sample could sit in two boxes
            // within tolerance.
            var wallBoxes = result.Statics
                .Where(s => s.Shape is BoxShape b && MathF.Abs(b.HalfExtents.Y - floorHeight * 0.5f) < 1e-3f)
                .Select(s => ((BoxShape)s.Shape, s.Pose))
                .ToList();
            Assert.NotEmpty(wallBoxes);

            DungeonTile wallTile = FirstWallTile(layout);
            (float wx, _, float wz) = plot.TileCenter(wallTile, cell, floorHeight);
            var midHeight = new Vector3(wx, plot.BaseY + wallTile.Floor * floorHeight + floorHeight * 0.5f, wz);
            (BoxShape, Pose) wall = Assert.Single(wallBoxes, wb => ContainsPoint(wb.Item1, wb.Item2, midHeight));

            Quaternion expectedWall = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -plot.YawRadians);
            float dot = Quaternion.Dot(wall.Item2.Orientation, expectedWall);
            Assert.True(MathF.Abs(dot) > 1f - 1e-5f,
                $"wall orientation {wall.Item2.Orientation} != CreateFromAxisAngle(UnitY, -plotYaw) {expectedWall}");

            // (b) The stair ramp's local +Z (its climb axis), rotated by its pose orientation, must project
            // horizontally onto the world run direction derived independently from plot.TileCenter, and the
            // ramp must still climb (positive Y) all the way to the upper floor.
            var stairEdges = layout.Edges.Where(e => e.Kind == DungeonEdgeKind.Stair).ToList();
            var stairBoxes = result.Statics
                .Where(s => s.Shape is BoxShape b && MathF.Abs(b.HalfExtents.Y - 0.1f) < 1e-3f
                    && Vector3.Transform(Vector3.UnitY, s.Pose.Orientation).Y < 0.99f)
                .Select(s => ((BoxShape)s.Shape, s.Pose))
                .ToList();
            Assert.NotEmpty(stairEdges);
            Assert.Equal(stairEdges.Count, stairBoxes.Count);

            DungeonEdge stairEdge = stairEdges[0];
            (BoxShape Shape, Pose Pose) ramp = stairBoxes[0];

            (float lx, _, float lz) = plot.TileCenter(stairEdge.Path[0], cell, floorHeight);
            (float ux, _, float uz) = plot.TileCenter(stairEdge.Path[1], cell, floorHeight);
            Vector2 runDir = Vector2.Normalize(new Vector2(ux - lx, uz - lz));

            Vector3 climb = Vector3.Transform(Vector3.UnitZ, ramp.Pose.Orientation);
            Assert.True(climb.Y > 0f, $"ramp local +Z must climb upward, got Y component {climb.Y}");
            Vector2 climbHorizontal = Vector2.Normalize(new Vector2(climb.X, climb.Z));
            Assert.Equal(runDir.X, climbHorizontal.X, 3);
            Assert.Equal(runDir.Y, climbHorizontal.Y, 3);

            float maxY = float.MinValue;
            foreach (Vector3 corner in BoxCorners(ramp.Shape, ramp.Pose))
            {
                maxY = MathF.Max(maxY, corner.Y);
            }

            float upperFloorY = plot.BaseY + (stairEdge.Path[0].Floor + 1) * floorHeight;
            Assert.True(MathF.Abs(maxY - upperFloorY) < 0.3f,
                $"expected ramp AABB max Y near upper floor {upperFloorY}, got {maxY}");
        }

        private static DungeonTile FirstWallTile(DungeonLayout layout)
        {
            for (int f = 0; f < layout.Floors; f++)
            {
                for (int z = 0; z < layout.Depth; z++)
                {
                    for (int x = 0; x < layout.Width; x++)
                    {
                        if (layout.GetCell(x, z, f) == DungeonCellKind.Wall)
                        {
                            return new DungeonTile(x, z, f);
                        }
                    }
                }
            }

            throw new Xunit.Sdk.XunitException("Layout has no wall cells.");
        }
    }
}
