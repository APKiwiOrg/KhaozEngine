using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>Covers <see cref="MapResidencyConfig"/> and its wiring-time check. The arithmetic corrections
    /// pinned here each fix a first-draft rule that certified a config with a hole in it, so each gets its own
    /// test: the reach is measured to the streamer's UNLOAD radius rather than its outer load radius, the sculpt
    /// inset is SUBTRACTED, and the worst-case chunk reach is the true off-axis maximum rather than the axial
    /// one. A later simplification that undoes any of the three fails here.</summary>
    public class MapResidencyConfigTests
    {
        const float Tile = 512f;
        const float SculptCell = 2f;      // Ruinborne's, a 64 m sculpt span

        static StreamerConfig Streamer(int load, int unload, int decor = 0, float chunkSize = 60f) =>
            new(LoadRadius: load, UnloadRadius: unload, MaxLoadsPerFrame: 3, ChunkSize: chunkSize, Async: true, DecorRadius: decor);

        [Fact]
        public void Default_IsTwoThreeTwoWithNoDecorRing()
        {
            MapResidencyConfig d = MapResidencyConfig.Default;

            Assert.Equal(2, d.LoadRadius);
            Assert.Equal(3, d.UnloadRadius);
            Assert.Equal(2, d.MaxLoadsPerUpdate);
            Assert.Equal(0, d.DecorRadius);
            Assert.True(d.Async);
            Assert.Equal(2, d.OuterRadius);                              // no decor ring: outer == load
            Assert.Equal(3, (d with { DecorRadius = 3 }).OuterRadius);   // decor widens the loaded ring
            Assert.False(d.Synchronous().Async);
            Assert.Equal(d.LoadRadius, d.Synchronous().LoadRadius);      // and changes nothing else
        }

        [Fact]
        public void ValidateAgainst_AcceptsTheDefaultPairing()
        {
            // The default residency covers the default streamer with 527 m of margin on the data rule, and still
            // covers a wide decor ring (UnloadRadius 10, reach 684 m) with 276 m to spare. That second row is the
            // entire argument for LoadRadius 2 over 1.
            Assert.Empty(MapResidencyConfig.Default.ValidateAgainst(Streamer(4, 6), Tile, SculptCell));
            Assert.Empty(MapResidencyConfig.Default.ValidateAgainst(Streamer(4, 10, decor: 8), Tile, SculptCell));
        }

        [Fact]
        public void ValidateAgainst_RejectsAStreamerRingWiderThanResidency()
        {
            var narrow = new MapResidencyConfig(LoadRadius: 1, UnloadRadius: 2, MaxLoadsPerUpdate: 2);

            // LoadRadius 1 passes the default streamer by 15 m out of a 512 m tile...
            Assert.Empty(narrow.ValidateAgainst(Streamer(4, 6), Tile, SculptCell));

            // ...and fails the moment a game turns on a decor ring, which is what a default has to survive.
            IReadOnlyList<string> errors = narrow.ValidateAgainst(Streamer(4, 10, decor: 8), Tile, SculptCell);
            string only = Assert.Single(errors);
            Assert.Contains("data rule", only);
        }

        [Fact]
        public void ValidateAgainst_UsesUnloadRadiusNotOuterRadius()
        {
            // A streamer with a tiny load ring and a huge hysteresis band. Chunks persist to UnloadRadius and
            // Invalidate rebuilds every LOADED chunk a rect touches, which the sculpt handoff calls on every tile
            // arrival, so a chunk out at UnloadRadius can be rebuilt at any moment and must find its data
            // resident. A check measuring the outer LOAD radius instead sees 134 m of need and reports nothing.
            var config = new MapResidencyConfig(LoadRadius: 1, UnloadRadius: 2, MaxLoadsPerUpdate: 2);

            IReadOnlyList<string> errors = config.ValidateAgainst(Streamer(1, 9), Tile, SculptCell);

            string only = Assert.Single(errors);
            Assert.Contains("data rule", only);
            Assert.Contains("UnloadRadius 9", only);
        }

        [Fact]
        public void ValidateAgainst_SubtractsTheSculptSpan()
        {
            // Identical everything except the sculpt cell size. A resident tile's low-X and low-Z edges are
            // covered by sculpt owned by the neighbour on that side, so the coverage a tile actually guarantees
            // is one span short. Hysteresis does not pay for this: it is an unload-side allowance and the
            // shortfall is on the load side.
            var config = new MapResidencyConfig(LoadRadius: 1, UnloadRadius: 2, MaxLoadsPerUpdate: 2);

            Assert.Empty(config.ValidateAgainst(Streamer(4, 6), Tile, sculptCellSize: 2f));   // 512 - 64 = 448

            IReadOnlyList<string> errors = config.ValidateAgainst(Streamer(4, 6), Tile, sculptCellSize: 4f);
            string only = Assert.Single(errors);                                              // 512 - 128 = 384
            Assert.Contains("data rule", only);
        }

        [Fact]
        public void ValidateAgainst_MaxChunkReachIsNotAxial()
        {
            // At UnloadRadius 6 the worst chunk offset is (5, 3), reaching sqrt(52) = 7.211 chunks, NOT the
            // axial (6, 0)'s sqrt(50) = 7.071. This tile size sits between the two readings: 492 - 64 = 428 m of
            // coverage, against 424.3 m axial and 432.7 m true. An axial-only check certifies it. It has a hole.
            var config = new MapResidencyConfig(LoadRadius: 1, UnloadRadius: 2, MaxLoadsPerUpdate: 2);

            IReadOnlyList<string> errors = config.ValidateAgainst(Streamer(4, 6), tileSize: 492f, sculptCellSize: SculptCell);

            string only = Assert.Single(errors);
            Assert.Contains("data rule", only);
        }

        [Fact]
        public void ValidateAgainst_ColliderRuleIsSeparateFromTheDataRule()
        {
            // A decor ring wide enough to satisfy the data rule while the GAMEPLAY ring is far too narrow: every
            // chunk finds its data, but a gameplay chunk sits over a Decor document tile whose colliders a
            // consumer has already shed. That is the case the second rule exists for and the first cannot see.
            var config = new MapResidencyConfig(LoadRadius: 1, UnloadRadius: 4, MaxLoadsPerUpdate: 2, DecorRadius: 3);

            IReadOnlyList<string> errors = config.ValidateAgainst(Streamer(4, 6), tileSize: 200f, sculptCellSize: SculptCell);

            string only = Assert.Single(errors);
            Assert.Contains("collider rule", only);
        }

        [Fact]
        public void ValidateAgainst_ReportsDegenerateWiring()
        {
            var band = new MapResidencyConfig(LoadRadius: 2, UnloadRadius: 2, MaxLoadsPerUpdate: 2);
            Assert.Contains(band.ValidateAgainst(Streamer(4, 6), Tile, SculptCell), e => e.Contains("UnloadRadius"));

            var budget = new MapResidencyConfig(LoadRadius: 2, UnloadRadius: 3, MaxLoadsPerUpdate: 0);
            Assert.Contains(budget.ValidateAgainst(Streamer(4, 6), Tile, SculptCell), e => e.Contains("MaxLoadsPerUpdate"));

            Assert.Contains(MapResidencyConfig.Default.ValidateAgainst(Streamer(4, 6), 0f, SculptCell),
                e => e.Contains("tileSize"));
            Assert.Contains(MapResidencyConfig.Default.ValidateAgainst(Streamer(4, 6), Tile, -1f),
                e => e.Contains("sculptCellSize"));
        }

        [Fact]
        public void ValidateAgainst_ErrorsNameBothRulesWhenBothFail()
        {
            var config = new MapResidencyConfig(LoadRadius: 1, UnloadRadius: 2, MaxLoadsPerUpdate: 2);

            IReadOnlyList<string> errors = config.ValidateAgainst(Streamer(6, 9), tileSize: 100f, sculptCellSize: SculptCell);

            Assert.Equal(2, errors.Count);
            Assert.Contains(errors, e => e.Contains("data rule"));
            Assert.Contains(errors, e => e.Contains("collider rule"));
            Assert.All(errors, e => Assert.False(string.IsNullOrWhiteSpace(e)));
        }

        [Fact]
        public void ValidateAgainst_CoversEveryRadiusChebyshevPromises()
        {
            // Chebyshev's whole selling point: LoadRadius * tileSize of guaranteed coverage for EVERY radius,
            // with no special cases. So a config that passes at one radius keeps passing as the radius grows,
            // monotonically, which is what makes the rule a single line of arithmetic rather than a table.
            bool[] pass = Enumerable.Range(1, 6)
                .Select(r => new MapResidencyConfig(r, r + 1, 2).ValidateAgainst(Streamer(4, 6), Tile, SculptCell).Count == 0)
                .ToArray();

            Assert.All(pass, Assert.True);
        }
    }
}
