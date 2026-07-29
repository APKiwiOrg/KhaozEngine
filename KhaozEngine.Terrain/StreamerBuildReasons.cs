namespace KhaozEngine.Terrain
{
    /// <summary>Cumulative per-REASON chunk-build counters for one <see cref="TerrainStreamer"/>, read through
    /// <see cref="TerrainStreamer.BuildReasons"/>. Counters are life-of-streamer totals and are never reset (not
    /// even by <see cref="TerrainStreamer.UnloadAll"/>, exactly like
    /// <see cref="TerrainStreamer.FailedBuildCount"/>), so a per-second rate is the difference of two samples.
    /// <para><b>What this answers.</b> <c>Scene3DChunkSink.MergeStats</c> says HOW MUCH build work happened and how
    /// much of it was thrown away. It cannot say WHY any of it was asked for, and a client rebuilding chunks while
    /// its player stands still is exactly the case where that is the only question worth asking. These five counters
    /// split the streamer's own request sites, so one panel row (or one log line) separates a ring that is following
    /// a moving player from one being wiped by something upstream.</para>
    /// <para><b>Frame thread only.</b> Every increment happens on the thread that calls
    /// <c>Update</c> / <c>Invalidate</c> / <c>PrimeAround</c>, unlike the merge counters, whose merges run on the
    /// background build threads. So these are plain adds and a reader on another thread can see a torn pair.</para></summary>
    public readonly struct StreamerBuildReasons
    {
        /// <summary>Builds requested for a chunk with NO mesh yet: the ring scan found it missing. Priming a world
        /// counts one per chunk, and after that a fresh load means a chunk left the ring and came back (or the whole
        /// ring was dropped by <see cref="TerrainStreamer.UnloadAll"/>). Re-targeting an in-flight load whose tier or
        /// ring changed before it landed counts here too, because the chunk still has nothing on the GPU.</summary>
        public long FreshLoad { get; }

        /// <summary>Rebuilds of a LOADED chunk whose LOD tier changed (<c>TerrainLodConfig.PickLod</c> crossed a
        /// boundary, damped by <see cref="StreamerConfig.LodHysteresis"/>). A tier flip is metre-distance driven, so
        /// this is the counter that moves when the position fed to the streamer is drifting.</summary>
        public long TierChange { get; }

        /// <summary>Rebuilds of a LOADED chunk whose residency ring changed at the SAME tier (gameplay to decor or
        /// back, which adds or drops scatter and colliders). Ring membership is integer chunk distance from the ring
        /// anchor, so this only moves when the anchor does.</summary>
        public long RingChange { get; }

        /// <summary>Rebuilds issued by <see cref="TerrainStreamer.Invalidate(RectArea)"/> /
        /// <see cref="TerrainStreamer.Invalidate(ChunkCoord)"/>: the explicit "this ground just changed" call a
        /// document-residency or editor layer makes. One per LOADED chunk actually rebuilt (an invalidated rect that
        /// covers unloaded chunks counts nothing for them).</summary>
        public long Invalidate { get; }

        /// <summary>How many times the ring RE-CENTRED on a different chunk. Not a build count: it is the upstream
        /// cause of a burst of the three ring-scan reasons above, and the one number that separates "the disk moved"
        /// from "something invalidated or wiped the ring". A streamer's first anchor is not a re-centre (a fresh
        /// streamer, and the undamped re-anchor after <see cref="TerrainStreamer.UnloadAll"/>, both land where the
        /// player already is), so this stays 0 through priming.</summary>
        public long AnchorRecentre { get; }

        /// <summary>Every build this streamer asked for, whatever the reason:
        /// <see cref="FreshLoad"/> + <see cref="TierChange"/> + <see cref="RingChange"/> + <see cref="Invalidate"/>.
        /// <see cref="AnchorRecentre"/> is deliberately not in the sum (it counts causes, not builds).</summary>
        public long Total => FreshLoad + TierChange + RingChange + Invalidate;

        internal StreamerBuildReasons(long freshLoad, long tierChange, long ringChange, long invalidate, long anchorRecentre)
        {
            FreshLoad = freshLoad;
            TierChange = tierChange;
            RingChange = ringChange;
            Invalidate = invalidate;
            AnchorRecentre = anchorRecentre;
        }
    }
}
