# Private input probe on released 21.21.0

Never merge this archive into main, release it, pack it, modify a feed or adopt it through a game pin.
This carries the approved private prepare split and passive recorders onto released runtime source
`84d8fb7f2b34882eb387e252b4058f155ba5700c`. It measures that private split, not the production frame policy.
The engine version and changelog remain 21.21.0. The previous private source and evidence stay frozen.

## Source and preparation identity

The distinct purpose is `paused-sun-input-stability-release-21.21.0`. Its exact input contract is:

```json
{
  "purpose": "paused-sun-input-stability-release-21.21.0",
  "schemaVersion": 5,
  "framebufferWidth": 1591,
  "framebufferHeight": 1027,
  "recordingSlots": 3,
  "baseFrames": 3,
  "drawables": 3,
  "retirementCap": 8,
  "mode": 1,
  "vsync": true,
  "softwareCapHz": 0,
  "preparePhaseSplit": true,
  "rendererInitializesSwapchain": true,
  "engineBase": "84d8fb7f2b34882eb387e252b4058f155ba5700c",
  "gameCommit": "bfde827b4f959cbdbfd44c14e39ec85b7c35fbdc",
  "engineSourceVersion": "21.21.0",
  "assemblyVersion": "21.21.0.0"
}
```

The accepted private `probeCommit` must name all five replacement informational versions as
`21.21.0+<probeCommit>`. Gpu, Gpu.Metal, Windowing, Render3D and Game.Render3D retain assembly and file
version 21.21.0.0, including their engine references. The game informational version names the frozen
game commit. The new profile requires exactly 661 runtime files and 411 Content files, derived from
the audited pristine published client manifest. No placeholder files satisfy that audit. The collector
still rehashes every declared byte and mode before and after capture and refuses links.

The named world selection retains all 40 directory files and both separate catalogs. At the new game
commit, the sorted `path SHA256` LF fingerprint is
`0e818bed9e529ae826b20c4994fb8a2250e5764559bb41613eb233930b35263d` for the world and
`786b926e58dafdad62ae6845b82bc8a228de05043c87ab1f87cf0ee8a4e030cc` for the catalogs. Exact source,
directory and payload checks apply. The old purpose still selects its old base, versions, counts and
world fingerprints. Neither purpose can substitute the other's identity. Schema 4 remains legacy only.

## Released consumption sites

The real GameApp3D probe caller forwards the current positive frame dimensions through
`PrepareScene` to `PrepareFrame(width,height)`. Bound water prepares its targets and optical bindings
before either recording opens. Repeated preparation remains a no-op with the released target checks.
Resizing prepares the new viewport on the next scene frame.

Cascade fitting, geometry and culling retain their released owners and arithmetic. `camera.eyeWords`
copies the existing render-relative eye local consumed by the fit. `view.renderOriginWords` states
that coordinate frame. `absoluteFitWords` copies the released absolute diagnostic matrices derived
from the relative fits before GPU clip correction and atlas transforms. Those absolute matrices are
diagnostic views, not the matrices used by released caster culling. Culling continues to reduce bounds
and consume the relative fits. No extra camera or light getter, matrix calculation or renderer mutation
is introduced by observation. Origin-zero observations retain the existing absolute/relative equality.

The released headless diagnostic settings retain precedence over ambient base-frame configuration.
An enabled recording-capacity override still requires the resolved base to be 3. Recording storage
uses 3 or 6, native drawable buffering stays 3, and scene retirement stays 8. No native frame policy
changes when the override is absent.

The prepare split retains the atmosphere, final-composite and temporal-debug uniform refreshes in
their consuming recording. Released pass-observer failures and safe dependent disposal remain in
force. Submission, capture, presentation and abandonment keep their released ordering.

## Collection and proof boundary

Use the existing one-shot collector with the new explicit purpose and a fresh preparation, result,
startup log and archival run identifier. The initial stability window still requires counts, slots 3,
1591x1027, VSync on, cap off, prepare split and renderer-owned output. The five canonical helper
identities are unchanged in shape. Strict integer/Boolean parsing, no-follow nonblocking admission,
fresh trigger consumption, immutable metadata finalization, immediate archival and archive-only
recovery retain their existing predicates. No automatic collection or retry is authorized here.

Focused CPU, allocation, Python and strict validated Metal proofs cover the port. Native comparison
retains exact eight-frame output and same-frame capture bytes at both recording capacities, across
Off, Counts and Timings, with frame reuse and released viewport/bound-water preparation. These tests
do not establish live correctness, an FPS effect, full workload equivalence or production readiness.
Parent review, the full finish, private push and a separately audited client handoff remain required.
