# Metal frame label probe

Permanent diagnostic archive on `probe/mac-frame-labels`, based on
`66391b2d6fcadfe483849f5ffb8371bc13f55a78`. **Never merge this branch into main.**
There is no version bump, public diagnostics API or portable backend change.

Set `KE_METAL_FRAME_LABEL_PROBE=1` before starting the process with this branch's Metal DLL.
Only the exact value `1` enables it. The flag is sampled once at first native use and cannot be
changed during a process. The probe does not start or stop capture.

## Output and ordering

Every command buffer returned by the native queue gets `KE Metal buffer=N`. IDs start at one and
increase across the process. This includes setup, present and drain buffers outside command lists.
After a real native command list acquires its ring segment, its buffer label becomes, for example,
`KE Metal buffer=17 list=1 recording=9`. The list ID is assigned on its first instrumented Begin.
The recording ordinal starts at one for each list and advances on each Begin that acquires a ring
segment, including recordings later abandoned or failing after that point. Invalid nested Begin
and failed native buffer or ring acquisitions do not advance it. This is not a presented game frame
number or a submission number. Buffers outside command lists have no list or recording ordinal.

Each actual render, blit or compute encoder factory sets a label such as:

```text
KE Metal buffer=17 list=1 recording=9 encoder=83 render source=[6] KhaozEngine.Render3D.SomePass.Draw <- [9] KhaozEngine.Render3D.Scene3D.Render
```

This is an illustrative shape, not observed capture evidence. Encoder IDs increase process-wide
at the native boundary. They describe CPU boundary ordering, not submission or GPU execution order
across command lists or threads. Buffer creation IDs can have gaps between recordings. The bracketed
indices preserve the original managed stack positions, nearest caller first. Up to eight closest
frames from the `KhaozEngine.Render3D`, `KhaozEngine.Terrain` and `KhaozEngine.TileWorld` namespaces
are included, with declaring type and method names. Metal wrappers are omitted. If none survives,
`source-frames-unavailable callers=...` supplies up to three other non-Metal, non-System and
non-Microsoft managed frames, or `unavailable`. Fallback callers are not proof of a logical render
pass. Overloads are not distinguished by parameter signature.

Each actual native-device Begin emits one invariant-culture stderr line after ring acquisition:

```text
KE_METAL_FRAME_LABEL_PROBE Begin KE Metal buffer=17 list=1 recording=9 stopwatchTicks=123456789 stopwatchFrequency=1000000000 backpressureStallCount=4 backpressureStallMs=12.25
```

The timestamp is sampled immediately after ring acquisition, before labeling or writing stderr.
Divide tick differences by the reported `Stopwatch.Frequency` to get seconds. The two backpressure
values come from the existing `GpuDeviceCounters` and are cumulative for that device. Compare their
deltas to identify counted CPU backpressure waits. They are not per-recording GPU durations, and
other lists sharing the device can contribute. Zero delta does not prove zero CPU waiting elsewhere,
such as queue acquisition, drawable acquisition or stderr I/O. Fake command-list owners never enter
this path, read counters, allocate probe state or write stderr.

## Semantics, lifetime and limitations

Only the native `label` property is set. There is no encoder end, extra GPU command, debug group,
signpost, attachment mutation, shader change, resource-state change or pass reorder. Existing
entry-point autorelease pools enclose label creation and assignment. `NSString.FromManaged` returns
an autoreleased string that is never manually released or retained here. Metal copies the label.
The existing NSString helpers and nested label selectors reuse the runtime's selector cache. All
native probe bodies carry the macOS platform annotation and native entry bodies remain NoInlining.
No selector initializes on fake Begin or on the disabled path.

Collecting every encoder's managed stack, reflection, managed and NSString allocations, label
copying, atomic IDs and synchronous stderr writes add instrumentation overhead. Even disabled native
factories acquire a small flag-check call. **Use captures only for identity attribution. Do not
present instrumented FPS, frame times, stall totals or capture timings as comparable performance.**
The probe can change CPU pacing, backpressure and GC timing. A slow stderr consumer can block Begin.

Release optimization, inlining, tail calls, generated methods or stripped metadata can remove or
obscure needed source frames. No caller compilation settings are changed to preserve them. Method
names are collected without source files or line numbers. See the
[StackTrace limitations](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.stacktrace).
The label describes the call stack when an encoder actually opens, which can differ from the code
that first selected a framebuffer. Later logical work sharing that encoder retains its opening label.
Capture tools may truncate the displayed text. Missing or ambiguous source identity must be reported
as a limitation and resolved from evidence, never inferred from a generic wrapper.

## Verification boundary

The implementation worker runs only the focused Release build through
`build-slot --label metal-frame-label-probe -- dotnet build KhaozEngine.Gpu.Metal/KhaozEngine.Gpu.Metal.csproj -c Release`,
small repository guards and `git diff --check`. Build logs belong under
`/tmp/khaozengine-mac-frame-performance`. No full suite, live client, render, stress or repeated tests
are part of this probe implementation.

The parent inspects the diff and DLL hash, prepares a private SnapshotTool copy, performs one bounded
capture, and verifies actual command-buffer labels, all three encoder kinds where present, useful
Release source frames and matching Begin counter lines. None of those native outcomes is established
by a successful build. The parent archives and pushes only after that evidence. This worker does not
push, merge, tag or change another checkout.

## Observed native attribution

A bounded Metal System Trace capture with probe code `8c0004e8b` completed successfully on an
Apple M2 Max. The private client differed from its stock copy only in `KhaozEngine.Gpu.Metal.dll`.
All 60 selected render recordings, 157 through 216 on list 2, contained 14 encoders. Their labelled
opening stacks were stable across the window. Recording 217 was the closing render and was excluded.

| Encoder ordinal | Observed source opening |
| --- | --- |
| 2 | `ModelRenderer.DrawShadowCasterRun` through `RenderShadowDepthPass` and `RenderCascadeShadowPass` |
| 4 | `ModelRenderer.DrawMeshInstanced` through `Scene3D.RenderInternal` |
| 9 | `TemporalSplitResolve.Run` through `TemporalResolveRenderer.RunSplit` |

The source names survived Release optimization. Native render and blit labels were observed. No
compute encoder was required by this fixture, so its label path remains unexercised. The log contained
217 Begin records for the render list and cumulative backpressure counter values. These observations
validate identity attribution only. Later logical work can share an encoder's opening label, and the
instrumented timings do not establish a frame-rate improvement.
