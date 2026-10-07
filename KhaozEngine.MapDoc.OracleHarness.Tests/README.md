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
no inner exception. Missing or mismatched private inputs fail, never skip. Actual private source
selection, extraction and execution belong to the controller's separate acceptance step.

The harness requires its own explicit build, test and format verification because the solution
does not include it. An unfiltered harness run is not a substitute for the separately gated
recorder, synthetic guard and private inventory runs.
