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

This project will later host the private exhaustive oracle under R2 Task 3. That private entry
boundary is not implemented or authorized by the public recorder. Never commit private shipped
source, derived geometry, coordinate inventories, private digests, snapshots or detailed reports
to this public repository. Do not add private credentials or source access to public CI.

Only approved public synthetic fixtures may be recorded here. Private acceptance requires its
separate input verification, sanitized entry boundary and private report path. Missing private
inputs must fail that acceptance gate, never silently skip. The harness requires its own explicit
build, test and format verification because the solution does not include it.
