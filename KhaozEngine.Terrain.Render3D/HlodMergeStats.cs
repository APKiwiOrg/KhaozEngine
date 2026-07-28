namespace KhaozEngine.Terrain
{
    /// <summary>Cumulative HLOD merge counters for one <see cref="Scene3DChunkSink"/>, read through
    /// <see cref="Scene3DChunkSink.MergeStats"/>. Always on and allocation-free (four <c>long</c> adds behind
    /// <c>Interlocked</c>, since the merge runs on the streamer's background build threads), in the same shape as
    /// <c>Scene3D.LastFrameStats</c>: a value snapshot a consumer reads whenever it likes. Counters are cumulative
    /// for the sink's whole lifetime and never reset, so a per-second rate is the difference of two samples.
    /// <para><b>Why BUILT and UPLOADED are separate.</b> A merged cluster is only ever consumed by a fresh chunk
    /// load or a field rebuild: a tier re-LOD and a ring change both keep the mesh already on the GPU. Before issue
    /// #393 the sink merged the cluster on EVERY chunk build and then threw the result away on those two paths, so
    /// a running player paid a multi-megabyte large-object allocation per re-LOD for nothing. <c>Built</c> minus
    /// <c>Uploaded</c> is exactly that waste, which is why it is counted rather than reasoned about: it must stay at
    /// zero under steady streaming, and a non-zero <see cref="DiscardedBytes"/> is the regression signal.</para>
    /// <para>Zero discarded is the steady state, but it is not an invariant, and a small trickle is not the bug
    /// coming back. The streamer supersedes an in-flight build when a chunk's target tier changes before its first
    /// one lands (last request wins), and a superseded fresh load has already merged. That is churn during loading,
    /// bounded by how fast the player crosses tiers with chunks still in flight, and it does not grow with distance
    /// travelled. What issue #393 looked like is different in kind: discarded climbing in step with consumed,
    /// forever.</para>
    /// <para>One count is one LAYER of one chunk (a sink with two HLOD layers counts two per chunk build), and the
    /// bytes are the PRODUCED merged mesh, vertices plus 32-bit indices. That is the welded result, so it is the
    /// floor rather than the peak: the pre-weld merge the weld consumes is several times larger and is transient.
    /// An empty cluster counts as a build of zero bytes on both sides, so the two totals still match exactly.</para></summary>
    public readonly struct HlodMergeStats
    {
        /// <summary>Cluster merges COMPUTED since the sink was built (one per HLOD layer per chunk build).</summary>
        public long Built { get; }

        /// <summary>Bytes of merged mesh computed (vertices at <c>ModelVertex.SizeInBytes</c> plus 4 bytes per index).</summary>
        public long BuiltBytes { get; }

        /// <summary>Cluster merges CONSUMED by an apply (a fresh load or a field rebuild). Equal to
        /// <see cref="Built"/> when nothing is being wasted.</summary>
        public long Uploaded { get; }

        /// <summary>Bytes of merged mesh consumed by an apply, counted exactly like <see cref="BuiltBytes"/>.</summary>
        public long UploadedBytes { get; }

        /// <summary>Merges computed and then thrown away. Zero is the healthy value.</summary>
        public long Discarded => Built - Uploaded;

        /// <summary>Bytes of merged mesh computed and then thrown away. Zero is the healthy value.</summary>
        public long DiscardedBytes => BuiltBytes - UploadedBytes;

        internal HlodMergeStats(long built, long builtBytes, long uploaded, long uploadedBytes)
        {
            Built = built;
            BuiltBytes = builtBytes;
            Uploaded = uploaded;
            UploadedBytes = uploadedBytes;
        }
    }
}
