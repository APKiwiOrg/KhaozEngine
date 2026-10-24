# Cold Task4 implementation report

Plan `docs/superpowers/plans/2026-10-24-renderer-cold-publication.md`, Task4.

## Starting guard and authority

Physical cwd `/Users/antonio/KhaozEngine/.worktrees/perf-baseline`, branch `feature/renderer-performance-baseline`.
The tracked tree/index were clean. HEAD and origin branch both equalled
`587eb9ed354fc12860a16f62088d24edd5426be8` before writes. Tasks1 to 3 are consumed without main reconciliation.
Staged 21.13.0 is preserved. No push, tag, release, native/window/timing/stress/paid/workflow execution,
settings change, stash, worktree cleanup or subagent occurred. Tests own and remove their temporary files.

Read the current contributor/test/allocation/security/dependency/build-CI/docs rules, canonical spec,
Task4, Global Constraints, consumed declarations, CP-1 to CP-8 and Task3 final acceptance. Earlier
reports were consulted for consumed contracts and proof limits only.

## Actual path and requirement matrix

| Requirement | Actual owner and proof |
| --- | --- |
| Producer-owned frozen expectations | SidecarRegistry Register/Freeze. Entrypoint and publication producers explicitly register manifest, authorization, runtime/native admission, shader sources, control corpus/definitions, layouts, recipes, work models and the existing host telemetry JSONL. No directory enumeration creates expected evidence |
| Inventory JSON and graph limits | CompilerJson plus value-struct converters retain source/layout/graphics inputs and all occurrences. Strict required/unknown/duplicate fields, recipe rehashing, stage validation and Task2 logical sub-budgets remain active |
| Real precollection account | PublicationWriter implements IRendererPerfUnitPublication. PrepareCollection encodes retained root/inventory and completion shape before writing admitted compiler inventory or creating a diagnostic graph |
| Incremental native admission | PublicationAccount calls Task1 ForAvailable. Collect receives the exact reservation/request, uses the actual budgeted native reader and commits EncodedResultBytes once by reservation reference identity. Outstanding failed results remain represented until terminal refusal |
| Root and compiler aggregate | Root 268435456, depth32, unchanged 16777216 completion reserve, conditional Vulkan R and fixed D3D 1048576. Actual file lengths are independently summed with checked Int64 arithmetic |
| Completion/error reserve | Four maximally escaped option tables plus actual result/association/reference cardinality are encoded before optional reads. Text projection retains exact required bytes and omission counts without changing actual Error occurrences |
| Shared file verification | ArtifactVerification reopens registered regular files with exclusive leases, safe paths, link/reparse checks, nonempty lengths and 65536-byte chunk checks/hashes. It reconciles manifest operations, independent counts, recipe/stage identities, correspondence, effective configuration and actual admitted profiles |
| Verified wrapper | Private constructor. Verify is the sole construction path. Parse returns only data. ReadDirectory, live publication and console/workflow share verification. Byte/report paths explicitly refuse promotion and comparison verdicts |
| Comparison and reference context | Verified summaries retain an internal artifact association. Comparisons recompute from it with the supplied reference rates. Occurrence role joins permit recipe split/merge while requiring layout/code-generation/settings compatibility |
| Actual failures and recovery | Typed Publish and WriteFailure return new actual events in catch order. Actual Unit tests cover final/correction/recovery, early identify/dispose/write/recovery, arbitrary original aggregates, exhausted text and retained partial evidence |
| One deadline and stamp | Both contexts are already disposed at publication. Provisional final JSON/Markdown precede live verification, completion sampling and the two reserved accounting slots. File cancellation/deadline tests exercise verification and stamp recovery |
| Trusted workflow | Existing independent Select/Authorize/TRX/execution identity checks remain. Trusted Verify rejects report/1 and calls its built managed verifier. The one fixed measurement fact, runner admission, attempt gates and 12/35-minute job caps remain unchanged |

The production Entrypoint now always constructs the concrete file PublicationWriter. Its former
injected-publication alternative and missing-publication refusal are removed. Unit still exposes its
accepted typed interface for headless lifecycle tests. Report-only compatibility overloads remain
outside production wiring.

PrepareCollection produces actual encoded inventory bytes and registration metadata, freezes the
registry, constructs the account, checks the minimum primary profile, then writes admitted inventory.
Capacity failure does not start a diagnostic device/graph and does not write an unadmitted compiler
file. The detached inventory remains retained. No raw frame or native report is dropped to reclaim room.

Publish writes an initial partial, provisional final JSON/Markdown, verifies the directory, samples
completion and stamps only two 32-byte numeric slots. Actual file failures append final, correction
and recovery events. Known failure wrappers transfer their events. Arbitrary AggregateException
instances and repeated independent occurrences are not flattened or deduplicated.

## Rulings and bounded adaptations

Ruling. A strict execution envelope binds the unchanged Task2 inventory to unit, manifest and workload.
The accepted inventory DTO has operation membership but no execution identity fields. CompilerJson
retains its declared standalone Serialize/Parse DTO methods and adds the producer file-envelope codec.
This avoids changing the consumed Task2 DTO. Cost if wrong is an internal prerelease file-codec
adjustment before native acceptance, not a public GPU ABI change.

Ruling. Compiler evidence has a required nullable accounting projection. Verification requires the
base/inventory/completion sizes and ordered admissions and re-derives requests from actual headroom.
Accepted read/result interfaces are unchanged. Cost if wrong is a report/2 projection adjustment
before native acceptance.

Ruling. Referencing Render.Tests executes three native-provider module initializers and a validation
logging initializer. The verifier executable is explicitly exempted by entry-assembly identity.
Ordinary test registration and native backend implementations remain unchanged. The executable checks
that all three provider registrations are absent before and after Verify. Actual process tests passed
with that check, without a device or performance fact. Cost if wrong is confined to the named offline
host exemption and is detectable by the process checks.

Ruling. Markdown recomputation preserves a verified association only while Raw is the exact associated
immutable report. A replaced Raw falls back to unverified descriptive analysis. This preserves stale
summary refusal without discarding verified reference context. Cost if wrong is an offline verdict,
covered by the original Markdown and comparison assertions.

## Quantitative full-fixture proof

All three variants execute real PrepareCollection, Collect, ReserveReport, CommitReportBytes and
Publish. Both complete variants pass live and offline Verify and the actual console process. The
held-error/Invalid variant retains a completed first report and a refused second report with no
forbidden native fill, preserves every raw field and refuses promotion. The original compact
held-error fixture and all its assertions remain separate.

Each variant has 16944 frames, 258 factory events, 129 occurrences (43 graphics, 86 compute), two
unique recipes, 86 bound occurrences and two attempted reports. The nonempty case has two executables
and ten typed statistics, including Boolean, signed extrema, UInt64.MaxValue and finite Float64.
Every retained frame, CPU span, command metric, pass, unavailable field and error is compared to the
recorder-built input. Clean and held-error fixtures are generated separately. Retained data is not
pruned after admission.

| Variant | Encoded base | Inventory file | Completion shape | T per read | R per read | Committed bytes | Root file including padding | Combined actual bytes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Complete nonempty | 199541974 | 282181 | 7093453 | 12462, 12461 | 51830784, 51826688 | 4832, 5263 | 199552584 | 199834765 |
| Complete Unsupported | 199541975 | 282181 | 7093454 | 12462, 12462 | 51830784, 51830784 | 2357, 2776 | 199547624 | 199829805 |
| Held-error/Invalid | 199757075 | 282181 | 7093481 | 12410, 12409 | 51617792, 51613696 | 4832, 2800 | 199765255 | 200047436 |

Committed bytes follow the consumed EncodedResultBytes contract, actual report/read encoding plus
its 256-byte syntax allowance. They are not claims that all reserved bytes were materialized.
Prior-result charges are cumulative and counted once. Each T/R is independently checked against
literal arithmetic and Task1's independent maximal-escaping proof is reused for actual derived
requests, in addition to T=0,1,12448,16384.

Historical accepted base 199883475 is retained as history. The old contradiction remains
`199883475+75497472+16777216-268435456=23722707`, with old threshold 176160768. The current unchanged
original compact fixture serializes to 199826931 bytes under the current schema. No obsolete size
was forced onto the new schema and no workload shrink or cap relaxation was used.

Real files are retained under `/tmp/khaozengine-renderer-cold-publication/task4/full/` in
`complete-small`, `complete-unsupported` and `held-invalid`. `full-proof.json` contains their complete
measured section/account/request/count/commit/file data. `full-evidence-audit.log` independently
matched actual file lengths and streamed hashes, decoded inventory counts and admission arithmetic.

## Red, failures and correction evidence

All exact argv, physical cwd, integer exits, environment gates, log lengths and SHA256 values are in
`/tmp/khaozengine-renderer-cold-publication/task4/commands.jsonl`. TRX and original logs remain intact.

- Behavioral red Parse_is_not_directory_verification failed expected false, actual true.
- Behavioral red Previously_omitted_original_text_keeps_exact_required_bytes expected 18000004,
  actual 18000063. Green preserves the exact original requirement, excludes the omission token from
  event accounting and retains an ordered prefix when earlier text was omitted.
- Behavioral red Preparation_capacity_refusal_precedes_compiler_file_write proved refusal still
  wrote a 201 MiB compiler inventory. Green admits before compiler-file IO. The source graph is exactly
  at its 33554432 logical source partition and the retained root includes 52428800 provenance bytes.
- The first implementation compile used an intermediate source snapshot missing ExpectedSidecars.
  Another run exposed missing graphics fixture descriptor arrays and a recursive bounded-stream
  override, aborting its process. These are retained failures, not acceptance evidence.
- Intermediate builds also exposed a throw-expression precedence error and a fixture local-name
  collision. Both were corrected. A git diff check exposed one PowerShell trailing space, removed.
- The first focused Task4 selection had 101 passed, two failed, zero skipped. All three full variants
  and the original compact fixture passed. The failures were Markdown association loss and an alias
  test reaching its length guard first. Both corrected tests then passed. No unchanged full fixture
  was run as a red selection or stress loop.
- Core typed publication/process selection passed 29 cases. Omission correction passed one case.
  Capacity correction and complete/Unsupported controls passed three cases.

## Compatibility and living documentation

Report/2 and metric-vectors/2 remain strict. Report/1 is refused before missing new-schema fields can
obscure its unsupported-version cause. No migration or grandfathered native baseline is introduced.
Typed scalar precision, stage aggregates, raw flags and inventory membership are retained.
Sidecar checksums never replace actual artifact digests. Metal's unavailable artifact is null with
Unsupported status and a cause, while MSL remains source. Current resource counts and actual backend
peak payload remain distinct, with no PeakLiveResourceCount field.

GPU and Vulkan/D3D11/Metal READMEs, USING-KHAOZENGINE, RENDERER-PERFORMANCE, CROSS-PLATFORM and the
renderer verification document describe the final file/account/reader path and remaining native gates.
The optional console is outside the solution and references Render.Tests using the planned internal
assembly access. No package dependency, ABI, version, manifest authority or spend policy changed.

## Finishing gates

All three final Release builds passed with zero warnings/errors. These are the complete solution,
`tools/Renderer.Perf.Tests` and `tools/Renderer.Perf.Verify`. The measurement fact was built only.

After the admission-before-write correction, the final prescribed focused selection passed 104,
failed 0, skipped 0. The direct affected protocol/construction/lifecycle/backend selection passed 476,
failed 0, skipped 0. This final focused run includes all three full fixtures and the original compact
fixture. Repeating that selection was justified by the actual compiler-file admission-order change,
not a flake or stress loop. Final artifacts are retained under `task4/full-final/` and compact evidence
under `task4/compact-final/`. Earlier files remain preserved under `task4/full/`.

Whole-solution diagnostic format verification passed, formatted 0 of 9131 files, with no workspace
warning/error in that final log. The earlier apply passes emitted the disclosed generic workspace
loading warning. The outside verifier format check passed, formatted 0 of 3 files. Only whitespace
changed after the final builds/tests, with the final formatter verifying the resulting sources.

Whole-tree dashes, prose, file size, instruction budget, doc versions and diff checks passed. File size
reports only the pre-existing smaller-baseline advisory for Scene3D, 2148 versus 2228. No baseline grew.
Instructions are 8310 of 16384 bytes. All guarded version declarations remain 21.13.0. Actionlint returned
raw exit 0 for both the exact starting workflow and changed workflow, with no diagnostics in either.
`actionlint-comparison.json` retains the actual comparison. No nonzero lint result is called green.

The full ordinary suite remains reserved for root after later main reconciliation. No main
reconciliation is performed by this worker.

## Precise proof limits and handback

These are headless implementation and synthetic accounting proofs on the dev Mac. They do not prove
native primary-backend correspondence, complete real compiler admission, real vendor payloads,
unchanged native deadline/cost, Windows filesystem behavior, or original armour/consumer acceptance.
Root owns reconciliation, ordinary full suite, actual-evidence audit, fresh whole-amendment review and
push. No manual validation is needed for this headless implementation. No native run is authorized.
