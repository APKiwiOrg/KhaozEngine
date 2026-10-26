# Public renderer controls

These shaders and native workloads are test-harness inputs. They do not change production rendering or
create an execution authorization. `RendererPerfControls.Create` accepts a matching version, work digest
and the existing full or pilot frame counts. `RendererPerfNativeSession` retains the production host,
outer timestamps, completion fences and final short batch. No second measurement loop exists here.

Every full control has 64 warm and 128 measured frames, one iteration. The pilot uses only controls
1, 4, 5, 7, 9 and 11, with 32 warm and 64 measured frames. Output validation runs once after final
completion. Shader compilation, seed uploads and readbacks are outside frame brackets. The common
production host clear and End tail remain in each raw outer duration. Attachment initialization is
also retained. Neither cost is silently subtracted to manufacture a shader-only time.

Before allocating control resources, the workload checks each whole storage binding against
`GpuCapabilities.MaxStorageBufferRangeBytes`. A missing or insufficient limit refuses with the control
ID, required bytes and device limit. Control 7 needs 128 MiB per buffer, control 8 needs 32 MiB,
control 9 needs a 256 KiB input and 2 MiB output, and control 10 needs 256 KiB per buffer.
Controls 4 through 6 bind individual 4-byte buffers, even when 36 outputs are allocated.
The other controls have no storage-buffer bindings. No buffer or readback is reduced to fit.
Control 7 fixes the same workload on every leg at the portable 128 MiB storage range advertised by
lavapipe and guaranteed by Direct3D 11 for WARP. It remains four times control 8's stream size.

| Control | Fixed work | Retention evidence |
| --- | --- | --- |
| 1 | Empty production frame, RGBA8 display target | Full cleared image |
| 2 | Empty 1080p main MRT, RGBA16F, RGBA8, R32F and RG16F, D32F/S8, one sample | Every color attachment and depth clear |
| 3 | Empty 2048-square depth-only pass, D32F/S8, one sample | Cleared depth and host image |
| 4 | One 1-thread dispatch, one input word and observable output word | Seed plus one |
| 5 | 36 dispatches, each reading the previous output | All 36 incremented outputs |
| 6 | The same 36 dispatches with independent outputs and a common read-only seed | All 36 seed-plus-one outputs |
| 7 | 128 MiB input and 128 MiB output, local size 256, 1024 by 128 groups, portable storage range | Every XOR-transformed word |
| 8 | Matched 32 MiB input and output, local size 256, 1024 by 32 groups | Every XOR-transformed word |
| 9 | 65,536 invocations, eight independent FP32 accumulators, 64 FMA steps | Every accumulator, 128 retained vector FMA instructions |
| 10 | 65,536 invocations, 64 serial divide/root pairs | Every result, 64 retained divides and roots |
| 11 | One unblended fullscreen triangle writing the four scene MRT formats | Every color, normal, depth-color and motion texel |
| 12 | Eight fullscreen alpha-blended, depth-tested layers, same MRT and one sample | Blended color, preserved attachments and depth |

The matched chain comparison is combined dispatch/dependency overhead. It is not an isolated barrier
measurement. The workload uses the seam's native resource-ordering implementation without extra
submissions or explicit barrier calls.

The streaming numerator includes reads plus writes, 256 MiB per control 7 frame and 64 MiB per control 8 frame.
FMA counts two FP32 operations per lane. Divide and root work has its own instruction class and cannot
borrow an FP32 FMA rate. Definitions retain
dispatch groups, local sizes, formats, samples, dimensions, seeds, serial edges and source/compiler
artifact hashes. Exact shader bytes use LF on all checkouts. Final output hashes describe the actual
checked outputs and remain separate from the work definition digest.

Arithmetic controls hash every uploaded seed byte. Generation, cold upload and output validation share
the same private read-only seed data. A change to any input bit changes the authorized work identity.

`RendererPerfControlShaderTests` validates SPIR-V, HLSL and MSL without a device and checks retained
stores and arithmetic. The existing D3D11 validator also runs FXC when the test executes on Windows.
The corpus describes intermediate compiler work, not physical registers, runtime occupancy or native
instruction scheduling. Native correctness and achieved rates require the separately approved pilot.

`RendererPerfFloorModel` takes every hardware rate as an explicit sourced input. No hardware rate is
embedded as measured throughput. It uses the maximum of overlapping resource/launch terms and adds
launch cost only for an explicitly disjoint serial phase. Peak bounds, calibrated references and
observed GPU durations are separate values. Unknown traffic remains incomplete. A duration below a
claimed minimum invalidates that model, preserving the negative excess.

Floor component keys are `readBytes` and `writeBytes` in bytes, `fp32-fma` in operations and
`pixels` in pixels. Other instruction mixes use `operations.<class>`. `gpuEligible = 0` identifies
CPU-only ownership and `execution = 0` identifies a skipped occurrence. Recorded `target.*` and
`dispatch.*` components describe requested resources and group shapes. They do not establish traffic.
Invalid references remain null metrics with reasons. `ClaimedPeakLowerBoundNs`, `UnclampedExcessNs`
and `CalibratedRange` retain the diagnostic arithmetic separately.

Recipe work models retain one assessment for every frozen registry occurrence. Exclusive copy and
resolve payloads supply separate read and write components. Texture formulas include mip levels,
layers, samples and format sizes. The mip reduction helper is explicitly a four-tap logical model,
not exact off-chip traffic. Geometry and alpha paths retain invocation ranges. Shader, cache, format,
parent, CPU-only and new unknown owners name the evidence still needed instead of inventing a floor.
The [renderer performance reference](../../../../docs/RENDERER-PERFORMANCE.md) owns offline analysis,
reference matching and comparison policy. Task 15 owns native pilot checks and publication integration
with the existing metric-vectors/2 artifact.
