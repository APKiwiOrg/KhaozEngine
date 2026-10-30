# Private prepare-phase submission probe

This branch is a diagnostic archive carried onto the released 21.21.0 renderer. The original 21.13.0 archive stays frozen. Never merge it into main,
pack it as a published version, or adopt it through a game package pin.

`KE_PREPARE_PHASE_SPLIT=0`, or an absent or empty value, keeps the ordinary one-recording scene path.
`KE_PREPARE_PHASE_SPLIT=1` submits the offscreen scene and post chain during the standard
`GameApp3D` prepare callback. The normal frame recording then draws the existing final composite,
scene overlays, 2D, UI and capture taps. There is no additional full-frame copy. Other values are
refused. Enabled mode requires MetalNative, renderer-owned swapchain initialization and the inherited
`GameApp3D` prepare and render callbacks.

The prefix owns a persistent command list. It closes and submits that list before the window's
recording opens. A prepared output names one scene frame, viewport size and post-target generation.
It can be consumed once. Beginning another scene frame, an ordinary render, failure or disposal
invalidates it. The final blit uploads its frozen uniforms in the recording that consumes them,
because Metal uniform segments belong to recordings. Temporal debug views likewise republish their
resolve and visibility uniforms without advancing history again. Queued billboard overlays republish
the cached atmosphere payload without recalculating lighting or sky textures. Scene disposal drains pending GPU work before
the prefix list is disposed.

Vsync, software caps, graphics quality and drawable/ring depth are not changed. The extra recording
uses another ring segment. Ring backpressure is part of the measured result, not a cost hidden by
raising queue depth.

## Direct comparison

Use identical private client bytes for both modes, with the same world, view, window dimensions and
settings. The passive recorder is enabled with an absolute `KE_FRAME_DIAGNOSTICS_PATH` whose parent
already exists. After the client is foreground at a settled view, create `<result path>.start` from
a background command. It excludes trigger I/O, samples ten seconds or 4096 intervals, then publishes
once without overwriting an existing result. It does no synchronous readback or sampling-profiler work.

Schema 3 records actual cumulative `preparePhaseSubmissionCount` and `preparePhaseCompositeCount`
at every boundary. `preparePhaseSplitActive` becomes true only after a successful prefix submission.
`allPreparePhaseSubmissionsPaired` requires one prefix and composite per measured interval in enabled
mode and no such work in disabled mode. Mode changes invalidate the stable-window result. A prefix
count means submission returned. A composite count means its commands recorded successfully. The
next callback boundary includes the final frame submission and presentation.

Use the monotonic frame intervals for full-client cadence. GPU counters are cumulative CPU waits,
including full drawable-acquisition API durations. Neither they nor the recorder establish physical
display cadence or GPU execution duration. Keep focus, framebuffer size and actual mode stable. A
measurement from a changing or unpaired window is not a valid comparison.

Private runtime replacements are Windowing, Render3D and Game.Render3D. Audit their hashes and
assembly references, and verify every other client file against the frozen pristine client. Do not
copy freshly rebuilt unrelated engine DLLs. Debug views, resizing and capture require native proof
before this archive is handed to the owner for a comparison.

The current release identity and consumption sites are documented in
[`verification/mac-frame-release-2121-input-diagnostics-probe.md`](verification/mac-frame-release-2121-input-diagnostics-probe.md).

The private buffered shadow-stage extension is documented in
[`verification/mac-frame-shadow-stage-probe.md`](verification/mac-frame-shadow-stage-probe.md).
