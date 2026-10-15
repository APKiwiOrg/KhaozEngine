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
