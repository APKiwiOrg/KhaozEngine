#nullable enable

namespace KhaozEngine.Updates;

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
    void DeleteFile(string path);
    void DeleteDirectory(string path);
    void Sleep(int milliseconds);

    /// <summary>Blocks until the process with <paramref name="pid"/> exits or the timeout elapses.</summary>
    void WaitForParentExit(int pid, int timeoutMilliseconds);

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

    /// <summary>Starts the game executable again after the apply completes.</summary>
    void Relaunch(string executablePath, string workingDirectory);

    /// <summary>Clears the macOS <c>com.apple.quarantine</c> attribute on the install (no-op elsewhere).</summary>
    void ClearQuarantine(string installDir);

    /// <summary>True when <paramref name="path"/> exists and is a symlink/reparse point.</summary>
    bool IsReparsePoint(string path);

    /// <summary>
    /// Verifies the OS-level code signature of the installed executable/bundle at
    /// <paramref name="executablePath"/>. Returns true on platforms without signature enforcement.
    /// </summary>
    bool VerifyCodeSignature(string executablePath);

    void Log(string message);
}
