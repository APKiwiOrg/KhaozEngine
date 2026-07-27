using System;

namespace KhaozEngine.Terrain
{
    /// <summary>The player-facing render-distance tiers a <see cref="RenderDistanceProfile"/> is chosen by. Ordered
    /// from cheapest to furthest, so a settings UI can present them in declaration order and a weak machine can dial
    /// down one step at a time.</summary>
    public enum RenderDistanceTier
    {
        /// <summary>Shortest horizon, cheapest to stream and draw.</summary>
        Near,
        /// <summary>A middle horizon between <see cref="Near"/> and <see cref="Far"/>.</summary>
        Medium,
        /// <summary>The default: a full horizon at the engine's stock 500 m far clip.</summary>
        Far,
        /// <summary>The furthest horizon, for machines with headroom to spare.</summary>
        Ultra,
    }

    /// <summary>The concrete radii one <see cref="RenderDistanceTier"/> maps to: the single place a view distance is
    /// defined, so the streamer setup, the camera setup and the headless tests all read the same numbers. GPU-free
    /// (it names no renderer type), so it is headless-testable and usable from a server or a tool.
    ///
    /// <para><b>Why one type instead of four knobs.</b> Render distance is a COHERENT SET, not four independent
    /// sliders. The streamed-terrain far field (<see cref="DecorRadiusChunks"/>), the prop cull radius
    /// (<see cref="PropDrawRadius"/>), the camera far clip (<see cref="FarClip"/>) and the camera-following ocean
    /// plane extent (<see cref="OceanHalfExtent"/>) only read as one horizon when they are chosen together. Raise the
    /// far clip alone and the frustum reaches past where terrain is resident, so the ground ends in a black void with
    /// props still drawing over it. Raise the ocean extent alone and its rim sits inside the frustum as a visible wall
    /// of water, or worse, floats over unstreamed nothing. The ordering that makes a horizon read as one piece is:
    /// the ocean rim sits OUTSIDE the frustum (past <see cref="FarClip"/>) but INSIDE terrain residency (at or within
    /// <see cref="DecorRadiusMeters"/>), and props cull at or before the far clip so nothing is drawn that the clip
    /// would slice. <see cref="Validate"/> is those rules in code. Shipping tiers rather than sliders is what stops a
    /// caller from moving one of them and breaking the set.</para>
    ///
    /// <para><b>The gameplay ring is a separate concern and does not scale with view distance.</b>
    /// <see cref="GameplayLoadRadiusChunks"/> is the SIMULATION radius: chunks inside it are full
    /// <see cref="ChunkRing.Gameplay"/> chunks (scatter, prop colliders, optional terrain collision). Chunks between
    /// it and <see cref="DecorRadiusChunks"/> are render-only <see cref="ChunkRing.Decor"/> chunks (a coarse terrain
    /// mesh, no scatter and no physics), so buying more horizon costs mesh rather than simulation. That is why every
    /// built-in tier keeps the same gameplay ring and scales only the view radii above it.</para>
    ///
    /// <para><b>Units.</b> The three streamer radii are in CHUNK units (one chunk is <see cref="ChunkMeters"/> m,
    /// read from <see cref="TerrainChunkRegion.DefaultSize"/> rather than copied). <see cref="PropDrawRadius"/>,
    /// <see cref="FarClip"/> and <see cref="OceanHalfExtent"/> are in metres.</para>
    ///
    /// <para><b>Validation is at the point of use, not in the constructor.</b> A record struct always has a
    /// zero-initialised <c>default</c> form that no constructor can intercept, so throwing from the primary
    /// constructor would only give the illusion of an always-valid value. Callers that accept a hand-rolled profile
    /// call <see cref="Validate"/> once, at startup, so a bad set fails loudly instead of rendering wrong.</para>
    /// </summary>
    /// <param name="GameplayLoadRadiusChunks">Simulation radius in chunks: everything within it streams as a full
    /// gameplay chunk (scatter and colliders). Fixed across the built-in tiers.</param>
    /// <param name="DecorRadiusChunks">Outer terrain residency in chunks. Chunks between the gameplay radius and this
    /// one stream render-only, which is what buys a far horizon cheaply.</param>
    /// <param name="UnloadRadiusChunks">Hysteresis unload boundary in chunks. Must exceed both radii above so a
    /// camera oscillating across the outer edge does not churn chunks in and out.</param>
    /// <param name="PropDrawRadius">Horizontal cull radius (m) for streamed scatter props.</param>
    /// <param name="FarClip">Camera far clip plane (m).</param>
    /// <param name="OceanHalfExtent">Half-extent (m) of the camera-following ocean plane, i.e. how far its rim sits
    /// from the camera on each axis.</param>
    public readonly record struct RenderDistanceProfile(
        int GameplayLoadRadiusChunks,
        int DecorRadiusChunks,
        int UnloadRadiusChunks,
        float PropDrawRadius,
        float FarClip,
        float OceanHalfExtent)
    {
        /// <summary>World metres per streamer chunk. Reads <see cref="TerrainChunkRegion.DefaultSize"/> directly, so
        /// the chunk-unit radii and the metre radii can never drift apart on a chunk-size change.</summary>
        public const float ChunkMeters = TerrainChunkRegion.DefaultSize;

        /// <summary>The far-field terrain residency this profile guarantees, in metres
        /// (<see cref="DecorRadiusChunks"/> * <see cref="ChunkMeters"/>). <see cref="OceanHalfExtent"/> stays at or
        /// under this, so the sea is always drawn over resident, occlusion-capable terrain rather than over a
        /// void.</summary>
        public float DecorRadiusMeters => DecorRadiusChunks * ChunkMeters;

        /// <summary>The profile for a tier. The gameplay ring is 4 chunks (~240 m) at every tier: view distance and
        /// simulation footprint are separate concerns, so only the decor, prop, clip and ocean radii scale. Each tier
        /// satisfies every <see cref="Validate"/> invariant, which the tests assert tier by tier. Any unrecognised
        /// value maps to <see cref="RenderDistanceTier.Far"/>, the default.</summary>
        public static RenderDistanceProfile For(RenderDistanceTier tier) => tier switch
        {
            RenderDistanceTier.Near => new(GameplayLoadRadiusChunks: 4, DecorRadiusChunks: 7, UnloadRadiusChunks: 9,
                PropDrawRadius: 300f, FarClip: 300f, OceanHalfExtent: 360f),
            RenderDistanceTier.Medium => new(GameplayLoadRadiusChunks: 4, DecorRadiusChunks: 9, UnloadRadiusChunks: 11,
                PropDrawRadius: 400f, FarClip: 400f, OceanHalfExtent: 480f),
            RenderDistanceTier.Ultra => new(GameplayLoadRadiusChunks: 4, DecorRadiusChunks: 15, UnloadRadiusChunks: 17,
                PropDrawRadius: 700f, FarClip: 700f, OceanHalfExtent: 800f),
            // Far, and the fallback: 660 m of terrain residency behind the engine's stock 500 m far clip.
            _ => new(GameplayLoadRadiusChunks: 4, DecorRadiusChunks: 11, UnloadRadiusChunks: 13,
                PropDrawRadius: 500f, FarClip: 500f, OceanHalfExtent: 600f),
        };

        /// <summary>The default profile, <see cref="RenderDistanceTier.Far"/>: a horizon sized to the stock 500 m
        /// camera far clip every engine camera already ships with.</summary>
        public static RenderDistanceProfile Default => For(RenderDistanceTier.Far);

        /// <summary>This profile scaled up by <paramref name="multiplier"/> as one coherent set, for a settings UI
        /// that offers Base/2x/4x rather than the fixed <see cref="RenderDistanceTier"/> steps. <see cref="FarClip"/>,
        /// <see cref="OceanHalfExtent"/> and <see cref="PropDrawRadius"/> scale linearly. <see cref="DecorRadiusChunks"/>
        /// and <see cref="UnloadRadiusChunks"/> scale in whole chunks, rounded UP: a linear scale of a chunk count is
        /// usually fractional, and rounding up rather than down only ever grows residency, so it cannot break the
        /// rim rules in <see cref="Validate"/> (a larger <see cref="DecorRadiusMeters"/> still covers the scaled
        /// <see cref="OceanHalfExtent"/>). The unload radius is then clamped above the scaled decor radius and the
        /// unchanged <see cref="GameplayLoadRadiusChunks"/> in case rounding alone ever lands exactly on the boundary,
        /// so the hysteresis invariant holds regardless. <see cref="GameplayLoadRadiusChunks"/> itself is unchanged:
        /// the gameplay ring is a simulation footprint, not a view distance (see the type summary). <c>Scaled(1f)</c>
        /// is the identity, and every built-in <see cref="For"/> tier passes <see cref="Validate"/> after scaling by
        /// any factor at or above 1.</summary>
        /// <param name="multiplier">Scale factor, 1 or greater. Use a smaller <see cref="RenderDistanceTier"/> via
        /// <see cref="For"/> to scale down instead.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="multiplier"/> is less than 1, NaN, or
        /// positive infinity.</exception>
        public RenderDistanceProfile Scaled(float multiplier)
        {
            // NaN fails every comparison, so `!(multiplier >= 1f)` catches NaN as well as anything below 1.
            if (!(multiplier >= 1f) || float.IsPositiveInfinity(multiplier))
                throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier,
                    "multiplier must be finite and at least 1 (use a smaller For(...) tier to scale down).");

            int decorRadiusChunks = (int)MathF.Ceiling(DecorRadiusChunks * multiplier);
            int unloadRadiusChunks = (int)MathF.Ceiling(UnloadRadiusChunks * multiplier);
            int outer = GameplayLoadRadiusChunks > decorRadiusChunks ? GameplayLoadRadiusChunks : decorRadiusChunks;
            if (unloadRadiusChunks <= outer)
                unloadRadiusChunks = outer + 1;

            return this with
            {
                DecorRadiusChunks = decorRadiusChunks,
                UnloadRadiusChunks = unloadRadiusChunks,
                PropDrawRadius = PropDrawRadius * multiplier,
                FarClip = FarClip * multiplier,
                OceanHalfExtent = OceanHalfExtent * multiplier,
            };
        }

        /// <summary>This profile's radii layered onto <see cref="StreamerConfig.Default"/>. Use the
        /// <see cref="ToStreamerConfig(StreamerConfig)"/> overload when the caller has its own tuned config (a
        /// non-default chunk size, LOD table or per-frame apply budget) to keep.</summary>
        public StreamerConfig ToStreamerConfig() => ToStreamerConfig(StreamerConfig.Default);

        /// <summary>This profile's three radii layered onto <paramref name="baseConfig"/>, leaving every other field
        /// of it (chunk size, LOD table, applies-per-frame, async flag) untouched. Note that
        /// <see cref="ChunkMeters"/> is <see cref="TerrainChunkRegion.DefaultSize"/>, so the metre radii on this
        /// profile only line up with the chunk radii while <paramref name="baseConfig"/> keeps that chunk size: a
        /// config with a custom <see cref="StreamerConfig.ChunkSize"/> gets the radii it asked for, but its own
        /// coherence is then the caller's to check.</summary>
        public StreamerConfig ToStreamerConfig(StreamerConfig baseConfig) => baseConfig with
        {
            LoadRadius = GameplayLoadRadiusChunks,
            DecorRadius = DecorRadiusChunks,
            UnloadRadius = UnloadRadiusChunks,
        };

        /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> unless this profile is a coherent set: every
        /// radius positive, the unload band outside both load radii, the ocean rim past the far clip but inside the
        /// terrain far field, and the prop cull at or inside the far clip. Call it once where a caller-supplied
        /// profile is consumed (editor or game startup) so a hand-rolled set fails loudly rather than rendering a
        /// void horizon. The built-in <see cref="For"/> tiers all pass.</summary>
        /// <param name="paramName">Reported as the offending parameter, for a profile that arrived as an argument or
        /// an options field. Defaults to the type name.</param>
        public void Validate(string? paramName = null)
        {
            string name = paramName ?? nameof(RenderDistanceProfile);

            if (GameplayLoadRadiusChunks <= 0)
                throw new ArgumentOutOfRangeException(name, GameplayLoadRadiusChunks,
                    "GameplayLoadRadiusChunks must be positive (a render-distance profile always streams a gameplay ring).");
            if (DecorRadiusChunks <= 0)
                throw new ArgumentOutOfRangeException(name, DecorRadiusChunks,
                    "DecorRadiusChunks must be positive (use the gameplay radius as the decor radius for no far field).");
            if (PropDrawRadius <= 0f)
                throw new ArgumentOutOfRangeException(name, PropDrawRadius, "PropDrawRadius must be positive.");
            if (FarClip <= 0f)
                throw new ArgumentOutOfRangeException(name, FarClip, "FarClip must be positive.");
            if (OceanHalfExtent <= 0f)
                throw new ArgumentOutOfRangeException(name, OceanHalfExtent, "OceanHalfExtent must be positive.");

            // Hysteresis: the unload boundary sits outside whichever load radius reaches furthest, or a camera
            // hovering on the outer edge unloads and reloads the same chunks every frame.
            int outer = GameplayLoadRadiusChunks > DecorRadiusChunks ? GameplayLoadRadiusChunks : DecorRadiusChunks;
            if (UnloadRadiusChunks <= outer)
                throw new ArgumentOutOfRangeException(name, UnloadRadiusChunks,
                    $"UnloadRadiusChunks must exceed the outer load radius ({outer}) so the outer edge does not churn.");

            // The rim rules, in the order they fail visibly. Ocean inside the frustum reads as a wall of water.
            if (OceanHalfExtent <= FarClip)
                throw new ArgumentOutOfRangeException(name, OceanHalfExtent,
                    $"OceanHalfExtent must exceed FarClip ({FarClip}) so the ocean rim clips out instead of being visible.");
            // Ocean past terrain residency reads as sea floating over a void, the bug this type exists to prevent.
            if (DecorRadiusMeters < OceanHalfExtent)
                throw new ArgumentOutOfRangeException(name, OceanHalfExtent,
                    $"OceanHalfExtent must be within the terrain far field ({DecorRadiusMeters} m) so the sea is never drawn over unstreamed ground.");
            // Props culled past the far clip are simply wasted: the clip slices them anyway.
            if (PropDrawRadius > FarClip)
                throw new ArgumentOutOfRangeException(name, PropDrawRadius,
                    $"PropDrawRadius must not exceed FarClip ({FarClip}); props past the clip are never visible.");
        }
    }
}
