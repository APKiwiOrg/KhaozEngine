# Native Windows console regression

This finite harness exercises issue #1322 against `KhaozEngine.Platform` source through a project
reference. The probe is a Windows `WinExe` and the driver is a console executable. Both are
`IsPackable=false` and live outside the main solution. Neither references a game or consumer.

Run once on Windows with the .NET 10 SDK and PowerShell 7 from the engine checkout:

```powershell
./scripts/tests/windows-console-smoke.ps1
```

`-EvidenceDirectory <new-directory>` selects the evidence destination. `-CompileOnly` builds without
launching processes, including from another OS. The runner verifies the native apphost PE subsystems
are 2 for the probe and 3 for the driver. It restores with a private cache by default, using only
nuget.org, and rejects a restored engine package. CI sets an isolated cache for each workflow attempt.
Builds are sequential and never enter a release, pack or publish path.

## Native fixtures and assertions

The driver allocates a private kernel console, opens `CONIN$` and `CONOUT$`, and hides its window.
Core scenarios launch through a real `cmd.exe` batch file that waits for the GUI executable. Evidence
records the probe's native parent PID while cmd is alive. Successful attachments must share the
console window and kernel process membership with both cmd and the driver. Terminal markers are
read from the actual console screen buffer. The successful one-shot scenario deliberately detaches
after writing its markers, then checks that an enabled second call cannot reattach.

The natural terminal and cmd stdout/stderr redirection cases never call `SetStdHandle` in the child.
The batch file performs `>` or `2>` itself. Their entry evidence must show absent native handles with
file type 0 or a redirected disk handle with file type 1 as appropriate. A direct native launch also
retains inherited driver console handles under `STARTF_USESTDHANDLES` without resetting child inputs.
All natural cases require identical native entry and before-call snapshots.

Other cases record unmodified entry state, then install controlled native stream fixtures before
calling the engine. This covers absent and invalid handles even when a runner or cmd would otherwise
inherit handles. The scenarios cover:

- Natural terminal, natural cmd stdout/stderr file redirection, and inherited real console handles.
- Both handles absent and both handles invalid.
- stdout file with stderr absent and the reverse.
- stdout pipe with stderr absent and the reverse.
- Both streams redirected to files and both streams redirected to pipes.
- An already attached console, an opt-out followed by an enabled call, and a successful one-shot.
- Direct native launches with and without `STARTF_USESTDHANDLES`, including invalid startup handles.
- A normal GUI launch from a parent that owns no console, without console creation flags.

Redirected scenarios also install a real stdin file and verify its handle identity and native input
marker after the engine call. For stdout and stderr, assertions require preserved handle identity,
native file type, native write success, managed and native destination markers, and no terminal
leakage. File and pipe content must match exactly after process exit, catching an exit newline sent
to redirected stdout. Anonymous pipe readers stay in the driver and drain once until EOF.

`Console.IsOutputRedirected` and `Console.IsErrorRedirected` are diagnostic evidence only. Absent
handles can report redirected and the managed flags can remain cached after attachment. Success is
gated by native handles and observed output, never by those flags.
An unassociated WinExe can also receive a nonzero stale handle. Natural terminal setup requires
`GetFileType` to report `ERROR_INVALID_HANDLE`, rather than assuming every missing handle is zero.
Inherited console handles are verified in the driver before launch, then repaired and verified in
the child after attachment.

Windows can replace handles during attachment when `STARTF_USESTDHANDLES` was absent. The native
launch cases assert that the requested startup flag was actually used, forcing coverage of both
replacement and retained-invalid-handle paths. See Microsoft's
[GetStdHandle attach behavior](https://learn.microsoft.com/en-us/windows/console/getstdhandle#attachdetach-behavior).

## Bounds and evidence

Each child is assigned to a kill-on-close job before its first instruction. Every ready wait and
child exit wait is 20 seconds, pipe EOF and cleanup waits are five seconds, and each build command is
limited to 180 seconds. The outer driver deadline is 480 seconds and CI has a 25-minute job ceiling.
There are no stress loops. A failed case stays failed while independent cases produce evidence.

CI uses only GitHub-hosted `windows-2025`, runs on filtered pushes to main and the issue branch,
filtered pull requests and manual dispatch, and always attempts to upload evidence. Artifacts include
source hashes, build logs, subsystem checks, case evidence, live parent/membership evidence, terminal
buffers, exact redirected content, batch scripts, and the result summary. Explorer and crash-report
acceptance belong to their existing independent checks.

For test-first use, commit and push this harness while production is unchanged. Confirm native
failure comes from the missing attachment or stream preservation contract, then apply the engine
fix and rerun the same workflow. This directory does not change production.
