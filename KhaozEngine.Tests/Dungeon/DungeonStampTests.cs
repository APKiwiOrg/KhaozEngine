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
        public void FloorPiece_RenderedTop_LandsOnCollisionSlabTop()
        {
            // The feet-on-floor engine fix: DungeonPiece.Floor pieces carry a negative Y offset (PieceMapper.
            // FloorPieceYOffset = -(floor slab thickness)) so the rendered floor tile's TOP - not its base -
            // lands at floorY, flush with the collision floor slab's top (where the capsule rests). Without it the
            // visible floor floated one slab thickness above the collision (character read as sunk into the floor).
            const float FloorKitThickness = 0.2f; // greybox floor tile height == collision slab thickness by design

            DungeonLayout layout = DungeonGenerator.Generate(SingleFloorConfig(), 9UL);
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f); // identity transform

            DungeonStampResult result = DungeonStamp.Build(layout, kit, plot);
            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;
            string floorKit = kit.Require(DungeonPiece.Floor);

            // A RoomFloor cell, its floor prop, and the collision floor slab under it.
            DungeonTile floorTile = FirstRoomFloorTile(layout);
            (float fx, _, float fz) = plot.TileCenter(floorTile, cell, floorHeight);
            float floorY = plot.BaseY + floorTile.Floor * floorHeight;

            DungeonPropInstance floorProp = result.Props.Single(p =>
                p.KitId == floorKit && MathF.Abs(p.X - fx) < 1e-3f && MathF.Abs(p.Z - fz) < 1e-3f);

            var slabBoxes = result.Statics
                .Where(s => s.Shape is BoxShape b && MathF.Abs(b.HalfExtents.Y - 0.1f) < 1e-3f)
                .Select(s => ((BoxShape)s.Shape, s.Pose))
                .ToList();
            (BoxShape slab, Pose slabPose) = slabBoxes.Single(sb =>
                ContainsPoint(sb.Item1, sb.Item2, new Vector3(fx, floorY - 0.05f, fz)));
            float slabTop = slabPose.Position.Y + slab.HalfExtents.Y;

            // The collision slab top is floorY, and the rendered floor tile's top (prop base + kit thickness) is
            // exactly there too - so the visible floor and the walkable surface coincide.
            Assert.Equal(floorY, slabTop, 3);
            Assert.Equal(slabTop, floorProp.Y + FloorKitThickness, 3);
        }

        private static DungeonTile FirstRoomFloorTile(DungeonLayout layout)
        {
            for (int f = 0; f < layout.Floors; f++)
                for (int z = 0; z < layout.Depth; z++)
                    for (int x = 0; x < layout.Width; x++)
                        if (layout.GetCell(x, z, f) == DungeonCellKind.RoomFloor)
                            return new DungeonTile(x, z, f);
            throw new Xunit.Sdk.XunitException("Layout has no RoomFloor cells.");
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
                            || kind == DungeonCellKind.StairMid
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

        // Every static box whose XZ centre sits over the stair run's tread footprint (plot-local: the cell-wide
        // strip from the lower cell's near edge through the top-tread cell's far edge) and whose bottom rests on
        // the lower floor. Floor slabs are NOT emitted over tread cells and walls sit off the strip, so these are
        // exactly the stair's solid step boxes (BuildStairSteps). Works under any plot yaw (inverse-rotates each
        // box centre back to plot-local before the strip test).
        private static List<(BoxShape Box, Pose Pose)> StairStepBoxes(
            DungeonStampResult result, DungeonPlotTransform plot, DungeonEdge stair, float cell, float floorHeight)
        {
            DungeonTile lower = stair.Path[0];
            DungeonTile upper = stair.Path[^2]; // top tread (second-to-last path cell)
            float lLocalX = (lower.X + 0.5f) * cell, lLocalZ = (lower.Z + 0.5f) * cell;
            float uLocalX = (upper.X + 0.5f) * cell, uLocalZ = (upper.Z + 0.5f) * cell;
            float minX = MathF.Min(lLocalX, uLocalX) - cell * 0.5f, maxX = MathF.Max(lLocalX, uLocalX) + cell * 0.5f;
            float minZ = MathF.Min(lLocalZ, uLocalZ) - cell * 0.5f, maxZ = MathF.Max(lLocalZ, uLocalZ) + cell * 0.5f;
            float floorY = plot.BaseY + lower.Floor * floorHeight;

            float cos = MathF.Cos(plot.YawRadians), sin = MathF.Sin(plot.YawRadians);
            var boxes = new List<(BoxShape, Pose)>();
            foreach ((PhysicsShape shape, Pose pose) in result.Statics)
            {
                if (shape is not BoxShape box) continue;
                if (MathF.Abs((pose.Position.Y - box.HalfExtents.Y) - floorY) > 0.05f) continue; // bottom on the lower floor
                float dx = pose.Position.X - plot.OriginX, dz = pose.Position.Z - plot.OriginZ;
                float localX = dx * cos + dz * sin, localZ = -dx * sin + dz * cos; // inverse of PieceMapper.TransformXZ
                if (localX >= minX - 1e-3f && localX <= maxX + 1e-3f && localZ >= minZ - 1e-3f && localZ <= maxZ + 1e-3f)
                    boxes.Add((box, pose));
            }
            return boxes;
        }

        [Fact]
        public void StairSteps_SpanFromLowerToUpperFloor()
        {
            DungeonLayout layout = MultiFloorLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f); // identity transform

            DungeonStampResult result = DungeonStamp.Build(layout, kit, plot);

            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;

            var stairEdges = layout.Edges.Where(e => e.Kind == DungeonEdgeKind.Stair).ToList();
            Assert.NotEmpty(stairEdges);

            DungeonEdge firstStair = stairEdges[0];
            List<(BoxShape Box, Pose Pose)> stepBoxes = StairStepBoxes(result, plot, firstStair, cell, floorHeight);
            Assert.True(stepBoxes.Count >= 2, $"expected a run of step boxes, got {stepBoxes.Count}");

            int lowerFloor = firstStair.Path[0].Floor;
            float expectedFloorY = plot.BaseY + lowerFloor * floorHeight;
            float expectedTopY = plot.BaseY + (lowerFloor + 1) * floorHeight;

            // Every step box rests on the lower floor; the tallest reaches the upper floor. So the run as a whole
            // spans exactly one floor, and each riser is under the default step-up height (0.4 m) - walkable.
            float minBottom = float.MaxValue, maxTop = float.MinValue, maxRiser = 0f;
            foreach ((BoxShape box, Pose pose) in stepBoxes)
            {
                minBottom = MathF.Min(minBottom, pose.Position.Y - box.HalfExtents.Y);
                maxTop = MathF.Max(maxTop, pose.Position.Y + box.HalfExtents.Y);
                maxRiser = MathF.Max(maxRiser, 2f * box.HalfExtents.Y); // each step is solid from the floor to its tread
            }

            Assert.True(MathF.Abs(minBottom - expectedFloorY) < 0.05f, $"steps should rest on the lower floor {expectedFloorY}, got {minBottom}");
            Assert.True(MathF.Abs(maxTop - expectedTopY) < 0.05f, $"the top step should reach the upper floor {expectedTopY}, got {maxTop}");

            // The riser (per-step rise) is floorHeight/steps and must stay under the step-up mount height so the
            // character climbs every tread.
            float riser = floorHeight / stepBoxes.Count;
            Assert.True(riser < 0.4f, $"stair riser {riser} must be under the 0.4 m step-up height to be walkable");
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

            // (b) The stair's solid step boxes must compose the plot yaw: each is oriented by
            // CreateFromAxisAngle(UnitY, LocalYaw(run) - plotYaw) so its depth axis (local +Z) projects onto the
            // world run direction (lower cell -> upper cell), and the run as a whole still climbs to the upper
            // floor. A re-flipped plot-yaw sign or a swapped composition order fails this at a non-90-degree yaw.
            var stairEdges = layout.Edges.Where(e => e.Kind == DungeonEdgeKind.Stair).ToList();
            Assert.NotEmpty(stairEdges);

            DungeonEdge stairEdge = stairEdges[0];
            List<(BoxShape Box, Pose Pose)> stepBoxes = StairStepBoxes(result, plot, stairEdge, cell, floorHeight);
            Assert.True(stepBoxes.Count >= 2, $"expected a run of step boxes, got {stepBoxes.Count}");

            DungeonTile lowerTile = stairEdge.Path[0];
            DungeonTile upperTile = stairEdge.Path[^2];
            (float lx, _, float lz) = plot.TileCenter(lowerTile, cell, floorHeight);
            (float ux, _, float uz) = plot.TileCenter(upperTile, cell, floorHeight);
            Vector2 runDir = Vector2.Normalize(new Vector2(ux - lx, uz - lz));

            // Each step box's local +Z (depth axis), rotated by its pose, projects onto the run direction; and the
            // step boxes are upright (no pitch, so local +Z is purely horizontal).
            foreach ((BoxShape _, Pose pose) in stepBoxes)
            {
                Vector3 depthAxis = Vector3.Transform(Vector3.UnitZ, pose.Orientation);
                Assert.Equal(0f, depthAxis.Y, 3); // upright step: no pitch
                Vector2 depthHorizontal = Vector2.Normalize(new Vector2(depthAxis.X, depthAxis.Z));
                Assert.Equal(runDir.X, depthHorizontal.X, 3);
                Assert.Equal(runDir.Y, depthHorizontal.Y, 3);
            }

            float maxTop = float.MinValue;
            foreach ((BoxShape box, Pose pose) in stepBoxes)
            {
                maxTop = MathF.Max(maxTop, pose.Position.Y + box.HalfExtents.Y);
            }

            float upperFloorY = plot.BaseY + (lowerTile.Floor + 1) * floorHeight;
            Assert.True(MathF.Abs(maxTop - upperFloorY) < 0.05f,
                $"expected the top step to reach the upper floor {upperFloorY}, got {maxTop}");
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
