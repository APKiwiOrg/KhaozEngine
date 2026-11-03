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
the driver. Read-footprint dictionaries and range lists are reused after final completion.

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
