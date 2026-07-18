using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Dungeon;
using KhaozEngine.MapDoc;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    /// <summary>Roofed-interior coverage: ceilings roof every walkable cell except open verticals, both sinks
    /// agree, the collision slabs match the props, and Open mode stays byte-for-byte the pre-ceiling output.</summary>
    public class DungeonCeilingTests
    {
        const string CeilingKit = "dungeon_ceiling";

        static DungeonConfig SingleFloor(DungeonCeilingMode mode, float? ceilingHeight = null) => new()
        {
            MaxFloors = 1,
            RoomCountTarget = 8,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 0,
            CeilingMode = mode,
            CeilingHeightMeters = ceilingHeight,
        };

        static DungeonConfig MultiFloor(DungeonCeilingMode mode) => new()
        {
            MaxFloors = 3,
            RoomCountTarget = 16,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 0,
            CeilingMode = mode,
        };

        // First seed 11..60 whose growth reaches an upper floor, so stair (open-vertical) assertions never
        // pass vacuously. Same scan pattern as DungeonStampTests.MultiFloorLayout.
        static DungeonLayout RoofedMultiFloorLayout()
        {
            for (ulong seed = 11; seed <= 60; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(MultiFloor(DungeonCeilingMode.Roofed), seed);
                if (layout.Rooms.Any(r => r.Floor > 0))
                {
                    return layout;
                }
            }

            throw new Xunit.Sdk.XunitException("No seed in 11..60 grew onto an upper floor.");
        }

        // The independent oracle for HasCeiling, derived only from the public raster: a cell that is walkable OR a
        // StairVoid shaft-headroom cutout, whose same-XZ cell one floor up is neither walkable nor StairVoid. The
        // StairVoid is roofed at its own (upper) floor's ceiling height so the shaft is capped overhead, but a tread
        // (walkable, on the lower floor) stays uncapped because the cell directly above it IS a StairVoid (the
        // above-is-StairVoid exemption); out-of-range floors read Empty, hence not walkable and not StairVoid either.
        static int ExpectedCeilingCells(DungeonLayout layout)
        {
            int count = 0;
            for (int f = 0; f < layout.Floors; f++)
            {
                for (int z = 0; z < layout.Depth; z++)
                {
                    for (int x = 0; x < layout.Width; x++)
                    {
                        DungeonCellKind cell = layout.GetCell(x, z, f);
                        if (!DungeonLayout.IsWalkable(cell) && cell != DungeonCellKind.StairVoid)
                        {
                            continue;
                        }

                        DungeonCellKind above = layout.GetCell(x, z, f + 1);
                        if (!DungeonLayout.IsWalkable(above) && above != DungeonCellKind.StairVoid)
                        {
                            count++;
                        }
                    }
                }
            }

            return count;
        }

        static bool ContainsPoint(BoxShape box, Pose pose, Vector3 world, float eps = 1e-3f)
        {
            Vector3 local = Vector3.Transform(world - pose.Position, Quaternion.Conjugate(pose.Orientation));
            return MathF.Abs(local.X) <= box.HalfExtents.X + eps
                && MathF.Abs(local.Y) <= box.HalfExtents.Y + eps
                && MathF.Abs(local.Z) <= box.HalfExtents.Z + eps;
        }

        [Fact]
        public void CeilingMode_DoesNotAffectLayoutStructure()
        {
            // Ceilings are pure sink-time geometry: flipping the mode must not perturb generation, so the two
            // layouts share a structure hash. This is the determinism guarantee from the brief.
            DungeonLayout open = DungeonGenerator.Generate(SingleFloor(DungeonCeilingMode.Open), 9UL);
            DungeonLayout roofed = DungeonGenerator.Generate(SingleFloor(DungeonCeilingMode.Roofed), 9UL);

            Assert.Equal(open.LayoutHash(), roofed.LayoutHash());
            Assert.Equal(DungeonCeilingMode.Open, open.CeilingMode);
            Assert.Equal(DungeonCeilingMode.Roofed, roofed.CeilingMode);
        }

        [Fact]
        public void CeilingHeight_DefaultsToFloorHeight_OrHonorsExplicit()
        {
            DungeonLayout defaulted = DungeonGenerator.Generate(SingleFloor(DungeonCeilingMode.Roofed), 9UL);
            Assert.Equal(defaulted.FloorHeightMeters, defaulted.CeilingHeightMeters);

            DungeonLayout custom = DungeonGenerator.Generate(SingleFloor(DungeonCeilingMode.Roofed, ceilingHeight: 3f), 9UL);
            Assert.Equal(3f, custom.CeilingHeightMeters);
        }

        [Fact]
        public void Roofed_CeilingProps_CoverWalkableCellsMinusOpenVerticals()
        {
            DungeonLayout layout = DungeonGenerator.Generate(SingleFloor(DungeonCeilingMode.Roofed), 9UL);
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);

            DungeonStampResult stamp = DungeonStamp.Build(layout, kit, plot);

            int expected = ExpectedCeilingCells(layout);
            Assert.True(expected > 0, "single-floor roofed layout must have ceiling cells");

            var ceilingProps = stamp.Props.Where(p => p.KitId == CeilingKit).ToList();
            Assert.Equal(expected, ceilingProps.Count);

            // Every ceiling prop sits above a distinct walkable cell at floorY + ceiling height.
            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;
            for (int z = 0; z < layout.Depth; z++)
            {
                for (int x = 0; x < layout.Width; x++)
                {
                    if (!PieceMapperHasCeiling(layout, x, z, 0))
                    {
                        continue;
                    }

                    var tile = new DungeonTile(x, z, 0);
                    (float tx, float ty, float tz) = plot.TileCenter(tile, cell, floorHeight);
                    Assert.Contains(ceilingProps, p =>
                        MathF.Abs(p.X - tx) < 1e-3f
                        && MathF.Abs(p.Z - tz) < 1e-3f
                        && MathF.Abs(p.Y - (ty + layout.CeilingHeightMeters)) < 1e-3f);
                }
            }
        }

        [Fact]
        public void Roofed_StairEmergence_AndBase_StayOpen()
        {
            DungeonLayout layout = RoofedMultiFloorLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);

            DungeonStampResult stamp = DungeonStamp.Build(layout, kit, plot);
            var ceilingProps = stamp.Props.Where(p => p.KitId == CeilingKit).ToList();

            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;

            DungeonEdge stair = layout.Edges.First(e => e.Kind == DungeonEdgeKind.Stair);
            // Path is [StairLower, StairMid, StairUpper, StairTop]. Every tread has a StairVoid open-shaft cutout
            // directly above it, so none of the three tread cells may be roofed at its OWN (lower) floor level - the
            // ramp stays walkable-through at head height from the base to the emergence. (The shaft is still roofed
            // higher up, at the StairVoid's own upper-floor ceiling level, a full ceiling height above the climb -
            // asserted by Roofed_StairwellShaft_RoofedAtTop_ClearForTheClimb; this test only pins the head clearance.)
            DungeonTile stairLower = stair.Path[0];
            DungeonTile stairMid = stair.Path[1];
            DungeonTile stairUpper = stair.Path[2];

            foreach (DungeonTile tread in new[] { stairLower, stairMid, stairUpper })
            {
                (float tx, float tyy, float tz) = plot.TileCenter(tread, cell, floorHeight);
                Assert.DoesNotContain(ceilingProps, p =>
                    MathF.Abs(p.X - tx) < 1e-3f && MathF.Abs(p.Z - tz) < 1e-3f
                    && MathF.Abs(p.Y - (tyy + layout.CeilingHeightMeters)) < 1e-3f);
            }
        }

        [Fact]
        public void Roofed_StairwellShaft_RoofedAtTop_ClearForTheClimb()
        {
            // A walker climbing the stair must find the ramp itself clear - no floor slab capping the shaft
            // mid-climb and no ceiling capping a tread at head height - while the shaft is still ROOFED overhead so
            // it does not read as open to the sky. Concretely, over every tread's shaft column (the StairVoid above
            // it): (a) no floor slab at the void's own floor level, (b) no ceiling at the tread's own (lower)
            // ceiling level, but (c) a ceiling slab DOES sit at the void's own (upper) ceiling level - the roof,
            // a full ceiling height above the floor the treads climb to, well clear of the ~1.8 m capsule.
            // StairTop, the landing beyond the top tread, keeps its own floor slab (the walker steps onto it).
            DungeonLayout layout = RoofedMultiFloorLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);

            DungeonStampResult stamp = DungeonStamp.Build(layout, kit, plot);

            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;

            DungeonEdge stair = layout.Edges.First(e => e.Kind == DungeonEdgeKind.Stair);
            DungeonTile stairLower = stair.Path[0];
            DungeonTile stairMid = stair.Path[1];
            DungeonTile stairUpper = stair.Path[2];
            DungeonTile stairTop = stair.Path[3];

            // Flat (world-up) thin slabs only - floor OR ceiling. The solid step boxes are NOT thin (their halfY
            // is a tread height, not 0.1), so they are excluded here on purpose.
            var slabBoxes = stamp.Statics
                .Where(s => s.Shape is BoxShape b && MathF.Abs(b.HalfExtents.Y - 0.1f) < 1e-3f
                    && Vector3.Transform(Vector3.UnitY, s.Pose.Orientation).Y > 0.99f)
                .Select(s => ((BoxShape)s.Shape, s.Pose))
                .ToList();
            Assert.NotEmpty(slabBoxes);

            // Clearance check: the roof (void ceiling level = upperFloorY + ceilingHeight) sits a full ceiling
            // height above the upper floor the climb reaches, so a 1.8 m capsule emerging there never hits it.
            Assert.True(layout.CeilingHeightMeters >= 2f * 0.9f,
                $"roof clearance {layout.CeilingHeightMeters:F2} m must exceed the 1.8 m capsule height");

            foreach (DungeonTile tread in new[] { stairLower, stairMid, stairUpper })
            {
                (float tx, float tyy, float tz) = plot.TileCenter(tread, cell, floorHeight);
                var voidFloorPoint = new Vector3(tx, tyy + floorHeight - 0.05f, tz);                          // a would-be slab capping the void's floor level
                var voidCeilingPoint = new Vector3(tx, tyy + floorHeight + layout.CeilingHeightMeters + 0.1f, tz); // the shaft roof (upper ceiling level)
                var treadCeilingPoint = new Vector3(tx, tyy + layout.CeilingHeightMeters + 0.1f, tz);          // the tread's own (lower) ceiling level - head height on the climb

                // (a) no floor slab mid-shaft, and (b) no ceiling at the tread's head height: the ramp stays clear.
                Assert.DoesNotContain(slabBoxes, sb => ContainsPoint(sb.Item1, sb.Item2, voidFloorPoint));
                Assert.DoesNotContain(slabBoxes, sb => ContainsPoint(sb.Item1, sb.Item2, treadCeilingPoint));
                // (c) the shaft IS roofed at the top (upper ceiling level) - no open sky.
                Assert.Contains(slabBoxes, sb => ContainsPoint(sb.Item1, sb.Item2, voidCeilingPoint));
            }

            // StairTop DOES carry its own floor slab (the landing a walker steps onto, sampled just below its
            // floor level) - a sanity/regression guard that the walker still has solid ground at the top.
            (float sx, float sy, float sz) = plot.TileCenter(stairTop, cell, floorHeight);
            var stairTopFloorPoint = new Vector3(sx, sy - 0.05f, sz);
            Assert.Contains(slabBoxes, sb => ContainsPoint(sb.Item1, sb.Item2, stairTopFloorPoint));
        }

        [Fact]
        public void Roofed_CeilingSlabs_CoverEveryCeilingCell()
        {
            DungeonLayout layout = DungeonGenerator.Generate(SingleFloor(DungeonCeilingMode.Roofed), 9UL);
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);

            DungeonStampResult result = DungeonStamp.Build(layout, kit, plot);

            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;
            float ceilingHeight = layout.CeilingHeightMeters;

            // Ceiling slabs: thin (halfY 0.1), world-up like floor slabs, but sitting a full ceiling height
            // above the floor (floor slabs sit at -0.1). On a single identity-plot floor, center Y disambiguates.
            var ceilingBoxes = result.Statics
                .Where(s => s.Shape is BoxShape b && MathF.Abs(b.HalfExtents.Y - 0.1f) < 1e-3f
                    && Vector3.Transform(Vector3.UnitY, s.Pose.Orientation).Y > 0.99f
                    && s.Pose.Position.Y > floorHeight * 0.5f)
                .Select(s => ((BoxShape)s.Shape, s.Pose))
                .ToList();
            Assert.NotEmpty(ceilingBoxes);

            int sampled = 0;
            for (int z = 0; z < layout.Depth; z++)
            {
                for (int x = 0; x < layout.Width; x++)
                {
                    if (!PieceMapperHasCeiling(layout, x, z, 0))
                    {
                        continue;
                    }

                    var tile = new DungeonTile(x, z, 0);
                    (float tx, float ty, float tz) = plot.TileCenter(tile, cell, floorHeight);
                    // A point just inside the slab (its center height, one ThinHalfThickness above the underside).
                    var point = new Vector3(tx, ty + ceilingHeight + 0.1f, tz);

                    int containing = ceilingBoxes.Count(cb => ContainsPoint(cb.Item1, cb.Item2, point));
                    Assert.Equal(1, containing);
                    sampled++;
                }
            }

            Assert.Equal(ExpectedCeilingCells(layout), sampled);
        }

        [Fact]
        public void Open_ProducesNoCeilings_AndRoofedIsPurelyAdditive()
        {
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);
            DungeonKitMap kit = DungeonKitMap.Greybox();

            DungeonLayout open = DungeonGenerator.Generate(SingleFloor(DungeonCeilingMode.Open), 9UL);
            DungeonLayout roofed = DungeonGenerator.Generate(SingleFloor(DungeonCeilingMode.Roofed), 9UL);

            DungeonStampResult openStamp = DungeonStamp.Build(open, kit, plot);
            DungeonStampResult roofedStamp = DungeonStamp.Build(roofed, kit, plot);

            // Open mode emits no ceiling props and no static above the wall tops (nothing roofs it).
            Assert.DoesNotContain(openStamp.Props, p => p.KitId == CeilingKit);
            Assert.DoesNotContain(openStamp.Statics, s => s.Pose.Position.Y > open.FloorHeightMeters);

            // Roofing is purely additive: strip the ceilings from the roofed stamp and the remaining props are
            // identical (same order, same values) to the open stamp's props.
            var roofedNonCeiling = roofedStamp.Props.Where(p => p.KitId != CeilingKit).ToList();
            Assert.Equal(openStamp.Props.Count, roofedNonCeiling.Count);
            Assert.Equal(openStamp.Props, roofedNonCeiling);
        }

        [Fact]
        public void Roofed_BothSinks_AgreeOnCeilingCount()
        {
            DungeonLayout layout = RoofedMultiFloorLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(3f, -2f, 1f, MathF.PI / 5f);
            var target = new MapDocument();

            DungeonMapDocEmitter.Emit(layout, kit, plot, target);
            DungeonStampResult stamp = DungeonStamp.Build(layout, kit, plot);

            int emitterCeilings = target.Placements.Count(p => p.Kind == CeilingKit);
            int stampCeilings = stamp.Props.Count(p => p.KitId == CeilingKit);

            Assert.True(emitterCeilings > 0, "roofed multi-floor layout must place ceilings");
            Assert.Equal(emitterCeilings, stampCeilings);
            Assert.Equal(ExpectedCeilingCells(layout), emitterCeilings);
        }

        [Fact]
        public void Roofed_CeilingPlacements_RoundTripThroughMapDocumentFile()
        {
            DungeonLayout layout = DungeonGenerator.Generate(SingleFloor(DungeonCeilingMode.Roofed), 9UL);
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(5f, -5f, 2f, 0f);
            var target = new MapDocument
            {
                Id = "roofed-zone",
                DisplayName = "Roofed Zone",
                Bounds = new MapBounds { MinX = -1f, MinZ = -1f, MaxX = 1f, MaxZ = 1f },
            };

            DungeonMapDocEmitter.Emit(layout, kit, plot, target);

            string json = MapDocumentFile.SaveText(target);
            MapDocument loaded = MapDocumentFile.LoadText(json);

            var outCeilings = target.Placements.Where(p => p.Kind == CeilingKit).ToList();
            var inCeilings = loaded.Placements.Where(p => p.Kind == CeilingKit).ToList();

            Assert.NotEmpty(outCeilings);
            Assert.Equal(outCeilings.Count, inCeilings.Count);

            // Every emitted ceiling placement sits at floorY + ceiling height, and that survives the save/load.
            float expectedY = plot.BaseY + layout.CeilingHeightMeters; // floor 0 -> BaseY
            Assert.All(outCeilings, p =>
            {
                Assert.NotNull(p.Y);
                Assert.Equal(expectedY, p.Y!.Value, 3);
            });

            MapPlacement firstOut = outCeilings[0];
            MapPlacement firstIn = inCeilings[0];
            Assert.Equal(firstOut.X, firstIn.X, 3);
            Assert.Equal(firstOut.Y!.Value, firstIn.Y!.Value, 3);
            Assert.Equal(firstOut.Z, firstIn.Z, 3);
        }

        // Mirror of PieceMapper.HasCeiling (internal) expressed against the public raster, so the tests read
        // the same rule the production code applies without reaching into internals.
        static bool PieceMapperHasCeiling(DungeonLayout layout, int x, int z, int f)
        {
            if (layout.CeilingMode != DungeonCeilingMode.Roofed)
            {
                return false;
            }

            DungeonCellKind cell = layout.GetCell(x, z, f);
            if (!DungeonLayout.IsWalkable(cell) && cell != DungeonCellKind.StairVoid)
            {
                return false;
            }

            DungeonCellKind above = layout.GetCell(x, z, f + 1);
            return !DungeonLayout.IsWalkable(above) && above != DungeonCellKind.StairVoid;
        }
    }
}
