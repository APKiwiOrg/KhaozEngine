# MapDoc oracle harness

This non-packable test project is deliberately outside `KhaozEngine.slnx`. No engine or test
project references it. Full and selective public CI must never select it. Architecture tests
enforce those boundaries, including the permitted test-only TileWorld dependency.

## Public format-4 recorder

The recorder uses only synthetic fixtures from `KhaozEngine.MapDoc.Tests`. It records released
resolver-v1 placements, callback coordinates and exact float bit patterns before the format
changes. Confirm the resolver source still matches the released R1 implementation before recording:

```bash
git diff a87038f5a HEAD -- KhaozEngine.MapDoc/MapResolver.cs KhaozEngine.MapDoc/MapAuthoredIdentity.cs
```

Use the shared verification slot assigned by the scheduling owner. From the repository root:

```bash
dotnet build KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj -c Release -m:1
KHAOZ_R2_RECORD_DIR="$PWD/KhaozEngine.MapDoc.Tests/Fixtures/FormatFour" dotnet test KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj -c Release --no-build --filter FullyQualifiedName~FormatFourRecorder --logger 'console;verbosity=detailed'
dotnet format KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj --verify-no-changes --no-restore
```

Recording requires `KHAOZ_R2_RECORD_DIR`, format 4, and an absent or empty destination. It refuses
to overwrite a recording. Missing inputs fail rather than skip. Run the recorder once, pin its
printed digest in `FormatFourFixtures.PinnedFixtureDigest`, and commit the synthetic output.
Do not regenerate the recording to make a later compatibility test pass.

The digest covers every recorded file in ordinal relative-path order. Paths use `/`, and each
UTF-8 manifest line is `relativePath TAB lowercase-sha256 LF`. The manifest itself is not a file
in the fixture directory. Expectation tests read the committed copy from their output directory.

## Private oracle boundary

KhaozEngine is public and its shipped-source oracle is private. These rules restate the D1 boundary the
harness enforces:

- Public CI runs only synthetic fixtures. This project is the one place the exhaustive shipped comparison
  runs, outside `KhaozEngine.slnx`, and public PR CI never sees its credentials, the private checkout or this
  project.
- Every private test body runs inside `PrivateOracleEntry.Run`, the one entry boundary. It reads exactly the
  four named variables, validates them, creates the report securely, runs the body and maps every exception.
- Missing or unsafe inputs fail and are never skipped or waived.
- The report is created securely or the body never runs.
- Every failure route is sanitized. Bodies use only `report.Check` and `report.Record`, never `Assert` on
  source-derived values, `ITestOutputHelper`, the console or `[InlineData]` with source data.
- Nothing private enters this repository, its CI or its logs of record: no source bytes, per-path
  provenance, coordinates, derived geometry, counts, digests of private content or detailed reports. The
  engine records only pass or fail and the private record reference.

The private oracle entry is separate from the public recorder and uses Unix permission checks.
It has no discovery-time input reads. Never commit private shipped
source, derived geometry, coordinate inventories, private digests, snapshots or detailed reports
to this public repository. Do not add private credentials or source access to public CI.

Only approved public synthetic fixtures may be recorded here. The synthetic guard cases use a
deliberate public secret marker and temporary files, not a shipped world. They can be selected with
`FullyQualifiedName~PrivateOracleGuardTests` after a separately scheduled build/test window.

Actual private acceptance reads exactly `KHAOZ_R2_SHIPPED_SOURCE`, `KHAOZ_R2_ORACLE_PROVENANCE`,
`KHAOZ_R2_ORACLE_PROVENANCE_SHA256` and `KHAOZ_R2_ORACLE_REPORT`, in that order. The provenance
digest is checked before parsing. Its relative path list and aggregate digest bind the extraction.
The inventory chooses exactly one attested `world.json` path, without a game-specific directory or
unattested search. An untagged checkpoint is represented by an explicitly empty `tag` with its
full commit recorded. Comparison, units and oracle-equivalence metadata may be structured JSON.

Reports must be outside a git work tree, in an existing directory with mode `0700`. They are
created exclusively with mode `0600`. Source details and exception messages belong only in that
report. Failure messages expose only fixed reasons, category/count and the report digest, with
no inner exception. The entry owns report completion. Bodies call `Record` and `Check`, leaving
`ThrowIfAnyFailed` and disposal to the entry. Premature disposal followed by failure is a closed
report error and fails with the fixed secure-report message. Final report links are refused,
including dangling links. Parent directory links are resolved before containment and mode checks.
Missing or mismatched private inputs fail, never skip. Actual private source
selection, extraction and execution belong to the controller's separate acceptance step.

## Exhaustive terrain comparison

`ShippedTerrainComparisonTests` enters through `PrivateOracleEntry.Run`. `ShippedSourceInventory.ReadRegions`
loads the world named by the single attested `world.json` path under the verified extraction root and returns
its regions in signed region order. `ShippedTerrainComparison.CompareRegion` links the public
`LegacyOracleConverter` and calls only its assertion-free members. For every region and every plane it
checks, through `report.Check` only:

- exact corners against the stored lattice, with derived upper planes recorded as derived
- exact cell bytes, including the reserved Bridge bit
- equal legacy triangle and native support-face counts, every vertex within 0.00001 m and every normal
  component within 0.000001
- the legacy fallback cell set
- shared corners across every signed region seam
- no topology records

The document must use tile size 1 and four planes. The provenance `comparison` object must name
`spikePoint` as `{ "worldX": <metres>, "worldZ": <metres> }` on plane 0. An absent `spikePoint` is a
`spike-point-named` failure, and a malformed one fails the run. Each run records exactly one `spike-point`
outcome, carried by one region: no loaded region owns the point, the owning tile draws no ground, or the
point was compared. When it is compared, the native support height from `MapSurfaceCompiler.ExactHeight` must
equal the legacy plane-0 movement triangle within 0.00001 m. A world with no regions fails the run.
Per-plane counts and maximum errors are recorded only in the private report. An exceedance is a reported
failure carried privately, never a tolerance change.

The harness requires its own explicit build, test and format verification because the solution
does not include it. An unfiltered harness run is not a substitute for the separately gated
recorder, synthetic guard and private inventory runs.
