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

## Fix round1

### Guard, ownership and finding matrix

Resumed at physical `/Users/antonio/KhaozEngine/.worktrees/perf-baseline`, branch
`feature/renderer-performance-baseline`, clean tracked tree/index and exact HEAD
`3086ee6f6307cbb4a1abbcb0f818a12fe53e9c58`. Origin remains accepted/pushed
`587eb9ed354fc12860a16f62088d24edd5426be8`. Read the full r0 review, original brief, current
rules, canonical Task4 and CP-7/CP-8. Accepted Task1/2/3 contracts are retained.

The preceding 15305 bytes are an exact preserved prefix with SHA256
`a8554593181f2e59118c9b7ab0844e0959d7e257b431fc78726d17e36821e6f4`.
All new evidence is under `/tmp/khaozengine-renderer-cold-publication/task4/fix-r1/`.
Original Task4 logs, TRX, source manifests and payload directories remain untouched.

| Finding | Final owner and proof |
| --- | --- |
| R0-1 ordinary setup | `scripts/build-release-tests.sh` builds the selected Release target, then the outside-solution verifier for the solution or Render.Tests. Full `ci.yml`, full/selective `ci-selective-test.sh` and documented local setup consume this owner. Fresh-output red/process green and bounded real-script orchestration fixtures prove the prerequisite without broadening selected tests |
| R0-2 handle identity | ArtifactIdentity obtains actual handle and no-follow entry IDs. ArtifactFileSet retains directory/file handles, refuses duplicate file IDs and rechecks all files and directories before certification. Deterministic root/inventory/producer replacement, earlier-file replacement, root replacement, hard-link, symlink/reparse/escape and unavailable-identity cases exercise the actual reader |
| R0-3 final completion | Typed PublicationWriter owns the existing `publication.return` check after stamp.after, inside its final/correction/recovery catch. Unit only transfers the typed result and new events. Actual Unit/file writer expiry/cancellation cases prove failed stored JSON/Markdown and actual ordered Error identity, including correction/recovery errors |

### Supported build and fresh-output proof

The supported local setup is `sh scripts/build-release-tests.sh KhaozEngine.slnx`, then the ordinary
Release `dotnet test --no-build` selection. Passing the Render.Tests project supports the affected
assembly route. The verifier stays outside the solution and keeps its one-way reference to Render.Tests.
There is no reverse/circular reference, test-time build, missing-executable skip or stale-output fallback.
Other selected test projects retain their original restore/build/test selection.

Before the red, the entire existing verifier bin/obj directories were moved to owned
`verifier-original-bin` and `verifier-original-obj`, preserving all 438 files. The exact original
paths, lengths and SHA256 values are in `starting-manifest.json`. No unrelated build output was deleted.
The original no-build console fact failed because its required DLL was absent, exit 1. The corrected
supported solution setup built both solution and verifier from that absent-output state, exit 0,
zero warnings/errors. The subsequent 17-case boundary/process selection passed with zero skips.
This includes actual console argument/refusal checks and valid/corrupt directories with no providers.

The bounded shell fixture executes the actual full workflow build command and actual selective
script through stubbed dotnet/git command boundaries. It covers full fallback, selected Render.Tests,
an unrelated selected assembly and prerequisite failure before tests. The full/determinism workload
was never executed locally by these fixtures. The real fresh build/process proof is separate from
those orchestration fixtures. Local instructions, contributor build rules, selective design and the
living renderer contract point to the shared setup.

### Identity guarantees, sources and limits

The old FileShare.None lease assumption is removed. Native regular-file/directory kind and identity
are established before allocating file payloads. IDs are independent of advisory lock availability.
Handles remain alive through final validation, preventing reuse of the opened file's identity.
Unix component opens and entry queries are relative to retained directory handles and refuse links.
Windows uses disk-file IDs and reparse-point handles, with retained parent identities. Every registered
file is rechecked, including earlier-read root/inventory files. Root replacement also refuses.
The unchanged 65536-byte chunk, before/after budget, length, hash, schema, decoded count, stage,
recipe and correspondence checks still run through the shared live/offline/console reader.

- macOS arm64/x64 uses openat O_NOFOLLOW/O_DIRECTORY, fstat/fstatat and the LP64 144-byte stat ABI.
  x64 selects INODE64 symbols and arm64 uses the sole 64-bit ABI. The local C ABI executable checked
  size 144, device offset 0, mode offset 4, inode offset 8, nofollow 0x100, directory 0x100000,
  close-on-exec 0x1000000, nonblock 4 and AT_SYMLINK_NOFOLLOW 0x20, exit 0.
- Linux x64 uses the fixed 256-byte statx UAPI, requires returned TYPE/INO mask bits and uses
  device-major/device-minor plus inode. It uses AT_EMPTY_PATH for handles and AT_SYMLINK_NOFOLLOW
  for entries. Other Linux architectures and unavailable statx facilities explicitly refuse.
- Windows uses GetFileInformationByHandleEx FILE_ID_INFO (volume plus 128-bit ID),
  FILE_ATTRIBUTE_TAG_INFO, disk-file type and OPEN_REPARSE_POINT/BACKUP_SEMANTICS handles.
  Unsupported identity facilities refuse. Size, timestamps, path text and a lucky reopen are not IDs.

Primary definitions are the [pinned .NET10 Unix handle owner](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs),
[Apple rename contract](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/rename.2.html),
[Darwin stat ABI](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/stat.h),
[Darwin fcntl flags](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/fcntl.h),
[Linux statx ABI](https://github.com/torvalds/linux/blob/v6.12/include/uapi/linux/stat.h),
[Linux open flags](https://github.com/torvalds/linux/blob/v6.12/include/uapi/asm-generic/fcntl.h),
[statx API](https://man7.org/linux/man-pages/man2/statx.2.html),
[Windows query API](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex),
[FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info),
[FILE_ATTRIBUTE_TAG_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_attribute_tag_info)
and [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew).
`platform-sources.json` records these sources and hashes preserved local SDK declarations.
The optional Python raw-source archival attempt returned HTTP 429, exit 1. The web reads and local
SDK/ABI proof supply the source evidence, not that failed download.

The held tests replace different regular bytes of identical length after the actual opened-handle
boundary. Additional cases replace a file already read before the terminal certification checks.
Every case now refuses. The unavailable platform seam preserves the same original refusal object
and has no fallback. Existing link/reparse/escaped-parent and strict decoded-evidence assertions remain.
The test boundary callback observes existing budget checks. It adds no clock origin or native work.

The guarantee is bounded to final identity validation. It is not a filesystem transaction or a freeze
against subsequent mutation. Receiving/offline/workflow owners must repeat verification on received
files. These local executions prove macOS behavior. Windows/Linux code and ABI sources were checked,
but neither platform path was executed locally. No Windows/Linux runtime proof is claimed.

### Final completion and actual failure transport

The `publication.return` check remains after stamp.after with the original budget, coldStart,
absolute deadline and cancellation token. It is now inside the writer's existing catch. There is no
new clock sample origin, relaxed deadline, publication retry or successful second completion.
Unit performs only result/event transfer after this owner returns. It has no later deadline check
that could fail outside disk correction. The interface comment records this completion ownership.

The real Unit/file-writer tests hold a successful stamp.after observation and expire or cancel at
the next enforcement. The original timeout/cancellation is the exact same object in the writer's
new-event result and UnitOutcome. Later real correction/recovery write sentinels appear exactly once
by ReferenceEquals in chronological final/correction/recovery order. The timeout message names the
retained publication.return check. Cancellation retains the original token. Stored final JSON and
Markdown say Failed and nonpromotion. Raw block/pipeline arrays remain retained from the successful
initial partial. Existing early failure, preidentity, exhausted failure-text and pre-stamp interruption
cases remain in the final selection.

Those tests also exposed a required admission handoff fix. The driver's default failure-text metadata
was incomplete with required bytes zero. Unit later canonicalized empty failures to complete with six
required JSON bytes, after precollection had frozen a different base. Unit now projects that same
empty failure evidence before PrepareCollection. This fixes the actual Unit/file-account path without
changing a schema, cap, profile, native identity or workload. The successful stamp assertion proves
that the real account and directory verifier agree before the post-stamp failure is injected.

With the held clock, cancellation retains elapsed/cost zero. Expiry retains 600000 milliseconds and
0.52 USD from the existing 0.052 USD/minute rate and original 100-tick origin. Correction/recovery
errors retain these clock semantics. Failure writes may themselves fail, so bounded recovery is not a
guarantee that arbitrary failing storage can persist every later error. Original actual write-failure
and text-capacity tests remain authoritative. The existing partial-write accounting issue #1547 stays open.


### Remeasured payload and verification evidence

The final affected run retained all 16944 frames in each variant through real PrepareCollection,
Collect, reservation/commit, Publish and Verify owners. The complete variants passed actual console
verification. The original held Invalid variant retains raw errors and partial results and refuses.
The measured section/request/commit/root sizes are unchanged from the prefix, observed again rather
than forced by obsolete constants. New files live under fix-r1/full, never the original payload paths.

| Variant | Base | Inventory | Completion shape | T | R | Committed | Root with stamp padding | Combined |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| complete-small | 199541974 | 282181 | 7093453 | 12462, 12461 | 51830784, 51826688 | 4832, 5263 | 199552584 | 199834765 |
| complete-unsupported | 199541975 | 282181 | 7093454 | 12462, 12462 | 51830784, 51830784 | 2357, 2776 | 199547624 | 199829805 |
| held-invalid | 199757075 | 282181 | 7093481 | 12410, 12409 | 51617792, 51613696 | 4832, 2800 | 199765255 | 200047436 |

The independent audit reopens real files, streams hashes in 65536-byte chunks, decodes actual
inventory totals and checks literal headroom/T/R/commit arithmetic. Each inventory still has 258
factory events, 129 occurrences and two recipes. The existing original 199883475-byte history,
23722707-byte contradiction and 176160768 threshold remain in the exact prefix. No cap, schema,
reservation formula, raw frame field, compiler identity, pipeline or native implementation changed.

All registered exact argv, physical cwd, environment gates, integer exits, log lengths and SHA256
values are in fix-r1/commands.jsonl. It includes full expanded test filters rather than abbreviated
commands. The cwd throughout is the physical guarded worktree. TRX retains actual process argv,
stdout, exits, held-boundary observations, Error checks and FULL_PROOF rows.

- fresh-red refused its absent verifier DLL, exit 1. orchestration-red also exited 1.
- The initial identity red had four false certifications and one hold that did not trigger. The
  corrected deterministic boundary test reproduced all five false certifications before the fix.
- The initial Unit reds exposed the precollection failure-text mismatch before reaching stamp.
  return-boundary-red after that handoff correction reached a successful stamp, then failed because
  Unit alone owned the new failure. Its write-error cases expected three events and observed one.
  Some initial no-write-error assertions indexed the empty writer event array. Final assertions
  explicitly check the event count before indexing. These historical failures are not called passes.
- One shell-fixture green attempt selected a nonexistent unrelated test project and failed its own
  expected-test assertion. It was corrected to the real Attachments.Tests assembly. The final fixture
  executes both actual orchestration owners and tests prerequisite failure before workload commands.
- fresh-supported-build is the actual solution plus verifier Release setup from absent verifier
  bin/obj output, exit 0. measurement-build and verifier-build-final also exit 0. All build summaries
  report zero warnings/errors. The measurement fact was built only.
- boundary-green passed 17, failed 0, skipped 0. The final focused selection passed 115, failed 0,
  skipped 0, including all three full variants and previous failure/budget assertions. The separate
  direct affected selection passed 476, failed 0, skipped 0. The full payload selection ran once
  after final behavior changes. No ordinary full suite or determinism workload ran here.


### Finishing dispositions and handback

Whole-solution format verification returned 0, formatted 0 of 9135 files. Outside-verifier format
returned 0, formatted 0 of 3 files. Neither log contains a warning/error. The scoped format apply
changed three test files only. The final expanded shell orchestration fixture and syntax check
returned 0. Final whole-tree dashes, prose, file size, instruction and doc-version checks plus diff
checking are recorded with raw exits in commands.jsonl. No size baseline or version declaration grew.

Actionlint is explicitly non-green. The exact starting CI workflow and changed CI workflow both
return raw exit 1 with the same five unknown ubuntu-26.04 runner-label diagnostics. Starting lines
37,72,207,300,394 map to current lines 37,72,208,301,395 after the added script-fixture line.
There are no new diagnostic messages. actionlint-comparison.json retains that comparison. No lint
configuration, runner admission, suppression, dependency or version was changed to hide the baseline.

source-hashes.json and audit.json identify final sources, report prefix, exact logs/TRX and actual
payload proofs. The audit also checks every original verifier backup file against starting-manifest.
The diff and source manifest hashes, final commit/parent, tracked cleanliness and unchanged origin are
recorded in precommit-proof.json and postcommit-proof.json. Normal hooks apply to explicit paths.

The living renderer/verification and local contributor/build docs describe these changes. The staged
version stays 21.13.0. Select/Authorize, fixed measurement fact, mode/spend/attempt policy, native
pipelines, measured workload, primary profiles and caps are unchanged. There was no native GPU/device,
window, timing, stress, paid, workflow or measurement-fact execution, full ordinary suite, subagent,
settings change, cleanup, stash, push, main merge, tag or release. Test setup cleaned only its own
owned temporary files. All original and new retained evidence remains available to root.

These are headless implementation and local filesystem proofs. Native correspondence, complete native
payloads, native deadline/cost, billed minutes and original armour/consumer gates remain open.
Root owns actual-evidence audit, fresh scoped review, acceptance/push and later main reconciliation
with the ordinary full suite. No manual validation is needed. Review objections require correction
and new affected evidence, not weakening these contracts.

## Fix round2

### R1-1 cause and correction

Started at physical `/Users/antonio/KhaozEngine/.worktrees/perf-baseline`, branch
`feature/renderer-performance-baseline`, clean tracked tree/index, exact HEAD
`44866b66e35060afed5e09ec4cfaea9746923594`. Origin remains
`587eb9ed354fc12860a16f62088d24edd5426be8`. Read the full scoped r1 rereview, prior correction
contract, current rules and the entire Fix round1 appendix. R0-1, the accepted replacement/alias
part of R0-2 and R0-3 remain intact.

The preceding 31759 bytes are preserved exactly, SHA256
`4050028ee7c98d84fc50f36e81d967c048cdbd7a26ff1ea6b589334ac28a21c2`.
All original and fix-r1 evidence, payloads and verifier backups remain untouched. New evidence lives
under `/tmp/khaozengine-renderer-cold-publication/task4/fix-r2/`.

R1-1 was real. Native file IDs and regular-file kind survive a same-length in-place write. The reader
had already hashed and decoded its old bytes, and its final identity-only checks could certify them
after the directory's bytes changed. Existing sharing flags and plain Unix opens did not exclude a
compatible writer. Wrapping the native handle in FileStream did not add write exclusion.

The correction adds a final full-content pass to DirectoryReader, after all decode, correspondence
and account checks and before the existing final identity/containment checks. Each successfully read
root or registered sidecar retains its actual opened stream, exact original length and original
SHA256. The verifier reads the entire retained handle again, checks length/EOF and compares the
new SHA256 with the bytes actually decoded. It never manufactures expectations from enumeration,
reopens a path for this pass, trusts timestamps, or substitutes a content hash for a real file ID.

The pass uses RandomAccess offset reads through the same SafeFileHandle. This bypasses the original
FileStream read buffer, including a file smaller than 65536 bytes. One fixed 65536-byte buffer is
reused. Before/after budget checks bound each chunk and terminal length/EOF/hash checks. Offset and
length arithmetic remain checked. Read/length/unsupported-facility failures propagate as refusals.
There is no unchecked acceptance or weaker fallback. Every original acquired handle is still released
by the existing using/Dispose ownership on success or any refusal.

This choice adds bounded IO to the same coldStart/deadline interval instead of adding platform locking
interop. Windows share flags, Unix native opens, native identity queries and no-follow containment
are unchanged. Both new and already-open writers may open, but a completed change before the final
content pass refuses verification. No lock is claimed to protect arbitrary noncooperating Unix writes.
The content claim covers the bytes observed during each file's final rehash, with identity checked
afterward. Changes after that rehash, including during another file's validation, are outside that
content check. This is not an atomic multi-file snapshot or a perpetual freeze. Offline/workflow verification must still repeat on received files.
The concrete writer still samples completion only after Verify and retains its post-stamp terminal
check within the accepted correction/recovery owner. Additional reads are charged to that original
budget, without a new clock origin, cap, reservation, deadline, workload or cost exemption.

### Authoritative API evidence

The official [RandomAccess.Read contract](https://learn.microsoft.com/en-us/dotnet/api/system.io.randomaccess.read?view=net-10.0)
specifies handle-based reads at a supplied offset, supports short reads and does not advance the file
position for seekable files. [RandomAccess.GetLength](https://learn.microsoft.com/en-us/dotnet/api/system.io.randomaccess.getlength?view=net-10.0)
provides the handle length and refuses unsupported handles. The pinned
[.NET10 entry owner](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/RandomAccess.cs),
[Unix implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/RandomAccess.Unix.cs)
and [Windows implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/RandomAccess.Windows.cs)
show direct PRead for seekable Unix files and ReadFile with native offsets on Windows. This path does
not consult FileStream's managed read buffer. api-sources.json records these authoritative URLs.
No new native flags, ABI declarations, library dependency or package version is introduced.

### Deterministic actual-reader proof

The six behavioral reds on the starting production behavior published valid fixtures, held at
renderer.verify.complete after decoding, then opened FileMode.Open/FileAccess.Write with compatible
ReadWrite/Delete sharing. Each overwrote byte zero and flushed to storage without changing length or
native identity. The targets were root, earlier inventory and registered work-models producer, with
both an already-open writer and a writer opened after the reader. All six reached the hold, completed
the write and then falsely certified. Each red failed Assert.NotNull on the missing verification error.

After correction, all six writers still successfully flush on the local Mac, and all six actual
verifications refuse with the specific content-changed IOException. No blocked-write or skipped case
is counted as a mutated-file proof. Lengths are root 525017, inventory 60673 and producer 227 bytes.
The two smaller files exercise the buffered-stream hazard. Native entry IDs before and after remain
equal. Console process checks on the same changed directories also refuse. TRX stdout and
content-observations.json preserve the actual boundary/write/refusal observations.

Four additional reds proved the new pass's before/after chunk boundaries did not exist. Their green
actual PublicationWriter cases cancel or expire the original budget at those boundaries. Each reaches
the boundary, retains the actual timeout/cancellation and original token/stage, corrects stored final
JSON to Failed/nonpromotion and retains partial/raw/typed evidence. The 22-case targeted green also
reruns all five replacement cases, root replacement, hard-link alias, unavailable identity and the
four accepted post-stamp Unit correction cases, with zero failures/skips. No sleeps, racing loops,
stress, facade event list or native GPU execution is involved.


### Final affected and full-payload proof

After the final behavioral edits and scoped formatting, the supported shared Release setup built the
solution and verifier, then the affected outside measurement tool. Each build summary reports zero
warnings/errors. The measurement fact was built only. The final affected selection passed 125,
failed 0, skipped 0. It covers content/identity, original replacement/alias/link/reparse/escape and
unavailable cases, actual publication failure/completion, process verification and the existing full
fixtures. The full payloads were run once in this correction. The unchanged 476-case r1 protocol/
lifecycle run remains prior evidence and was not repeated as a new r2 result.

The three real PrepareCollection/Collect/reserve/commit/Publish/Verify fixtures retain all 16944 frames,
raw CPU/command/pass/error fields, two recipes, 129 occurrences and 258 factory events. Complete
nonempty and genuine Unsupported directories pass the actual managed console with no GPU providers.
The held Invalid variant retains partial evidence and refuses. Actual measured accounting follows.

| Variant | Base | Inventory | Completion shape | T | R | Commits | Root including padding | Combined |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| complete-small | 199541974 | 282181 | 7093453 | 12462, 12461 | 51830784, 51826688 | 4832, 5263 | 199552584 | 199834765 |
| complete-unsupported | 199541975 | 282181 | 7093454 | 12462, 12462 | 51830784, 51830784 | 2357, 2776 | 199547624 | 199829805 |
| held-invalid | 199757075 | 282181 | 7093481 | 12410, 12409 | 51617792, 51613696 | 4832, 2800 | 199765255 | 200047436 |

These are fresh observations, not forced byte expectations. The original 199883475 history,
23722707 contradiction and 176160768 threshold remain in the exact prefix. Caps, 16 MiB completion
reserve, graph budgets, profiles, native attempts/counts and 32-byte stamp slots are unchanged.
The new pass reads existing bytes again and never charges them twice as new serialized payload.
Its extra IO is inside the original elapsed/deadline interval. Headless numbers do not establish
unchanged native execution cost or deadline acceptance.

Exact argv, cwd, environment gates, integer exits and log lengths/SHA256 are in fix-r2/commands.jsonl.
The expanded final filter is retained there. TRX records the actual process argv, refusal output,
boundary observations and FULL_PROOF rows. New payload backups are under fix-r2/full and compact.
The independent audit streams their actual file hashes, decodes inventory totals and checks literal
headroom/request/reservation/commit arithmetic. It also verifies report-prefix preservation, source
hashes and equality of the built Render.Tests assembly consumed by the verifier and measurement tool.
Source, patch and commit/parent hashes are captured in source-hashes.json, precommit-proof.json and
postcommit-proof.json. Prior report sections and all prior scratch/backup files remain untouched.


### Finishing checks, responsibilities and proof limits

Whole-solution format verification returned 0, formatted 0 of 9136 files. Outside-verifier format
returned 0, formatted 0 of 3. Neither final log contains warnings/errors. The scoped apply changed
one test file's whitespace. Relevant whole-tree dashes, prose, size, instruction and doc-version
checks plus diff checking retain their exact raw exits in commands.jsonl. The audit requires their
successful exits before completion. No baseline or version declaration is changed.

Actionlint remains non-green, raw exit 1. The fresh r2 invocation reports exactly the same five
unknown ubuntu-26.04 runner-label diagnostics as r1, at identical lines/columns/messages. The CI file
is byte-identical to the accepted r1 head. actionlint-comparison.json preserves that result, without
suppression, runner/configuration change or calling a nonzero lint result green. Unchanged shared
build/CI owners retain their accepted bounded orchestration proof and were not reimplemented.

Only the directly affected verifier, new content tests, living renderer guarantee and this report
change. The audit verifies that native identity/FileSet, Unit, typed publication correction, shared
build scripts, CI, version/size baselines and canonical approved contracts remain byte-identical to
44866b66e. The new pass preserves strict schema/count/recipe/stage/correspondence checks and actual
exception transport. No dependency, native pipeline, compiler identity, measured workload, admission,
spend/attempt policy, cap or deadline is changed. There is no subagent, push, merge, tag, release,
settings change, cleanup or stash. Tests remove only their own temporary files. All prior retained
files and backups remain available.

Local execution is macOS filesystem and headless implementation proof. Windows/Linux API paths were
read against primary .NET sources but were not executed locally. This change does not prove an atomic
snapshot, arbitrary later/noncooperating write exclusion, native compiler correspondence, native
payload admission, deadline/cost, billed minutes or original armour/consumer acceptance. No full
ordinary suite/determinism workload, native GPU/device/window/timing/stress/paid/workflow or measurement
fact ran. Those gates remain separate. No manual validation is needed. Root owns actual-evidence
audit, fresh scoped review, acceptance/push and later main reconciliation. A review failure returns
for a scoped correction and affected proof, without weakening the guarantee.


## Fix round3

### Guard, rejected limitation and correction

Started at physical `/Users/antonio/KhaozEngine/.worktrees/perf-baseline`, branch
`feature/renderer-performance-baseline`, clean tracked index/tree and exact HEAD
`a9bcaf880c8304e5a8f03789b94116f84e2dbd86`. Origin task branch remains
`587eb9ed354fc12860a16f62088d24edd5426be8`. Read current contributor, security, platform,
build/test and documentation rules, the full r2 rereview, relevant R1-1 finding, both prior fix
appendices, canonical section 8 and CP-5/CP-7/CP-8. This correction retains the approved contract.

The preceding 43745 bytes are preserved exactly, SHA256
`7c875d2125b1dd69121d48fecf691c5f654f6558f8fc4e242c3fbb497a44e8bd`.
New scratch evidence is under `/tmp/khaozengine-renderer-cold-publication/task4/fix-r3/`.
All earlier logs, sources, payloads and verifier backups remain untouched.

Root rejected the round2 per-file limitation. An ordinary compatible writer can overwrite an
earlier registered producer after its final rehash while the inventory is rehashed, or overwrite
a consumed root prefix during the root's own multichunk rehash. Both preserve native identity and
exact length. The accumulated digest still contains the original bytes and the final identity pass
can certify stale evidence. These writes occur during validation, before certification. Neither
an atomic multi-file snapshot nor perpetual protection is needed to establish the defect.

ArtifactFileSet now establishes compatible-writer exclusion on each retained regular-file handle
before constructing its stream or reading content. ArtifactIdentity keeps native identity and
no-follow status separate from this exclusion. Windows regular-file opens retain GENERIC_READ and
FILE_SHARE_READ | FILE_SHARE_DELETE, omitting FILE_SHARE_WRITE. Existing data-write handles conflict
with that share request and subsequent data-write opens conflict with the retained lease. Entry
and directory metadata handles retain share mode 7. Native open failure evidence is captured
before disposing an invalid handle.

Unix uses a single nonblocking LOCK_EX | LOCK_NB flock on that same descriptor. Shared flock would
admit the ordinary .NET writers used by the regressions. Contention, unsupported facilities and
all acquisition failures refuse, without retries or fallback. A handle-based filesystem query
requires a supported local filesystem. .NET skips writer-sharing locks on network or unknown
filesystems, so NFS/SMB/CIFS and unsupported filesystem types refuse. Disabled .NET sharing also
refuses. This adds no general filesystem service or new dependency.

The original directory/file handles, no-follow entries, hard-link alias checks, exact lengths,
strict codecs, associations and content hashes remain. The final full-content pass remains defence
in depth inside the same original budget. It is no longer the proof of exclusion. Every acquired
handle remains held through that pass and final identity/containment validation, then closes through
DirectoryReader/FileSet disposal on success or refusal. Closing the Unix descriptor releases its
flock. No descriptor is duplicated or reopened for content validation.

### Platform contract and primary sources

The living guarantee in `docs/RENDERER-PERFORMANCE.md` now covers ordinary cooperating writers for
this finite retained-handle lifetime, including while later files are validated. It explicitly
limits Unix support to local APFS/HFS on macOS arm64/x64 and ext2/3/4, XFS, Btrfs, tmpfs and
overlayfs on Linux x64, with successful filesystem queries and flock acquisition. Noncooperating
Unix writes, writers that disable or bypass the sharing protocol, writes after handle release and
atomic multi-file transactions are not claimed. Received/offline/workflow evidence still requires
verification again. Windows/Linux execution remains unproved by this local Mac run.

Primary evidence includes [CreateFileW sharing/access compatibility](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew),
[Apple flock](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/flock.2.html),
[Apple close and advisory-lock release](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/close.2.html),
[Linux flock](https://man7.org/linux/man-pages/man2/flock.2.html),
[Linux v6.12 lock constants](https://github.com/torvalds/linux/blob/v6.12/include/uapi/asm-generic/fcntl.h),
[Linux v6.12 statfs layout](https://github.com/torvalds/linux/blob/v6.12/include/uapi/asm-generic/statfs.h),
[Linux filesystem constants](https://github.com/torvalds/linux/blob/v6.12/include/uapi/linux/magic.h),
[glibc 2.40 statfs layout](https://github.com/bminor/glibc/blob/glibc-2.40/sysdeps/unix/sysv/linux/bits/statfs.h),
[Darwin statfs layout](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/mount.h),
[.NET10 sharing/cleanup owner](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs)
and [.NET native flock/filesystem owner](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_io.c).
`platform-sources.json` records source URLs, exact retained bytes and hashes. The initial web
.NET page timed out and the guessed FileSystemType path returned 404. Successfully retained pinned
source and pal_io.c supply the proof, rather than those failed lookups.

The local SDK C ABI check exited 0 with warnings treated as errors. Darwin statfs is 2168 bytes,
flags offset 64, filesystem name offset 72, MNT_LOCAL 0x1000, LOCK_EX 2, LOCK_NB 4, LOCK_UN 8,
EWOULDBLOCK 35 and ENOTSUP 45. x64 uses fstatfs$INODE64 and arm64 uses fstatfs. Linux x64's
120-byte statfs and type offset 0 are source/ABI analysis, not executed Linux evidence. The retained
SafeFileHandle P/Invoke passes the real descriptor with SafeHandle lifetime protection.

### Deterministic red and green evidence

Both reds execute freshly built Release tests against unchanged a9bcaf880 reader source. The first
10-case run has 7 failed and 3 passed, exit 1. The three passing new-writer pre-pass cases are the
existing rehash detection, not exclusion proof. The five already-open cases fail because the reader
reaches validation instead of refusing acquisition. The new late-sidecar and consumed-prefix
new-writer cases fail because the old reader returns a verified wrapper after a successful flush.

The second red adds actual observations and narrowly injected unavailable-facility/cleanup cases.
All 9 cases fail, exit 1. TRX explicitly records successful flushed overwrites, preserved native IDs
and lengths 227 and 525017, reached third content.before or first content.chunk.after, and verified
old bytes for both new and already-open writers. The injected exclusion callback is never reached
on the old owner. Cleanup cases demonstrate the missing write refusal at their actual held points.
No sleeps, races, retries, test loops or synthetic load are used. `red-sources.json` identifies the
actual pre-fix assembly and source hashes. `red-tests.patch` preserves the initial regression changes.

The first focused green passes 23, failed 0, skipped 0. New writers are refused with actual sharing
contention at the complete, later-file and consumed-prefix boundaries. Existing writers prevent the
reader's target acquisition, before any content read from that file. Tests observe exact acquisition
counts, native errno, unchanged bytes and no false verified wrapper after a permitted completed
write. After every success/refusal, the old writer is disposed and a new ordinary writer opens,
writes and flushes again. A valid console verification after restoring original bytes checks release
of every file lease. Replacement cases permit only a meaningful Windows blocked-mutation alternative,
with native refusal, unchanged identity/bytes and successful verification. Actual replacements
require identity refusal and changed entry identity. No unrelated parse error satisfies these cases.

The final affected run passes 136, failed 0, skipped 0, exit 0, after all behavioral edits. It
includes the six adapted pre-pass cases, four new schedule cases, four unavailable-exclusion
cases, three held refusal/cancellation release cases, all retained replacement/alias/link/reparse/
escape/unavailable-identity checks, before/after read budget cases, accepted post-stamp Unit
correction cases and actual console/publication tests. No unchanged broad lifecycle/protocol suite
or ordinary full/determinism workload was repeated.

Unavailable-exclusion tests inject only the platform operation through the identity boundary while
FileSet and DirectoryReader perform actual acquisition, native status/entry queries, previous
real exclusions, reads and disposal. Failure at acquisition 1 observes zero content reads and two
closed handles. Failure at acquisition 3 observes two completed file acquisitions, ten prior
content chunks and four closed handles. The exact thrown object survives. Missing-symbol refusal
retains EntryPointNotFoundException. Unsupported-operation refusal retains native ENOTSUP evidence
(45 on the Mac), distinct from actual contention errno 35. These are labelled injected failures,
not claims that the local filesystem lacks flock. Every acquired file can be reopened, written and
flushed after refusal. The actual console then verifies all files with no GPU providers.

### Final affected payload and unchanged budgets

Supported final setup uses `sh scripts/build-release-tests.sh KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj`,
including the outside verifier. The outside measurement tool is built through the same supported
setup and is not executed. All final build summaries report zero warnings/errors.
The final affected selection ran once after final behavior changes, in the heavy build slot.

Each real PrepareCollection/Collect/reserve/commit/Publish/Verify fixture retains 16944 frames,
two recipes, 129 occurrences and 258 factory events. Complete nonempty and genuine Unsupported
directories pass actual console verification. The held Invalid variant refuses while preserving
raw frame/error and partial evidence. Fresh observed accounting is unchanged from earlier rounds.

| Variant | Base | Inventory | Completion shape | T | R | Commits | Root including padding | Combined |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| complete-small | 199541974 | 282181 | 7093453 | 12462, 12461 | 51830784, 51826688 | 4832, 5263 | 199552584 | 199834765 |
| complete-unsupported | 199541975 | 282181 | 7093454 | 12462, 12462 | 51830784, 51830784 | 2357, 2776 | 199547624 | 199829805 |
| held-invalid | 199757075 | 282181 | 7093481 | 12410, 12409 | 51617792, 51613696 | 4832, 2800 | 199765255 | 200047436 |

The original 256 MiB cap, 16 MiB completion reserve, graph budgets, deadline, coldStart clock,
compiler profiles, schema, timed workload, native attempt limits and spend/attempt gates are
unchanged. Exclusion and the defence-in-depth rehash consume the existing finite interval. They
add no serialized payload and no new accounting exemption. Synthetic headless payload arithmetic
does not establish native deadline or cost acceptance.

### Finishing checks, retained evidence and handback

Whole-solution format verification returns 0, formatted 0 of 9137 files. Outside-verifier format
returns 0, formatted 0 of 3. Neither reports warnings/errors. Dashes, prose, file size, instruction
and documentation-version guards return 0. File size retains its existing Scene3D smaller-than-
baseline advisory and the baseline remains unchanged. The initial diff check returns 2 for an
extra blank line at the unfinished appendix EOF. The completed appendix removes that blank line,
then repeats the relevant text/diff checks. This was a documentation-only failure after green
behavioral verification, without a reason to rerun the unchanged suite.

Actionlint remains raw exit 1 for the unchanged ci.yml and the same five baseline ubuntu-26.04
runner-label diagnostics. `actionlint-comparison.json` compares exact message/line/column triples
with r2. No suppression, runner, workflow, settings or lint-configuration change hides this result.

A separate actual-console invocation with DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1 refuses the otherwise
valid retained complete-small directory, raw exit 1, with the specific required-sharing-protocol
cause. This sets only that child process environment. It changes no persistent setting. The normal
console already verifies the same directory in the final affected fixture.

`commands.jsonl` records actual argv, physical cwd, environment gates, integer exits, durations,
log lengths and hashes, including expected reds/refusals and the initial whitespace failure.
`observations.json` retains actual held-boundary, acquisition/refusal/release observations from TRX.
`full-proof.json` retains the three literal FULL_PROOF records. The independent `audit.py` streams
the real payloads in 65536-byte chunks, checks file lengths/hashes, decodes inventory counts and
recomputes literal T/R/commit arithmetic. It checks all command log hashes, TRX counters, report
prefix preservation, the three equal consumed Render.Tests assembly hashes and untouched accepted
owners/canonical contracts/version/baselines.

`source-hashes.json`, `final.patch`, `precommit-proof.json` and `postcommit-proof.json` identify exact
sources, patch, manifest, report, commit, parent and unchanged origin. Normal hooks apply to the
eight explicit paths. The staged engine version remains 21.13.0. No native GPU implementation,
compiler configuration/profile, admission/spend gate, timed workload, schema or budget changes.

This is local macOS filesystem and headless implementation proof. Windows/Linux runtime, native
compiler correspondence, native payload/deadline/cost acceptance, billed minutes, main integration,
ordinary full verification, release and original armour/consumer gates remain open. No measurement
fact, native GPU/device/window/timing/paid/workflow/stress execution, full ordinary suite,
determinism workload, subagent, setting change, stash, cleanup, push, main merge, tag or release
occurs. Tests remove only their own temporary files. All earlier evidence and backups are retained.

No manual validation is needed. Root owns actual-evidence verification and a fresh independent scoped
review, then acceptance/push or a proper correction. A failed review returns to the retained branch
for correction and affected proof, without weakening the approved finite exclusion contract.
