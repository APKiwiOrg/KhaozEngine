# SnapshotTool

The canonical reference for the KhaozEngine snapshot harness: a runnable one-2D-one-3D acceptance
sample, and the exact shape every game's `tools/SnapshotTool` mirrors. The whole `Program` is
`SnapshotHost.Main(args, Register)`, so the engine owns arg parsing, the `<temp>/ke-snapshots` default
output dir, and the summary, and the tool is just the shots.

See [`../KhaozEngine.Snapshot/README.md`](../KhaozEngine.Snapshot/README.md) for the harness API
(`SnapshotRunner` / `SnapshotHost`, and the `Shot3D` extension in `KhaozEngine.Snapshot.Render3D`).

## Requirements

- .NET 10.
- A GPU device (Veldrid/Metal). The captures run offscreen (no window) but still need a real device, so
  this runs on a dev box, not in headless CI.

## Run

```bash
dotnet run --project SnapshotTool -- /tmp/ke-snapshot-demo
```

The argument is the output directory. With no argument it writes to `<temp>/ke-snapshots`. Each shot
logs its path; a `done -> <dir> (N shots)` line ends the run. Writes `hello2d.png` (flat rects) and
`hello3d.png` (a lit box).

## Compare commands (`diff` / `score`)

Two GPU-free subcommands built on `KhaozEngine.Imaging.GoldenGrid` compare renders from the command
line without touching xUnit. Both exit `0` within tolerance, `1` over tolerance, `2` on a usage or IO
error, and print the worst diff, the offender count, and the top 8 cells like a golden-test failure.

```bash
# Compare two rendered PNGs (default grid 32x18, tolerance 0.06). --out writes a per-cell heat map PNG.
dotnet run --project SnapshotTool -- diff a.png b.png --tolerance 0.06 --grid 32x18 --out heat.png

# Score a rendered PNG against a committed golden grid txt (dimensions read from its header).
dotnet run --project SnapshotTool -- score render.png ../KhaozEngine.Tests/Gpu/goldens/scene3d.metal.txt
```

`diff` requires equal dimensions; `score` reads the golden's `WxH` header and downsamples to match. The
command logic (`DiffCommands.Diff`/`Score`) is factored as argument-array to exit-code with an injectable
log sink, so it is headless-testable without spawning the process. Any other first argument runs the
default render form above, unchanged.
