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
    void WriteAllText(string path, string content);
    void CreateDirectory(string path);
    void CopyFile(string source, string destination, bool overwrite);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
    void Sleep(int milliseconds);

    /// <summary>Blocks until the process with <paramref name="pid"/> exits or the timeout elapses.</summary>
    void WaitForParentExit(int pid, int timeoutMilliseconds);

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
