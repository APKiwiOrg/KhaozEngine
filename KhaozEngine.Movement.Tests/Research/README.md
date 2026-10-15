# Navigation capture research

`NavCaptureResearch.Run(setup, outputDirectory)` is an opt-in test utility for one
`PhysicsNavBake.Capture`. A caller supplies a factory returning its owned physics,
context, options and classifier. The utility invokes the factory once, captures
once, writes diagnostic parity bytes and disposes the context and owner. It does
not warm up, repeat captures or derive navigation profiles.

Keep caller adapters, private inputs and all outputs outside the repository when
they contain private data. The committed smoke uses a four-column engine fixture.
The helper itself has no default world or asset paths.

Build the caller's probe in Release without running it first. Use `build-slot`
for every local build, test and capture, with `--heavy` for the capture. A profiler
can launch the built probe suspended so that tracing is active before capture.
For an installed `dotnet-trace`, select the available sampled-thread-time and GC
profiles and add provider `KhaozEngine-NavCaptureResearch`. Its `Stage` events mark
setup, capture and snapshot boundaries. Do not install tooling or repeat a capture
to repair a failed trace.

`metrics.json` records separate setup, capture and snapshot wall seconds, process
CPU seconds, total managed allocation, current-thread managed allocation and GC
collection deltas. The process counters include runtime and profiler activity.
The wall interval excludes counter reads. CPU and allocation intervals include a
small amount of measurement overhead. Precise total allocation reads synchronize
runtime counters. Native allocations are outside these counters. These are cold,
single-observation measurements rather than performance budgets.

`options.json` preserves capture configuration. `columns.bin` starts with the
BinaryWriter length-prefixed string `PCAP1`, width and height as Int32, then origin
XYZ as Single. Each column in Z/X order stores an Int32 surface count and each
surface's Single height, Single headroom and UInt32 area bits. An Int32 water count
follows, then each entry's Int32 cell, Single surface height and UInt32 areas. All
numeric values are little endian. `columns.sha256` contains the SHA-256 of those
exact bytes. This diagnostic snapshot is independent of the shipped KENB format.

For an approved after comparison, preserve the original input identity and
`options.json`, run once into a new directory and compare `columns.bin` byte for
byte. A changed snapshot requires investigation and owner approval before adopting
changed bake semantics. Full bake parity still needs engine fixture coverage of
profile derivation and KENB serialization.


## Restoring an existing diagnostic capture

`NavCaptureSnapshot.Restore(input, path)` reconstructs the test-only builder from
PCAP1 without calling `PhysicsNavBake.Capture` or any ground, medium or classifier
callback. It validates the supplied dimensions and origin bits, surface counts,
ascending finite heights, headroom, water ordering and complete consumption.
`NavCaptureSnapshot.Write(capture, path)` uses the original diagnostic writer, so
compare the reconstructed bytes with the saved snapshot before deriving a profile.

Restoration does not authenticate a rebuilt scene from a file alone. The caller
must first verify the original input and source digests, every option including
float bits, collider identity, provider selection, bounds and classifier semantics.
The utility captures a new basis from that rebuilt owner. A saved process-local
basis cannot be transferred to a different physics instance. Keep the rebuilt
scene unchanged through profile construction.

Only the private builder constructor uses reflection. This utility lives in the
test assembly and adds no production restore API or supported file format. A
changed constructor fails explicitly. The fixture tests preserve negative zero,
infinite headroom, area bits and water bits without sampling, then compare a
complete KENB bake from restored columns with one from a fresh tiny fixture capture.
They also refuse trailing bytes.

For downstream research, call `BuildProfile` once with the actual caller tuning,
area filter and options. It constructs its traversal graph internally. Separate
rebuild, reconstruction, validation, profile construction, bake assembly and
serialization counters. Calling `GroundNavigationBake.Create` after `BuildProfile`
would derive another profile. A private research adapter may assemble the already
built profile through reflection, provided it reproduces Create's identity and
candidate-link checks and validates load and canonical rewrite. Keep this adapter,
raw traces, reconstructed columns and bake bytes outside the public repository.
