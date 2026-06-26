namespace KhaozEngine.Ecs;

// Per-row actions for the buffered ParallelForEach overloads: like RefAction but with a per-worker
// EntityCommandBuffer to record structural changes (Create/Despawn/Set/Remove). The buffers are merged in a
// deterministic order at the join and played back on the world after the parallel section, so structural changes
// stay thread-safe and reproducible. The action itself must still be per-row-pure for component access (touch only
// the ref components handed in); only structural changes go through the buffer.

public delegate void RefBufferAction<T1>(Entity e, ref T1 c1, EntityCommandBuffer commands);
public delegate void RefBufferAction<T1, T2>(Entity e, ref T1 c1, ref T2 c2, EntityCommandBuffer commands);
public delegate void RefBufferAction<T1, T2, T3>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3, EntityCommandBuffer commands);
public delegate void RefBufferAction<T1, T2, T3, T4>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4, EntityCommandBuffer commands);
