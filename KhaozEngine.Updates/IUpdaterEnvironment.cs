using System;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>
/// How a relaunch attempt started, reported by <see cref="IUpdaterEnvironment.TryRelaunch"/> so the
/// applier can retry a launch that hit the Windows AV/image race instead of leaving the player on a
/// bare "The application was unable to start correctly (0xc0000142)" dialog.
/// </summary>
public enum RelaunchStartupOutcome
{
    /// <summary>The process is still alive after the watch window (the image loaded and the game is booting).</summary>
    Running,

    /// <summary>
    /// The process exited within the watch window with a Windows startup-failure NTSTATUS
    /// (0xC0000142 DLL-init-failed, 0xC0000409 stack-buffer-overrun, 0xC0000005 access-violation,
    /// 0xC0000135 DLL-not-found, 0xC0000139 entry-point-not-found). This is the AV/torn-image race:
    /// retrying after a short back-off lets the security scan release the new image.
    /// </summary>
    StartupFailed,

    /// <summary>
    /// The process exited within the watch window with any other code (including 0). The game actually
    /// ran (it just closed quickly), so the relaunch is done and must not be retried.
    /// </summary>
    ExitedEarly,

    /// <summary>The launch could not be started at all (the file was missing or the OS refused to start it); retry.</summary>
    LaunchError
}

/// <summary>
/// The side-effecting operations the staged-apply core needs: file IO, process waiting/relaunch, and
/// the macOS quarantine clear. Abstracted so <see cref="UpdateApplier"/> is pure orchestration and
/// fully headless-testable; <see cref="SystemUpdaterEnvironment"/> is the real implementation the
/// updater shim uses.
/// </summary>
public interface IUpdaterEnvironment
{
    bool FileExists(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string content);
    void CreateDirectory(string path);
    void CopyFile(string source, string destination, bool overwrite);

    /// <summary>
    /// Atomically replaces <paramref name="destination"/> with the contents of <paramref name="source"/>.
    /// The real environment copies to a temp file in the destination's directory then renames it into
    /// place (a same-volume rename is atomic on Windows and POSIX), so a concurrent reader or the
    /// post-apply relaunch can never observe a half-written image, and the on-disk file is always either
    /// the complete old or complete new content. This is how the freshly-installed binaries (including the
    /// game exe) are written, closing the "WER reports the old version" torn-image race. The default
    /// delegates to a plain overwriting <see cref="CopyFile"/> so external implementers predating this
    /// member keep compiling (they simply do not get the atomic guarantee).
    /// </summary>
    void ReplaceFile(string source, string destination) => CopyFile(source, destination, overwrite: true);

    void DeleteFile(string path);
    void DeleteDirectory(string path);
    void Sleep(int milliseconds);

    /// <summary>
    /// The current UTC wall-clock time, used to stamp the post-update marker's completion time. Abstracted
    /// so the timestamp is deterministic in headless tests. The default returns
    /// <see cref="DateTimeOffset.UtcNow"/>, which is what the real shim uses.
    /// </summary>
    DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <summary>Blocks until the process with <paramref name="pid"/> exits or the timeout elapses.</summary>
    void WaitForParentExit(int pid, int timeoutMilliseconds);

    /// <summary>
    /// True when a process with <paramref name="pid"/> is currently running. The applier calls this after
    /// <see cref="WaitForParentExit"/> to confirm the game is really gone before it mutates any install
    /// file (and to ride out a late-dying process). The real environment returns false on non-Windows
    /// (POSIX has no self-lock, so the barrier is a no-op there and the apply path is unchanged). The
    /// default returns false (assume gone), preserving the prior "proceed once the wait returns" behaviour
    /// for implementers predating this member.
    /// </summary>
    bool IsProcessAlive(int pid) => false;

    /// <summary>
    /// True when <paramref name="path"/> can be opened for exclusive read (no other process holds a
    /// handle on it). This is the post-apply settle check on Windows: after a freshly-written executable
    /// lands, the OS antivirus scans it and briefly locks the file, and relaunching mid-scan trips over
    /// the in-flight image (STATUS_DLL_INIT_FAILED / STATUS_STACK_BUFFER_OVERRUN). The applier polls this
    /// until the scanner releases the exe, then relaunches. The real implementation opens with
    /// <c>FileShare.None</c> and disposes on success; it returns true on non-Windows (POSIX has no
    /// equivalent lock, so the settle wait is a no-op there).
    /// </summary>
    bool CanOpenExclusively(string path);

    /// <summary>
    /// The full path of the running updater executable, or null when relocation is neither needed nor
    /// possible. The real environment returns <see cref="System.Environment.ProcessPath"/> on Windows
    /// (where a process locks its own loaded binary, so the updater must relocate out of the install dir
    /// to overwrite itself) and null elsewhere (POSIX lets a running executable's file be replaced in place).
    /// </summary>
    string? GetSelfExecutablePath();

    /// <summary>The directory the running updater's binaries live in (its <c>AppContext.BaseDirectory</c>).</summary>
    string GetSelfBaseDirectory();

    /// <summary>
    /// True when the running updater can apply the swap in <paramref name="path"/> without elevation. The
    /// applier checks the install dir before applying; a false result (on Windows, a protected location like
    /// Program Files) means the swap needs elevation to overwrite the installed binaries. The default returns
    /// true (assume writable) for implementers predating this member. The real environment reports a protected
    /// root (Program Files / Windows) as not-writable whenever the process is not already elevated - a plain
    /// create-a-temp-file probe is a false positive there, since a new file at the install root can be created
    /// even when overwriting the existing binaries fails - and otherwise falls back to a create/delete probe.
    /// </summary>
    bool CanWriteToDirectory(string path) => true;

    /// <summary>
    /// Relaunches the updater elevated (Windows UAC "runas") against the same apply config, passing
    /// <c>--relocated --elevated</c> so the elevated copy skips relocation and the writability check and
    /// applies with permission to overwrite a protected install. Returns true when the elevated copy was
    /// launched (the caller then exits), false when elevation is unavailable or refused (non-Windows, the
    /// user declined UAC, or no self-exe path), in which case the caller applies in place and rolls back
    /// cleanly if the write stays denied. The default returns false (no elevation) for implementers
    /// predating this member.
    /// </summary>
    bool TryElevate(string updaterExePath, string applyConfigPath, string workingDirectory) => false;

    /// <summary>
    /// Launches the relocated updater copy at <paramref name="updaterExePath"/> against the same apply
    /// config, passing <c>--relocated</c> so the copy skips relocation and applies in place from its
    /// scratch dir (where its own binaries are no longer locked by the install-dir process).
    /// </summary>
    void LaunchRelocatedUpdater(string updaterExePath, string applyConfigPath, string workingDirectory);

    /// <summary>
    /// Schedules best-effort deletion of <paramref name="directory"/> for after THIS process exits and
    /// releases its locks (a detached OS one-shot). Used to remove the relocated updater scratch dir so
    /// nothing is left behind on the machine.
    /// </summary>
    void ScheduleDirectoryDeletion(string directory);

    /// <summary>
    /// Starts the game executable again after the apply completes (fire-and-forget). This is the low-level
    /// primitive; the applier drives launches through <see cref="TryRelaunch"/> so it can detect and retry
    /// a failed start.
    /// </summary>
    void Relaunch(string executablePath, string workingDirectory);

    /// <summary>
    /// Relaunches the game and watches it for up to <paramref name="watchMilliseconds"/> to catch a fast
    /// startup failure. On Windows a freshly-written single-file exe can still be blocked from *executing*
    /// by an in-flight antivirus scan even after it is openable, so the launch fails at image load with a
    /// startup NTSTATUS. The applier retries a <see cref="RelaunchStartupOutcome.StartupFailed"/> or
    /// <see cref="RelaunchStartupOutcome.LaunchError"/> result with back-off until the scan settles; a
    /// process still alive after the watch window (<see cref="RelaunchStartupOutcome.Running"/>) or one
    /// that ran and exited on its own (<see cref="RelaunchStartupOutcome.ExitedEarly"/>) is done. The
    /// default (for external implementers predating this member) delegates to <see cref="Relaunch"/> and
    /// reports <see cref="RelaunchStartupOutcome.Running"/>, preserving the old fire-and-forget behaviour.
    /// </summary>
    RelaunchStartupOutcome TryRelaunch(string executablePath, string workingDirectory, int watchMilliseconds)
    {
        Relaunch(executablePath, workingDirectory);
        return RelaunchStartupOutcome.Running;
    }

    /// <summary>Clears the macOS <c>com.apple.quarantine</c> attribute on the install (no-op elsewhere).</summary>
    void ClearQuarantine(string installDir);

    /// <summary>True when <paramref name="path"/> exists and is a symlink/reparse point.</summary>
    bool IsReparsePoint(string path);

    /// <summary>
    /// Re-seals the installed application bundle so its signature matches the files just swapped in,
    /// then reports whether the re-seal succeeded. On macOS an in-place file swap inside a <c>.app</c>
    /// invalidates the sealed <c>_CodeSignature/CodeResources</c> hashes, so a post-apply
    /// <see cref="VerifyCodeSignature"/> ALWAYS fails and the update rolls back - a macOS in-place
    /// self-update can never complete without this step. The real environment re-signs the enclosing
    /// <c>.app</c> ad-hoc (<c>codesign --force --sign -</c>, inner-to-outer, no <c>--deep</c>), which
    /// makes the bundle internally consistent again so <see cref="VerifyCodeSignature"/> passes; the
    /// ad-hoc seal drops Developer ID / notarization, which is acceptable because quarantine is already
    /// cleared and the app has already launched, so Gatekeeper's quarantined-first-launch gate is past.
    /// Called AFTER the file swap and BEFORE <see cref="VerifyCodeSignature"/>; a false result rolls the
    /// apply back exactly like a verify failure (fail-closed). The default returns true - a no-op success
    /// for platforms without bundle sealing and for external implementers predating this member (they
    /// simply rely on <see cref="VerifyCodeSignature"/> as before).
    /// </summary>
    bool ResealCodeSignature(string executablePath) => true;

    /// <summary>
    /// Verifies the OS-level code signature of the installed executable/bundle at
    /// <paramref name="executablePath"/>. Returns true on platforms without signature enforcement.
    /// </summary>
    bool VerifyCodeSignature(string executablePath);

    void Log(string message);
}
