# Metal frame submission

Native Metal records directly into a command buffer. At the first drawable resolution, it ends the outgoing
pass, commits the offscreen prefix without presenting, then records the tail into another buffer on the same
queue before acquiring the drawable. Queue order puts the prefix before the tail. No pass, draw, GPU copy,
GPU wait or frame slot is added. Offscreen-only recordings keep one buffer.

`SetFramebuffer` ordinarily resolves the drawable immediately. If a compute or blit encoder is still open,
the bind keeps a metadata-only placeholder. Resolution happens at the next draw or clear-only flush, which
already needs that encoder to end. This avoids splitting an encoder just because the caller bound a target.
A placeholder superseded without draws or clears never acquires a drawable.

Both buffers belong to one recording. `Begin` alone claims the uniform-ring segment and staging-arena slot.
Only the final buffer encodes the frame timeline signal and arms completion fences. Its value owns the ring
segment and arena leases until completion. Native command buffers retain referenced resources until their
own completion, including resources whose wrappers are disposed meanwhile. The prefix and final buffers
both attach the ordinary completion error handler before committing. Either failure reaches the same device
loss latch. A handler attachment failure prevents the commit.

The prefix releases its uncommitted-buffer accounting and native retain before the tail is acquired. End
seals only the tail. Capture commands and the final timestamp still follow the game's draws in the tail.
Timestamp batches continue across the pair without a new batch or frame id. Present keeps its existing
separate buffer. An abandoned frame, including a failed tail acquisition, leaves the prefix's slot quarantined
until device teardown. It never creates a timeline signal, another submission or a wait to manufacture reuse.

Explicit `WaitForIdle` and blocking read maps also cover committed prefixes that have no final signal,
including abandoned recordings. After the timeline drain, a queue drain covers any prefix not already covered
by a later final signal or an earlier explicit queue drain. A completed final signal covers every prefix
committed before it, even if that prefix's own tail was abandoned. The submit lock is released during that wait. This
readiness proof neither submits the tail nor releases its slot or CPU write protection. A completed final
frame needs no additional queue drain, preserving the ordinary frame loop's waits.

## CPU write-after-record rule

Resolving a drawable can now execute earlier draws before the explicit `Submit`. CPU-visible bytes a
committed prefix can read or write stay immutable until the final buffer completes. An overlapping CPU write or
writable mapping throws `MetalFrameAccessException`, derived from `InvalidOperationException`. Its public
`Reason` distinguishes `OverlappingWrite`, `WritableMapping`, `MappedPrefix` and `AbandonedFrameSlot`.
`ResourceId`, `OffsetBytes`, `LengthBytes` and `FrameId` identify the refused access and protecting frame.
`RingSegment` identifies an abandoned slot and is -1 for ordinary access refusals.

Before committing, the list publishes the ranges its actual commands can read. Resource-set reads use only
the compiled shader's live bindings, including the declared buffer range, dynamic offset and physical ring
segment. Non-indexed vertex and instance inputs use their draw windows and strided attribute bytes, leaving
padding writable. Indexed vertex inputs cover every attribute address reachable through the binding because
indices may be GPU-produced. The index-buffer read itself is limited to the draw's index byte range. Buffer
copies protect their source and shared destination spans, including the existing alignment pad, so CPU
writes cannot race GPU writes either. Staging texture copies protect only
their source and shared destination rows, mip and layer. The staging-to-staging path protects the complete
software subresources it actually copies, including padding. No whole-device write prohibition is used. A disjoint CPU write performs a footprint lookup without polling
the driver.

Steady recording, prefix submission, disjoint CPU accesses and map/unmap cycles allocate no managed memory
once the workload has warmed. Each command list preallocates one read-footprint container per ring slot.
The normal ring gate proves final completion before that slot's footprint is cleared under the submit lock.
Retirement removes protection without clearing a footprint reclaimed by a later recording. Completion timing
never chooses a fresh footprint from a shared pool. Frame records are stored by value in ring-sized storage,
and mapping lease lists return to a pool after the outermost unmap. Before clearing the reclaimed slot,
it reserves the resource and interval capacities observed by the preceding recording. Only capacity is
carried forward. Resource identities, byte ranges and protection remain with their original slot. A
repeating scene shape therefore stays warm when its period differs from the ring depth.

Vertex coverage records all disconnected windows in one list-owned table. Its key is a coverage-header slot
and the first element, and its value is the maximum last element seen for that pair. The table is cleared at
Begin and footprint-generation changes, retaining its backing capacity. It grows only when aggregate window
demand exceeds the warmed recording peak, independently of binding order or per-key demand distribution.
Reachable indexed streams add no window entries. Total storage is O(N + D), where N is the coverage-header
high-water count and D is the peak total disconnected-window count of any recording, with normal dictionary
growth slack. Only integer slots and element ranges enter the table. No buffer, GPU object or key identity
crosses Begin. Lookup and max-end updates need no per-key enumeration or node chains. Exact byte coverage
is rebuilt independently in the slot-owned read index and retains the same prefix/completion rules.

Within a footprint, each resource has a reusable sorted append run for contiguous reads and separate AVL
indexes for earlier contiguous reads and strided reads. Increasing uniform-ring slices append in constant
work, with no tree insertion or rebalancing. Overlaps and adjacency merge with the last run interval without
filling alignment gaps. Earlier reads already covered by the run need only a binary search. Other earlier
reads enter the contiguous AVL, whose union is queried alongside the run. No run is migrated or rescanned
when recording changes direction. Strided entries retain exact element
sequences and index their bounding spans, while overlap checks still test only the actual attribute bytes.
Resource bindings record reads only after a set, dynamic offset, live shader table or recording changes.
An offset-only change records only dynamic elements, using cached live-element flags rather than querying
the shader-use dictionary again. Full read invalidation resolves those flags after a set, shader table,
physical segment or recording changes. Encoder invalidation does not invalidate already
recorded reads. Vertex coverage survives mesh, instance-buffer and pipeline rebinds within the recording.
Its key includes buffer identity, binding offset, stride, step rate, instance versus vertex stepping,
attribute byte ranges and indexed reachability. Equivalent pipeline stream layouts share coverage by value.
Stream shapes resolve to recording-local integer identities on a layout change, so mesh rebinds do not
repeat structural attribute comparisons.
Indexed streams record reachable attributes once per key and leave the per-draw window loop. Only active
non-indexed or instance streams need a window check. Attribute sizes and stream membership are resolved
with the pipeline, rather than rediscovered per draw. Adjacent attributes in a non-indexed or instance
window coalesce into one exact byte range per element, without crossing padding. Indexed reachable reads
keep separate attributes because a buffer can end partway through its last vertex, leaving only some
attributes fully addressable (#1696).

Recording an increasing contiguous slice costs O(1) after storage warm-up. Other changed reads cost O(log n),
amortized across contiguous intervals removed by coalescing. A contiguous lookup
costs O(log n). A strided lookup visits only candidate spans and their search paths, bounded by
O((k + 1) log n), where k is the number of overlapping strided bounding spans examined. Prefix
publication queries that same index once per writable mapping rather than crossing every read with every
mapping. No access scans all reads merely because the frame has many draws. This avoids the quadratic
recording and access work that stalled draw-heavy Mac frames (#1692).

Each vertex coverage entry keeps a primary contiguous element run and a dictionary of disconnected windows,
keyed by first element with the greatest recorded last element. The primary run only grows after overlapping
or adjacent windows have actually been recorded. It never moves to a disconnected window or fills a skipped
element. A window inside the primary run needs two bound comparisons. A previously recorded disconnected
window, including a shorter same-start window, needs an expected O(1) dictionary lookup and no interval-index
work. Different-start subwindows outside the primary run conservatively record through the exact index on
their first visit. Their later revisits use the cache too. The cache does not approximate protection by a
bounding span, and never changes CPU access queries or prefix publication.

Dictionaries, append runs, coverage entries, binding cursors and index node arrays retain their capacity
across recordings. Coverage keys and windows are cleared on a footprint or generation change, including
ring-slot reuse and interleaved command lists. No shared resource carries mutable coverage or cursors.
Stable recording positions retain storage rather than circulating it through a LIFO pool, so alternating
main and capture recordings with different resource counts cannot redistribute and regrow warmed storage.
Removed nodes are reused. A larger workload may grow storage during warm-up. Steady frames never pay for
those containers again.

Binding elements and vertex attributes own resource cursors, avoiding resource dictionary lookups on steady
draws. Each cached vertex stream owns its attribute cursors across rebinds. A new consecutive window extends
its exact sequence and refreshes ancestor bounds without insertion or balancing, in O(log s) for s strided
sequences on that resource. Other new shapes, stride phases or skipped elements retain the indexed worst
case above. Duplicate inserts are lookups with no tree writes. Increasing uniform slices keep their O(1)
append path. Unchanged draws perform no index work.

The internal diagnostics count index probes, insertion attempts, extension operations, rotations, node
mutations and stream coverage checks. The 3,000-draw fresh-slice regression bounds probes and mutations at
twelve per draw plus a fixed setup allowance, with no rotations or resource/shader-use hash lookups between
mesh or static-set rebinds. The 2,850-draw multi-pass regression models three cascades, six point-shadow faces,
a geometry pass and forward draws over padded, disconnected instance windows in different orders. After
the first cascade, each draw owes only two fresh uniform slices, at most two index probes and two writes,
with no tree insertion, extension or rotation and at most twelve coverage steps. Adjacent instance
attributes owe at most one tree extension per first-pass draw. These are deterministic
work bounds, not a claim about elapsed frame time (#1696).

Publishing and CPU access share the submit lock. A writable mapping opened before the prefix must be unmapped
before the prefix commits. A read-only mapping gives no write permission. Using a pointer after `Unmap` or
disposal violates the map lifecycle. Refused writable mappings fail before the map's ordinary drain.
Nested maps unwind in reverse order, so unmapping a nested read does not release an older writable lease.
Buffer mapping captures its physical segment base under the protection lock and uses that base for both the
lease and the returned pointer. A concurrent recording may rotate the ring while the normal map drain runs,
but it cannot move the mapped span away from its lease.
Completion polling releases range protection only after the final signal, never after the prefix or Submit.
An unfinished prefix has no final signal, so its ranges and frame slot remain quarantined.

| Write path | Protection |
| --- | --- |
| Command-list uniform upload | Checks its physical ring segment and byte span under the protection lock before copying |
| Device `UpdateBuffer`, all overloads | The shared writer checks non-ring bytes before copying. Uniform writes plan immediate copies and deferred patches under the submit lock. Only immediate physical copies need prefix protection, after the existing depth-one completion wait. All checks precede any copy or patch change, so refusal is atomic |
| Command-list non-uniform upload | Takes fresh, append-only staging bytes. The GPU copy is ordered after earlier commands, including the prefix, by the same queue |
| Staging arena reuse | The arena keeps the original frame slot. Only final completion recycles it. An abandoned prefix quarantines the slot |
| Writable buffer or staging-texture map | Checks and leases the mapped span. A prefix refuses an already-open overlapping lease. `Unmap` removes the lease |
| Staging-texture upload | Checks each written row and subresource under the same lock before copying any row |
| Private-texture upload | Copies the CPU payload into fresh setup staging. Setup flushes before the prefix. Later setup writes execute after the prefix on the same tracked queue, rather than mutating shared CPU bytes |

SpriteBatch uploads only the new Begin's 64-byte matrix at its disjoint dynamic slot. The old whole-mirror
rewrite existed for a retired Veldrid D3D11 staging path and would race even when the overwritten bytes were
identical. Sprite vertex runs use disjoint offsets in a dedicated buffer per flush. Render3D packs frame,
shadow, motion, skinning and post-process uniforms ahead of their readers, and its final-blit uniforms are
written before the drawable bind. Game.Render3D and GUI use these renderers rather than separate GPU writers.

## Attachment store intent

`IGpuCommandList.SetFramebufferWithStoreActions` accepts `GpuAttachmentStoreActions`. Zero means Store for
every attachment. `DiscardColourMask` chooses individual colour attachments. `DiscardDepth` and
`DiscardStencil` are independent, including on a combined-format texture. Intent applies to every native
rendering pass opened for that binding. The caller must not reopen a pass and load discarded data after an
intervening transfer or dispatch. A backend may conservatively retain discarded contents.

Metal maps dead attachments to `MTLStoreAction.DontCare`. Vulkan maps them to `AttachmentStoreOp.DontCare`.
D3D11 retains them through its ordinary framebuffer bind because its existing pipeline has no per-plane
store control. Its store-intent entry point retains the recording guard and the caller identifier. There is no live GL backend. The retired OpenGL selection does not construct a device. No GL call
or backend is introduced by this intent.

The directional cascade pass owns one R32F colour atlas and separate D32FloatS8UInt working storage. With
no pass observer, every cascade draws into that atlas in one uninterrupted render encoder. Matrix and skinning uploads use uniform
ring CPU writes, so they add no transfer boundary inside it. The pass clears the whole depth and stencil
before its first draw. Sampling, shadow captures, readback and dirty-skip reuse name only the R32F colour
atlas. The working depth/stencil texture is retained only by the graph, framebuffer and matching pipelines,
then disposed with that graph. Later cascades share the same encoder. Every next-frame rebuild clears it
again, and a skipped frame never reads it.

A registered pass observer may record timestamp markers that close native encoders between caster
families. The scene therefore retains working depth and stencil whenever an observer is present. This
preserves observed and captured output, including rigid versus skinned occlusion after a marker. Per-pass
timing campaigns consequently include those stores. The unobserved shipping path receives the discard.

This resolves #595's determinism concern. Discarded working bytes are undefined, but no output depends on
them before their next clear. The colour atlas and every attachment with a later reader still use Store.
Bitwise GPU comparisons cover the sampled atlas and all six main attachments after three scene frames.
