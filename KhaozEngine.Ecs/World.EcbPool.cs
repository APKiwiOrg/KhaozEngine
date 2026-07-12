using System.Collections.Generic;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    // Calling-thread-only pool of EntityCommandBuffers reused across World's buffered ParallelForEach calls,
    // replacing the per-call allocation of a fresh EntityCommandBuffer[] plus k new buffers per archetype. The
    // internal Query.ParallelForEachPooled variants rent k buffers per archetype (before scheduler.For, on the
    // calling thread) via RentEcb, and World returns each after its Playback (Playback leaves the buffer clean via
    // its own finally). The rent/return lifecycle is closed within World, so the pool cannot drain: the public
    // Query sink overloads allocate fresh caller-owned buffers and never touch it. A plain Stack is safe because
    // every Rent/Return happens on the calling thread - worker chunks only record into an already-rented buffer,
    // they never touch the pool. Internal so the pooling tests can observe reuse via Count.
    internal readonly Stack<EntityCommandBuffer> _ecbPool = new();

    // Pool of sink lists so a buffered ParallelForEach that re-enters during playback (a Defer action running
    // another buffered ParallelForEach) gets its own list instead of clobbering the outer call's sink. Steady-state
    // (no nesting) reuses the one list, so the buffered World path allocates no sink list per call.
    private readonly Stack<List<EntityCommandBuffer>> _ecbSinkPool = new();

    internal EntityCommandBuffer RentEcb() => _ecbPool.Count > 0 ? _ecbPool.Pop() : new EntityCommandBuffer();

    internal void ReturnEcb(EntityCommandBuffer ecb) => _ecbPool.Push(ecb);

    private List<EntityCommandBuffer> RentEcbSink() =>
        _ecbSinkPool.Count > 0 ? _ecbSinkPool.Pop() : new List<EntityCommandBuffer>();

    private void ReturnEcbSink(List<EntityCommandBuffer> sink) => _ecbSinkPool.Push(sink);

    // Plays back each recorded buffer in chunk order (identical to a sequential ForEach recording into one buffer),
    // returning each clean buffer to the pool, then recycles the list. Runs on the calling thread AFTER the parallel
    // section ends, so its structural changes are legal. If a Playback throws, the remaining un-played buffers are
    // dropped rather than pooled: an un-played buffer still holds recorded commands, and returning a dirty buffer
    // would corrupt a later rent. The buffer whose Playback threw is left clean by Playback's own finally, but we
    // stop and drop the rest to keep the failure path simple. The list is always recycled.
    private void PlaybackSink(List<EntityCommandBuffer> sink)
    {
        try
        {
            for (int i = 0; i < sink.Count; i++)
            {
                EntityCommandBuffer ecb = sink[i];
                ecb.Playback(this);
                ReturnEcb(ecb);          // reached only when this buffer played back cleanly
            }
        }
        finally
        {
            sink.Clear();
            ReturnEcbSink(sink);
        }
    }

    // Exception path for a buffered ParallelForEach: the parallel section threw, so the buffers in the sink hold
    // partial, un-played commands. They are DROPPED (not returned to the pool) - pooling a dirty buffer would hand a
    // later rent stale commands - and only the emptied list is recycled. The GC reclaims the dropped buffers.
    private void DropSink(List<EntityCommandBuffer> sink)
    {
        sink.Clear();
        ReturnEcbSink(sink);
    }
}
