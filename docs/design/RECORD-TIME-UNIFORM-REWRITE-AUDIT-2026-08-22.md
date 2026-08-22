# Record-time uniform rewrite audit (2026-08-22)

Issue: [#483](https://github.com/APKiwiOrg/KhaozEngine/issues/483). Written against engine `17.39.0`.

Sweep every record-time `IGpuCommandList.UpdateBuffer` to a UNIFORM buffer in the engine and answer one
question per site: can a frame write the same byte range twice with a draw recorded in between? That shape
renders differently on the engine's own native backends than on the Veldrid ones, silently.

## 1. What the hazard actually is, measured

On `Direct3D11Native`, `VulkanNative` and `MetalNative` a record-time `UpdateBuffer` to a uniform buffer is a
memcpy into that frame's own ring segment (`D3D11UniformRing.Write`, `VulkanUniformRing.Write`,
`MetalUniformRing.Write`). It records no command, so it is not ordered against the draws in the same list. One
segment holds one copy of those bytes for the whole frame, so the LAST write decides every byte and a draw
recorded between two writes reads the SECOND one.

That was a claim about three implementations, taken from their own doc comments, and #483 exists precisely
because the same claim was made about a fourth thing (Veldrid's Direct3D 11 leg) where it is false. So it is
measured rather than believed: `KhaozEngine.Render.Tests/Gpu/RecordTimeUniformRewriteGpuTests.cs` writes A,
draws into one target, rewrites the same sixteen bytes with B, draws into a second target and reads one texel
out of each.

| backend | first draw | second draw | reading |
|---|---|---|---|
| `MetalNative` | blue | blue | COLLAPSED: both draws read the last write |
| `Metal` (Veldrid) | red | blue | ORDERED: each draw read the value written before it |

The control matters as much as the assertion. It says the two families really do disagree, so a hazard is a
divergence between backends rather than a bug that would show up everywhere, which is why no committed golden
could have caught the one found below: the golden families are baked on the incumbents.

Three conditions have to hold at once for a site to be a hazard:

1. the buffer is a UNIFORM buffer (the only usage any of the three rings backs, see `MetalBufferPolicy.IsRingBacked`
   and its two siblings), so a vertex, index or instance stream re-streamed between draws is not one,
2. a DRAW or dispatch is recorded between the two writes, and
3. the rewritten bytes DIFFER. Rewriting a range with the bytes it already holds is a no-op for anything already
   recorded, which is what makes the engine's whole-mirror upload pattern safe.

## 2. The site table

Every uniform buffer created in `KhaozEngine.Render2D` and `KhaozEngine.Render3D`, which between them are the
only packages that create one at all (`Gui`, `Particles.Render3D`, `Terrain`/`TileWorld.Render3D`, `MapEditor`
and the samples draw through those two and declare no layout and no uniform buffer of their own).

| buffer | range written | writes per frame | draw between | verdict |
|---|---|---|---|---|
| `ModelRenderer._ubo` (1008 B) | whole, offset 0 | 1 (`SetFrameUniforms`, `Scene3D.cs:1844`) | no | safe: once per frame |
| `ModelRenderer` splat combined UBO, per material | whole, offset 0 | 1 per material (`DrawSplatRuns`) | no | safe: one buffer per material, each written once ahead of its draws |
| `ModelRenderer` tile-ground combined UBO, per material | whole, offset 0 | 1 per material (`DrawTileGroundRuns`) | no | safe: same shape as splat |
| `ModelRenderer._skinnedMainUbo` (8 x 9472 B) | whole, offset 0 | 1 (`UploadSkinnedMainSlots`) | no | safe: per-draw slots, one upload after every slot is packed |
| `ShadowMapRenderer._lightUbo` (4 x 256 B) | whole, offset 0 | 1 (`BeginDepthPass`) | no | safe: per-cascade slots + dynamic offset |
| `ShadowMapRenderer._skinnedUbo` (8 x 8448 B) | whole, offset 0 | 1 (`UploadSkinnedShadowSlots`) | no | safe: per (cascade, caster) slots + dynamic offset |
| `WaterRenderer._ubo` (4 x 768 B) | whole, offset 0 | 1 (`UploadSlots`) | no | safe: per-plane slots + dynamic offset, packed then uploaded (17.20.0) |
| `OverlayMeshRenderer._ubo` (8 x 256 B) | whole, offset 0 | 1 (`Flush`) | no | safe: per-draw slots + dynamic offset (17.18.0) |
| `OceanFftProducer._ubo` (80 B) | whole, offset 0 | 1 per command list | no | safe: one block serves both dispatches, and the priming pass carries its own on its own drained list |
| `PixelPostProcess` x10 (palette, edge, final, fxaa, bright, blurH, blurV, composite, tone, apply) | whole, offset 0 | at most 1 each | no | safe: every write is in `PrepareUniforms`, which runs once before the first `SetFramebuffer` |
| `SkyRenderer._ubo` | whole, offset 0 | 1 | no | safe: `Draw` runs once, and the sky/starfield switch takes at most one arm |
| `StarfieldRenderer._ubo` | whole, offset 0 | 1 | no | safe: as sky |
| `BeamRenderer._ubo` (80 B) | whole, offset 0 | 1 (`SetFrameUniforms`, before `Draw`) | no | safe |
| `TrailRenderer._ubo` (64 B) | whole, offset 0 | 1 (`SetFrameUniforms`) | no | safe: two draws follow it (additive then alpha) and neither writes |
| `TexturedBillboardRenderer._ubo` (64 B) | whole, offset 0 | 1 (`SetViewProj`, before the run loop) | no | safe |
| `DepthLineRenderer._ubo` (64 B) | whole, offset 0 | 1 | no | safe |
| `DistortionRenderer._frameUbo` | whole, offset 0 | 1 | no | safe |
| `ParticleRenderer._frameUbo` (192 B) | whole, offset 0 | 1 | no | safe |
| `TransitionRenderer._solidBuf` / `._crossBuf` (16 B) | whole, offset 0 | at most 1 (one arm of an if) | no | safe |
| `OverlayRenderer<T>._ubo` (64 B) as `LineRenderer` | whole, offset 0 | 1 | no | safe |
| `OverlayRenderer<T>._ubo` (64 B) as `FillRenderer` | whole, offset 0 | 1 | no | safe |
| `OverlayRenderer<T>._ubo` (64 B) as `BillboardRenderer` | whole, offset 0 | 2 (additive then alpha) | YES | safe by condition 3: both writes are `GpuClip.Correct(vp, caps)` from the same frame's `vp`, so the second write is byte-identical to the first |
| `SpriteBatch._vpUbo` (8 x 256 B) | whole, offset 0 | 1 per `Begin` | YES | safe by condition 3: a slot's mirror value never changes once its `Begin` claimed it, so every re-upload restates the bytes already recorded against (this is the pattern #408 proved) |
| `GroundDecalRenderer._frameUbo` (80 B, pre-17.39.0) | whole, offset 0 | 2 (blob-shadow pass, main pass) | YES | **HAZARD**, see section 3 |

Device-level `IGpuDevice.UpdateBuffer` sites are out of scope here and stay so: that call reaches every ring
segment and leaves a pending patch on any the GPU has not finished with, so a value written once persists for the
buffer's life. The neighbouring case of one landing mid-command-stream is #415, closed. The shipped device-level
sites are all load-time or bake-time (`ModelRenderer.cs:495`, `ModelRenderer.TileGround.cs:77`,
`OceanFftProducer.cs:446` and `:492`, `WaterRenderer.cs:303`, `Scene3D.cs:546`, `:566`, `:572`, `:781`) or write
vertex buffers (`SpriteBatch.cs:688`, `:716`), none of them ring-backed.

## 3. The one hazard, and why the fix is slots

`Scene3D` runs `GroundDecalRenderer.Draw` twice in one frame and the two runs disagree about one lane:

- `Scene3D.cs:1903`, the blob-shadow pass. Runs early, before the skinned draws, after a depth-only resolve. It
  must NOT reject dynamic-tagged pixels, because the normal target the reject reads is not yet valid there.
- `Scene3D.cs:2020`, the main decal pass. Runs after the depth+normal resolve. It MUST reject, so a ground decal
  never paints onto a character (#235).

Both wrote the same 80 bytes at offset 0, with the blob pass's draws recorded between the two writes and with
`TimeQ.w` differing. On the natives the second write decided both, so the blob pass ran with the reject on
against an invalid normal target. On the incumbents it was correct, which is why the goldens are silent.

**The fix.** The buffer is two 256-byte slots. Each pass packs its own slot into a CPU mirror, uploads the mirror
WHOLE, and binds its slot with a dynamic offset. The pass is an explicit `FramePass` enum rather than the old
`bool rejectDynamicGeometry`, so the slot and the reject are one decision and cannot drift.

**Alternatives weighed.**

- *Move the reject into the per-instance attribute stream.* Also removes the rewrite, and is arguably where the
  flag belongs, since it is a property of the decal set rather than of the frame. Declined: it changes a shader,
  the `DecalInstance` struct and the bytes every golden reads, for a correctness fix that does not need any of
  that. The slot version is pixel-identical on every backend that was already ordered.
- *Two `GroundDecalRenderer` instances, one per pass.* Doubles four pipelines and a shader set for two passes
  that differ in one float.
- *Reorder so both writes precede all decal draws.* Impossible: the two passes are separated by the whole
  skinned and sky half of the frame by design.

**Why the whole-mirror upload does not reintroduce the problem.** Each pass's upload restates the other pass's
slot with the bytes it already holds, which is condition 3 and is the same argument `SpriteBatch` has carried
since #408. Keeping it whole is what keeps the write off Veldrid's blocking partial-uniform-write staging route
on Direct3D 11.

**Golden classification.** Zero movement expected or observed on the incumbent families: the same 80 bytes are
read, from a different offset in a bigger buffer. On the natives this is a CORRECTION, in a configuration
(Blob shadows plus queued ground decals in one frame) that no committed golden covers. Nothing was rebaked.

## 4. The guard, which is the durable half

A written table nobody re-derives is worth less than a check that fails. Three pieces, all in
`KhaozEngine.Render.Tests`:

- `RecordingGpuCommandList` stamps each recorded upload with how many draws and dispatches preceded it. That is
  what turns a pair of uploads into an ordering fact rather than a count.
- `UniformRewriteAudit.Scan` applies the three conditions of section 1 to one frame's uploads and returns the
  offending pairs, each naming the buffer, the overlapping range and how many draws sit between the two writes.
- `UniformBufferTrackingGpuDevice` supplies the one fact `IGpuBuffer` does not carry, which is whether the buffer
  was created with the uniform bit. Without it the scan would have to guess, and a guess would either miss the
  hazard or flag every vertex stream the frame legitimately re-streams.

`UniformRewriteGuardGpuTests` renders a deliberately greedy frame through the real renderers, with every queue
that gates a pass filled, and asserts the finding list is empty. It checks its own inputs first (uniform buffers
were recognised, draws were recorded, more than ten uniform uploads happened) so an empty answer cannot come
from an empty frame. Reverting the section-3 fix turns it red with

```
a 512-byte uniform buffer had [0, 80) written twice with 7 draw(s) recorded between the two writes, and the bytes differ
```

so it catches the hazard it was written for rather than only watching for a hypothetical one.

**What it does NOT reach**, recorded rather than glossed: a pass whose queue the guard's frame does not fill
records nothing and cannot be audited. The frame fills every one it can today, but a NEW pass added later is
outside the guard until its queue is added to `QueueEverything`. That is the standing maintenance cost, and it is
the reason section 2's table is kept as well as the guard.

## 5. What consumers were told

`docs/USING-KHAOZENGINE.md` already stated the rule for `Direct3D11Native` and `VulkanNative` ("a uniform write
lands when you make it, not when the list is submitted"). The Metal section carried the ring's creation refusal
but not its ordering rule, so 17.39.0 adds the same paragraph there. Nothing about the rule changed: it is now
stated for all three of the backends it holds on, and measured on the one that can be measured locally.
