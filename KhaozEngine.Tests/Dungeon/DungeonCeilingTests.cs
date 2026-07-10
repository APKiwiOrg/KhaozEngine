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

        // The independent oracle for HasCeiling, derived only from the public raster: a walkable cell whose
        // same-XZ cell one floor up is neither walkable nor StairVoid (the deliberately-open stair headroom
        // cutout, which must stay uncapped even though it is not itself walkable; out-of-range floors read
        // Empty, hence not walkable and not StairVoid either).
        static int ExpectedCeilingCells(DungeonLayout layout)
        {
            int count = 0;
            for (int f = 0; f < layout.Floors; f++)
            {
                for (int z = 0; z < layout.Depth; z++)
                {
                    for (int x = 0; x < layout.Width; x++)
                    {
                        if (!DungeonLayout.IsWalkable(layout.GetCell(x, z, f)))
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
            DungeonTile stairLower = stair.Path[0]; // StairLower: StairVoid above -> open (the ramp's headroom)
            DungeonTile stairUpper = stair.Path[1]; // StairUpper: walkable StairTop above -> open

            (float lx, float ly, float lz) = plot.TileCenter(stairLower, cell, floorHeight);
            (float ux, float uy, float uz) = plot.TileCenter(stairUpper, cell, floorHeight);

            // The emergence cell is left open at ITS floor's level (the StairTop landing directly above it is
            // the roof). A ceiling one floor up over that same landing is expected and allowed, so the check
            // pins StairUpper's own Y, not just the XZ column.
            Assert.DoesNotContain(ceilingProps, p =>
                MathF.Abs(p.X - ux) < 1e-3f && MathF.Abs(p.Z - uz) < 1e-3f
                && MathF.Abs(p.Y - (uy + layout.CeilingHeightMeters)) < 1e-3f);

            // The stair base must ALSO stay open: the StairVoid cutout above StairLower is the ramp's own
            // headroom, not solid floor-above space, so it must not be roofed either (the whole shaft, base to
            // emergence, stays walkable through).
            Assert.DoesNotContain(ceilingProps, p =>
                MathF.Abs(p.X - lx) < 1e-3f && MathF.Abs(p.Z - lz) < 1e-3f
                && MathF.Abs(p.Y - (ly + layout.CeilingHeightMeters)) < 1e-3f);
        }

        [Fact]
        public void Roofed_StairwellShaft_NotCoveredByFloorSlabOrCeilingSlab()
        {
            // A walker climbing the ramp from StairLower must find the whole shaft clear: no floor slab or
            // ceiling slab over the StairVoid headroom cutout, no ceiling capping the entry (StairLower) or the
            // emergence (StairUpper), so the only shaft geometry is the pitched ramp itself. StairTop, the
            // landing the walker steps onto once they reach the upper floor, is deliberately NOT included here:
            // it is a normal walkable cell (structurally the stair's "doorframe"), and it DOES carry its own
            // floor slab - without it the walker would fall straight through onto the floor below instead of
            // stepping onto solid ground.
            DungeonLayout layout = RoofedMultiFloorLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);

            DungeonStampResult stamp = DungeonStamp.Build(layout, kit, plot);

            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;

            DungeonEdge stair = layout.Edges.First(e => e.Kind == DungeonEdgeKind.Stair);
            DungeonTile stairLower = stair.Path[0];
            DungeonTile stairUpper = stair.Path[1];
            DungeonTile stairTop = stair.Path[2];
            var stairVoid = new DungeonTile(stairLower.X, stairLower.Z, stairLower.Floor + 1);

            var slabBoxes = stamp.Statics
                .Where(s => s.Shape is BoxShape b && MathF.Abs(b.HalfExtents.Y - 0.1f) < 1e-3f
                    && Vector3.Transform(Vector3.UnitY, s.Pose.Orientation).Y > 0.99f)
                .Select(s => ((BoxShape)s.Shape, s.Pose))
                .ToList();
            Assert.NotEmpty(slabBoxes);

            (float vx, float vy, float vz) = plot.TileCenter(stairVoid, cell, floorHeight);
            (float lx, float ly, float lz) = plot.TileCenter(stairLower, cell, floorHeight);
            (float ux, float uy, float uz) = plot.TileCenter(stairUpper, cell, floorHeight);
            (float tx, float ty, float tz) = plot.TileCenter(stairTop, cell, floorHeight);

            // No flat (world-up) slab - floor OR ceiling - covers the StairVoid column at all: sample its own
            // floor level (top of a hypothetical floor slab) and its own ceiling level (bottom of a hypothetical
            // ceiling slab); neither point may fall inside any slab box.
            var stairVoidFloorPoint = new Vector3(vx, vy - 0.05f, vz);
            var stairVoidCeilingPoint = new Vector3(vx, vy + layout.CeilingHeightMeters + 0.1f, vz);
            Assert.DoesNotContain(slabBoxes, sb => ContainsPoint(sb.Item1, sb.Item2, stairVoidFloorPoint));
            Assert.DoesNotContain(slabBoxes, sb => ContainsPoint(sb.Item1, sb.Item2, stairVoidCeilingPoint));

            // No ceiling caps the shaft at either end: StairLower's own ceiling level (the bug this test guards
            // against - the StairVoid headroom above it used to be roofed) and StairUpper's own ceiling level
            // (the emergence cell, already-correct behaviour, pinned here too so a regression trips this test).
            var stairLowerCeilingPoint = new Vector3(lx, ly + layout.CeilingHeightMeters + 0.1f, lz);
            var stairUpperCeilingPoint = new Vector3(ux, uy + layout.CeilingHeightMeters + 0.1f, uz);
            Assert.DoesNotContain(slabBoxes, sb => ContainsPoint(sb.Item1, sb.Item2, stairLowerCeilingPoint));
            Assert.DoesNotContain(slabBoxes, sb => ContainsPoint(sb.Item1, sb.Item2, stairUpperCeilingPoint));

            // StairTop DOES carry its own floor slab (the landing a walker steps onto, sampled just below its
            // floor level) - a sanity/regression guard that opening the shaft did not also remove the solid
            // ground the walker needs at the top.
            var stairTopFloorPoint = new Vector3(tx, ty - 0.05f, tz);
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
            if (layout.CeilingMode != DungeonCeilingMode.Roofed || !DungeonLayout.IsWalkable(layout.GetCell(x, z, f)))
            {
                return false;
            }

            DungeonCellKind above = layout.GetCell(x, z, f + 1);
            return !DungeonLayout.IsWalkable(above) && above != DungeonCellKind.StairVoid;
        }
    }
}
