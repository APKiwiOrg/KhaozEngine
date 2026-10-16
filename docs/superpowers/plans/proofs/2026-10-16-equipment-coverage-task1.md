# Equipment coverage Task 1 checkpoint

Task 1 of the [approved plan](../2026-10-16-equipment-coverage.md) is implemented and independently
reviewed over `87e97c463..b4664f739`. The implementation and correction commits are `7a2d37031`,
`184bfa4ac` and `b4664f739` on `feature/equipment-coverage`.

The checkpoint provides immutable host and equipment sidecars, strict format 1 canonical JSON,
domain-separated identity and the failure vocabulary. Failure equipment instance IDs are `ulong`.
Schema-aware streaming preflight checks entry limits before DOM construction. New public model
constructors bound collection snapshots before copying. Attachments codecs share their internal
member-validation helpers while preserving existing v1/v2 bytes and identities.

## Verification

- HostCoverageMapTests: 80 passed, zero failed or skipped, build-slot exit 0.
- EquipmentCoverageJsonTests plus existing JsonTests: 145 passed, zero failed or skipped,
  build-slot exit 0.
- Both green logs contain no warnings or errors. Meaningful red tests exposed the identity,
  late-capacity and unbounded-constructor defects before their corrections.
- Both affected-project changed-file format checks passed. Dash, prose, file-size, contributor
  instruction, documentation-version and commit-hook checks passed. Existing lower prose-baseline
  advisories were left unchanged.
- Independent task review found two Important issues, late JSON allocation checks and duplicated
  Attachments helpers. The scoped re-review confirmed both fixes and the bounded constructor paths,
  with no new Critical, Important or Minor findings. The controller checked the commits, selected
  source paths and actual focused-test output, then verified the pushed branch equals the commit.

The focused commands were:

```bash
build-slot --label coverage-preflight-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~HostCoverageMapTests
build-slot --label equipment-preflight-green -- dotnet test KhaozEngine.Attachments.Tests/KhaozEngine.Attachments.Tests.csproj -c Release --filter 'FullyQualifiedName~EquipmentCoverageJsonTests|FullyQualifiedName~KhaozEngine.Tests.Attachments.JsonTests'
```

## Remaining gates

This is a schema checkpoint, not a complete rendering capability. Source-dependent topology,
core/boundary strengths, support geometry, refinement limits and runtime compatibility remain
with later tasks. Full solution, native GPU and cost acceptance have not run for this capability.
Scalar text and total input-byte limits are not specified by format 1. The implemented limits
bound structural entry counts and snapshots, not arbitrary scalar text length.
