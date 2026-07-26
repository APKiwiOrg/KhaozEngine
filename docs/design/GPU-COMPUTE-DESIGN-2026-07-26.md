# GPU compute in the KhaozEngine.Gpu seam

Design rationale for the 15.2.0 compute release. Issue: [#309](https://github.com/APKiwiOrg/KhaozEngine/issues/309).
Consumer program: [#310](https://github.com/APKiwiOrg/KhaozEngine/issues/310), the Tessendorf FFT ocean.

This is the **why**. What shipped and how to use it live in `CHANGELOG.md` 15.2.0,
`docs/USING-KHAOZENGINE.md` ("GPU compute shaders") and `KhaozEngine.Gpu/README.md`.

## The problem

The seam had no compute at all. Its only trace was an unreachable `ShaderStages.Compute` flag in
`GpuEnums.cs`. Everything exposed was the graphics path: a GLSL 450 vertex + fragment pair, a graphics
pipeline, and draw commands. A general parallel job on the GPU could only be faked as a fullscreen render
pass, which covers one-output-per-pixel maps and nothing else, and cannot write a buffer at all.

The pull is the FFT ocean. Every previous water round removed one coherent structure from a small sum of
directional components and the eye found the next one, which is the signature of the method rather than of any
component count. A real directional spectrum needs an FFT, an FFT needs compute, and compute needs this.

## Decisions

### Separate handle types for compute, not a shared pipeline type

The backend has ONE `Pipeline` type for both kinds and ONE `SetPipeline` that branches on it, so binding a
compute pipeline for a draw is a runtime error there. Mirroring that shape would import the error. Instead
`IGpuComputeShader` and `IGpuComputePipeline` are distinct from `IGpuShaderSet` and `IGpuPipeline`, and
`SetComputePipeline` / `SetComputeResourceSet` are distinct from their graphics counterparts. The cost is two
small wrapper classes; the gain is that the mistake does not compile. This is the same trade the seam already
makes elsewhere (`GpuBufferRange`, `IGpuBindableResource`).

### The workgroup size is read from the shader, never restated by the caller

This is the decision with the highest value per line, and it exists because of a specific silent-failure shape
one layer down.

`ComputePipelineDescription` carries `ThreadGroupSizeX/Y/Z`, validates them against nothing, and exactly one
backend reads them: Metal, where they become the `threadsPerThreadgroup` argument of `dispatchThreadGroups`
(MSL does not carry the workgroup size the way SPIR-V does, so Metal needs it again at encode time). Vulkan and
Direct3D11 ignore the fields entirely and take the size from the module. A description that disagrees with the
shader's `layout(local_size_x = ...)` is therefore **invisible on two backends and silently produces wrong
dispatch geometry on the third**. Nothing warns. And `Veldrid.SPIRV` does not report the size back either: its
`ComputeCompilationResult` carries only the cross-compiled source and a resource-layout reflection.

Two of those facts make it worse than an ordinary duplicated constant. Metal is the primary dev platform here,
so the one backend that misbehaves is the one everything is written on. And Veldrid's own compute test suite
never runs on Metal at all (`MetalComputeTests` derives from `RenderTests` rather than `ComputeTests`, so the
compute cases are silently skipped), which is why the shape survived upstream.

The fix is to parse the `LocalSize` execution mode out of the SPIR-V module (`Internal/SpirvLocalSize`, about
fifty lines), surface it on `IGpuComputeShader.ThreadGroupSizeX/Y/Z`, and build the pipeline from that. There is
then no second copy to disagree with, and a consumer computing group counts reads the size off the shader
instead of repeating a magic number. A source with no literal workgroup declaration is a
`ShaderValidationException` at creation rather than a wrong result at dispatch.

The alternative considered was keeping the fields and validating them against the parsed value. That still
requires the caller to write the number twice and turns a silent bug into a loud one, which is better than
today but strictly worse than not having the second copy.

### Structured buffers are always RAW views on Direct3D11

The engine's only shader path is GLSL 450 -> SPIR-V -> SPIRV-Cross. Dumping what SPIRV-Cross actually emits for
a GLSL storage block settles the question: it is `ByteAddressBuffer` / `RWByteAddressBuffer`, never
`StructuredBuffer<T>`, for both a scalar runtime array and a struct one. A byte-address buffer needs a RAW view
(`R32_Typeless` plus the raw view flag), which is a separate `rawBuffer` flag on the backend's buffer
description, and the default structured view would not match the shader.

Since the shader shape is fixed by the pipeline, this has exactly one correct value, so it is set automatically
for any buffer whose usage includes a structured-buffer flag rather than exposed as a knob a caller can only
get wrong. It is a no-op on Metal and Vulkan. Nothing outside the seam used structured buffers before this
release, so there is no behaviour to regress. `GpuBufferDescription.StructureByteStride` stays for the other
backends and is documented as advisory on the D3D11 path.

### Ordering: two guaranteed patterns, and no barrier call

There is no barrier method on this seam because the layer below has none at all: no `MemoryBarrier`, no
`TransitionResource`, nothing. Its one full-pipeline-barrier helper is dead code with zero callers. So the only
ordering available is whatever each backend does implicitly, and the three do not agree. Rather than invent an
abstraction over three different mechanisms, the seam guarantees exactly the two patterns that are actually
correct everywhere, writes them on `IGpuCommandList`, and proves them by test on all three backends.

**Compute writes a storage texture, then a graphics pass samples it.** Correct when both are recorded in ONE
command list and the texture is created `Storage | Sampled`. Per backend:

- *Vulkan* tracks image layouts. Binding the texture for a dispatch transitions it to `General` and queues it
  for restore; the next draw drains that queue and transitions it back to `ShaderReadOnlyOptimal` before the
  render pass begins. Two constraints fall out of the implementation. The restore queue is only fed for textures
  that also carry `Sampled`, so a `Storage`-only texture never gets one. And the queue is per-command-list
  instance state that neither `Begin` nor `End` touches, so splitting the pass across two command lists means
  the second list's queue is empty and no barrier is emitted at all, while the graphics descriptor still claims
  `ShaderReadOnlyOptimal`. Both failures are silent.
- *Metal* ends the compute encoder whenever a render encoder begins (and symmetrically for blit). Encoders in
  one command buffer execute in order with implicit synchronization under the serial dispatch type, which is
  what is used (there is no concurrent-dispatch encoder binding anywhere in the backend). No explicit barriers
  exist or are needed.
- *Direct3D11* has no barriers by design, but a resource cannot be bound as a UAV and an SRV at once. The
  backend tracks bound SRVs and UAVs per texture and unbinds the conflicting one automatically when the other
  is bound, in both directions. Leaving the compute resource set bound is therefore fine.

**A dispatch that reads what an earlier dispatch wrote.** NOT correct inside one command list, and this is the
uncomfortable finding. On Vulkan there is no cross-dispatch hazard handling whatsoever: storage buffers are not
tracked (the tracking lists are image lists, and a storage-buffer descriptor only takes a refcount), and a
storage image written by two consecutive dispatches stays in `General` both times, so the layout transition is
a no-op and emits nothing. Dispatches within a command buffer may overlap. So the ping-pong at the heart of any
multi-pass compute algorithm has no ordering unless a submission boundary provides it, and the seam's guarantee
is `End` + `Submit` + `WaitForIdle` between dependent stages. Independent dispatches can still share a list
freely; it is only the read-after-write chain that pays.

Two alternatives were weighed and rejected. Relying on the practical behaviour of real drivers is what most
Veldrid compute code does, and it would probably pass on all three CI backends today (software rasterizers do
not aggressively overlap dispatches), but "probably passes on the machines we test on" is not a contract to put
on a public engine API, and the failure mode on a real discrete GPU is silent corruption rather than an error.
Arranging the ping-pong so each stage flips a pair of storage TEXTURES between sampled and storage bindings does
make Vulkan emit an image barrier every stage, since the layouts genuinely change, but reading the emitted
masks shows the `General -> ShaderReadOnlyOptimal` case uses `Transfer`/`TransferRead` as its source scope
instead of `ComputeShader`/`ShaderWrite`, so the dependency it creates is not the one needed. That is an
upstream bug, unchanged on their master branch, and not something to build a guarantee on.

The consequence is a GPU stall per dependent stage, which is a real ceiling: a 128-point 2D FFT is 14 stalls
per transform. That constraint is recorded on #310 so the ocean program designs around it rather than
discovering it, and lifting it needs either a newer backend with a barrier API or a patch to the Vulkan
command list. It is not something this release can fix from above.

### Readback drains before its copy, not only after

`GpuReadback.ReadBuffer<T>` waits for idle before recording its copy as well as after. The work that produced
the data was submitted on a different command list, and a copy in a later submission is not ordered against it
on every backend (Vulkan submissions carry no semaphores, and the buffer copy emits no barrier ahead of
itself). A readback is a synchronous operation by nature, so the extra drain on an already-idle device costs
nothing measurable and removes a footgun that would only ever show up as intermittently stale data.

### The proof tests are the specification

Three `[GpuFact]` tests, readback-verified against exact or reference values, running on Metal, Direct3D11/WARP
and Vulkan/lavapipe:

1. `ComputeBufferGpuTests`: a two-pass parallel reduction of 4096 unsigned integers. Unsigned, so the expected
   value is exact regardless of summation order. Exercises storage buffers, workgroup shared memory plus
   `barrier()`, two dependent dispatches, and the typed readback.
2. `ComputeTextureHandoffGpuTests`: the compute-to-graphics handoff, asserted per texel. The compute shader
   stores `x / 255.0` and `y / 255.0`, which re-quantize to exactly `x` and `y` in UNorm8, so the assertion is
   exact rather than a tolerance. The fragment shader derives its UV from `gl_FragCoord` (top-left origin on all
   three backends) rather than a varying, so the test is immune to the backends' clip-space Y disagreement.
   Both the sampled render target AND the storage texture itself are read back, so a failure of the handoff can
   be told apart from a compute pass that never wrote anything.
3. `ComputeFftGpuTests`: a 2D radix-2 Stockham FFT, checked three ways. A CPU reference of the same butterfly
   turns an impulse into a flat spectrum (the textbook pair, which is what makes the reference trustworthy); the
   GPU forward transform matches that reference elementwise; and forward-then-inverse returns the original grid,
   which catches a sign or normalization error a self-consistent forward pass would not.

Test 3 is deliberate rather than incidental. It is the seam between this program and the ocean: the exact
algorithm the ocean is built on is validated on every backend before any ocean code exists, so a later ocean
failure can be attributed to the ocean rather than to the transform or the compute plumbing under it. Stockham
autosort was chosen over Cooley-Tukey for the same reason the ocean will want it, no bit-reversal pass and a
clean ping-pong between two buffers.

## Deferred, deliberately

- **Indirect dispatch** (`DispatchIndirect`). Nothing wants it yet, and it would ship untested.
- **Specialization constants for compute.** The layer below mis-marshals them on the compute cross-compile path
  (the managed struct is 16 bytes with the payload at offset 8, the native one is packed to 12 with it at
  offset 4), so they are wrong on Metal, Direct3D11 and OpenGL and only correct on Vulkan, which never
  cross-compiles. Not exposed rather than exposed-and-broken. A shader that needs a compile-time constant should
  get it from a uniform or from source substitution.
- **A cross-dispatch barrier**, per the ordering section above. Not fixable from this layer.
- **Compute-capable OpenGL/GLES.** `SupportsCompute` reports the backend's own flag, which is a gated extension
  check there, but the OpenGL path has no verified device in CI at all (see #59), so nothing about compute on it
  is claimed.
