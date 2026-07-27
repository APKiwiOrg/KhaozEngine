using System;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Covers <see cref="RenderDistanceProfile"/>: the tier table, the chunk-to-metre conversion reading the
    /// real <see cref="TerrainChunkRegion.DefaultSize"/>, the <see cref="StreamerConfig"/> projection, and the
    /// coherence invariants <see cref="RenderDistanceProfile.Validate"/> enforces. The invariants are the whole point
    /// of the type (four radii that only read as one horizon when chosen together), so they are asserted tier by tier
    /// AND broken one at a time.</summary>
    public class RenderDistanceProfileTests
    {
        public static TheoryData<RenderDistanceTier> AllTiers => new()
        {
            RenderDistanceTier.Near,
            RenderDistanceTier.Medium,
            RenderDistanceTier.Far,
            RenderDistanceTier.Ultra,
        };

        // ---- the tier table ---------------------------------------------------------------------------------

        [Fact]
        public void Default_IsTheFarTier()
        {
            Assert.Equal(RenderDistanceProfile.For(RenderDistanceTier.Far), RenderDistanceProfile.Default);
        }

        [Theory]
        [InlineData(RenderDistanceTier.Near, 4, 7, 9, 300f, 300f, 360f)]
        [InlineData(RenderDistanceTier.Medium, 4, 9, 11, 400f, 400f, 480f)]
        [InlineData(RenderDistanceTier.Far, 4, 11, 13, 500f, 500f, 600f)]
        [InlineData(RenderDistanceTier.Ultra, 4, 15, 17, 700f, 700f, 800f)]
        public void For_MapsEachTierToItsExactRadii(RenderDistanceTier tier, int gameplay, int decor, int unload,
            float prop, float farClip, float ocean)
        {
            RenderDistanceProfile p = RenderDistanceProfile.For(tier);

            Assert.Equal(gameplay, p.GameplayLoadRadiusChunks);
            Assert.Equal(decor, p.DecorRadiusChunks);
            Assert.Equal(unload, p.UnloadRadiusChunks);
            Assert.Equal(prop, p.PropDrawRadius);
            Assert.Equal(farClip, p.FarClip);
            Assert.Equal(ocean, p.OceanHalfExtent);
        }

        [Fact]
        public void For_UnknownTier_FallsBackToTheDefault()
        {
            // A value cast in from outside the declared set (a stale settings file, a new tier added upstream) must
            // land on the default rather than on a zeroed profile that would then fail Validate.
            Assert.Equal(RenderDistanceProfile.Default, RenderDistanceProfile.For((RenderDistanceTier)999));
        }

        [Fact]
        public void GameplayRing_IsFixedAcrossTiers()
        {
            // View distance and simulation footprint are separate concerns: buying more horizon must not silently
            // enlarge the ring that carries scatter and colliders.
            foreach (RenderDistanceTier tier in Enum.GetValues<RenderDistanceTier>())
                Assert.Equal(RenderDistanceProfile.Default.GameplayLoadRadiusChunks,
                    RenderDistanceProfile.For(tier).GameplayLoadRadiusChunks);
        }

        [Fact]
        public void Tiers_IncreaseMonotonicallyInReach()
        {
            // Near -> Medium -> Far -> Ultra must be strictly further on every view radius, or a player who steps
            // the setting up gets no more horizon (or less) than they had.
            RenderDistanceTier[] ordered =
            {
                RenderDistanceTier.Near, RenderDistanceTier.Medium, RenderDistanceTier.Far, RenderDistanceTier.Ultra,
            };
            for (int i = 1; i < ordered.Length; i++)
            {
                RenderDistanceProfile lower = RenderDistanceProfile.For(ordered[i - 1]);
                RenderDistanceProfile higher = RenderDistanceProfile.For(ordered[i]);
                string step = $"{ordered[i - 1]} -> {ordered[i]}";

                Assert.True(higher.DecorRadiusChunks > lower.DecorRadiusChunks, $"decor radius must grow: {step}");
                Assert.True(higher.UnloadRadiusChunks > lower.UnloadRadiusChunks, $"unload radius must grow: {step}");
                Assert.True(higher.PropDrawRadius > lower.PropDrawRadius, $"prop cull must grow: {step}");
                Assert.True(higher.FarClip > lower.FarClip, $"far clip must grow: {step}");
                Assert.True(higher.OceanHalfExtent > lower.OceanHalfExtent, $"ocean extent must grow: {step}");
            }
        }

        // ---- chunk-to-metre conversion ----------------------------------------------------------------------

        [Fact]
        public void ChunkMeters_IsTheRealChunkSize()
        {
            // The reason this type lives in KhaozEngine.Terrain rather than beside a game's settings: it reads the
            // engine's own chunk size instead of carrying a copied literal that can drift.
            Assert.Equal(TerrainChunkRegion.DefaultSize, RenderDistanceProfile.ChunkMeters);
        }

        [Theory]
        [MemberData(nameof(AllTiers))]
        public void DecorRadiusMeters_IsTheChunkRadiusTimesTheChunkSize(RenderDistanceTier tier)
        {
            RenderDistanceProfile p = RenderDistanceProfile.For(tier);
            Assert.Equal(p.DecorRadiusChunks * TerrainChunkRegion.DefaultSize, p.DecorRadiusMeters);
        }

        // ---- the StreamerConfig projection ------------------------------------------------------------------

        [Fact]
        public void ToStreamerConfig_MapsTheThreeRadiiOntoTheDefaultConfig()
        {
            RenderDistanceProfile p = RenderDistanceProfile.Default;

            StreamerConfig cfg = p.ToStreamerConfig();

            Assert.Equal(p.GameplayLoadRadiusChunks, cfg.LoadRadius);
            Assert.Equal(p.DecorRadiusChunks, cfg.DecorRadius);
            Assert.Equal(p.UnloadRadiusChunks, cfg.UnloadRadius);
            Assert.Equal(p.DecorRadiusChunks, cfg.OuterRadius);   // the decor ring is what the streamer loads out to
            // Everything the profile does not speak for stays at the default config's value.
            Assert.Equal(StreamerConfig.Default.ChunkSize, cfg.ChunkSize);
            Assert.Equal(StreamerConfig.Default.MaxLoadsPerFrame, cfg.MaxLoadsPerFrame);
            Assert.Equal(StreamerConfig.Default.Async, cfg.Async);
        }

        [Fact]
        public void ToStreamerConfig_PreservesTheBaseConfigsOtherFields()
        {
            // The overload exists so a caller with a tuned config (its own chunk size, LOD table, apply budget, or a
            // synchronous editor path) can layer the profile's radii on without losing that tuning.
            var lod = new TerrainLodConfig(new TerrainLodTier(16, 100f), new TerrainLodTier(4, float.PositiveInfinity));
            var tuned = new StreamerConfig(LoadRadius: 1, UnloadRadius: 2, MaxLoadsPerFrame: 17, ChunkSize: 32f,
                Async: false, DecorRadius: 0, LodConfig: lod);
            RenderDistanceProfile p = RenderDistanceProfile.For(RenderDistanceTier.Ultra);

            StreamerConfig cfg = p.ToStreamerConfig(tuned);

            Assert.Equal(15, cfg.DecorRadius);
            Assert.Equal(4, cfg.LoadRadius);
            Assert.Equal(17, cfg.UnloadRadius);
            Assert.Equal(17, cfg.MaxLoadsPerFrame);
            Assert.Equal(32f, cfg.ChunkSize);
            Assert.False(cfg.Async);
            Assert.Same(lod, cfg.LodConfig);
        }

        // ---- the coherence invariants -----------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(AllTiers))]
        public void EveryTier_HoldsEveryCoherenceInvariant(RenderDistanceTier tier)
        {
            RenderDistanceProfile p = RenderDistanceProfile.For(tier);

            p.Validate();   // the whole set, in one call

            // Spelled out as well, so a tier edit that breaks one rule names which rule it broke.
            Assert.True(p.DecorRadiusChunks > p.GameplayLoadRadiusChunks, "the decor ring must sit beyond the gameplay ring");
            Assert.True(p.UnloadRadiusChunks > p.DecorRadiusChunks, "the unload band must exceed the outer load radius");
            Assert.True(p.OceanHalfExtent > p.FarClip, "the ocean rim must clip out instead of being visible");
            Assert.True(p.DecorRadiusMeters >= p.OceanHalfExtent, "the terrain far field must cover the ocean extent");
            Assert.True(p.PropDrawRadius <= p.FarClip, "the prop cull must not reach past the far clip");
        }

        [Fact]
        public void Default_StructValue_IsConstructibleButInvalid()
        {
            // A record struct cannot stop `default` from existing, which is exactly why validation is at the point of
            // use rather than in the primary constructor. The zeroed value must construct, and must not validate.
            RenderDistanceProfile zero = default;

            Assert.Equal(0, zero.DecorRadiusChunks);
            Assert.Throws<ArgumentOutOfRangeException>(() => zero.Validate());
        }

        [Fact]
        public void Validate_RejectsANonPositiveGameplayRadius()
        {
            RenderDistanceProfile p = RenderDistanceProfile.Default with { GameplayLoadRadiusChunks = 0 };
            Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
        }

        [Fact]
        public void Validate_RejectsANonPositiveDecorRadius()
        {
            RenderDistanceProfile p = RenderDistanceProfile.Default with { DecorRadiusChunks = 0 };
            Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
        }

        [Fact]
        public void Validate_RejectsANonPositivePropDrawRadius()
        {
            RenderDistanceProfile p = RenderDistanceProfile.Default with { PropDrawRadius = 0f };
            Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
        }

        [Fact]
        public void Validate_RejectsANonPositiveFarClip()
        {
            // FarClip 0 also breaks the ocean-past-the-clip rule, but the positivity check is the one that fires
            // first, so the message names the actual defect.
            RenderDistanceProfile p = RenderDistanceProfile.Default with { FarClip = 0f };
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
            Assert.Contains("FarClip must be positive", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_RejectsANonPositiveOceanHalfExtent()
        {
            RenderDistanceProfile p = RenderDistanceProfile.Default with { OceanHalfExtent = 0f };
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
            Assert.Contains("OceanHalfExtent must be positive", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_RejectsAnUnloadRadiusInsideTheDecorRing()
        {
            // Equal is already wrong: the band needs at least one chunk of hysteresis or the outer edge churns.
            RenderDistanceProfile p = RenderDistanceProfile.Default with { UnloadRadiusChunks = 11 };
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
            Assert.Contains("UnloadRadiusChunks", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_RejectsAnUnloadRadiusInsideTheGameplayRing_WhenTheDecorRingIsSmaller()
        {
            // The unload check reads the LARGER of the two load radii, so an unusual profile whose gameplay ring is
            // the outer one is caught too.
            RenderDistanceProfile p = RenderDistanceProfile.Default with
            {
                GameplayLoadRadiusChunks = 14,
                DecorRadiusChunks = 11,
                UnloadRadiusChunks = 13,
            };
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
            Assert.Contains("UnloadRadiusChunks", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_RejectsAnOceanRimInsideTheFrustum()
        {
            // The rim would be drawn as a visible edge of water in shot.
            RenderDistanceProfile p = RenderDistanceProfile.Default with { OceanHalfExtent = 450f };
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
            Assert.Contains("OceanHalfExtent must exceed FarClip", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_RejectsAnOceanPastTheStreamedTerrain()
        {
            // Sea drawn over a void: the exact failure the coherent set exists to prevent. 700 m of ocean over a
            // 660 m far field (11 chunks) leaves a ring of water with nothing under it.
            RenderDistanceProfile p = RenderDistanceProfile.Default with { OceanHalfExtent = 700f };
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
            Assert.Contains("within the terrain far field", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_RejectsAPropCullPastTheFarClip()
        {
            RenderDistanceProfile p = RenderDistanceProfile.Default with { PropDrawRadius = 550f };
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
            Assert.Contains("PropDrawRadius must not exceed FarClip", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_NamesTheCallersParameter()
        {
            // So an editor or game head sees which option it got wrong, not just the type name.
            RenderDistanceProfile p = RenderDistanceProfile.Default with { OceanHalfExtent = 1f };
            ArgumentOutOfRangeException ex =
                Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate("RenderDistance"));
            Assert.Equal("RenderDistance", ex.ParamName);
        }
    }
}
