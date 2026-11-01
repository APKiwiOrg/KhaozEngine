using System.IO;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfWorkflowShell
{
    internal static string WindowsBash(string gitExecPath)
    {
        // Git for Windows keeps git-core under mingw64/libexec and its console Bash under bin.
        string path = Path.GetFullPath(Path.Combine(gitExecPath, "../../..", "bin", "bash.exe"));
        if (!File.Exists(path))
            throw new FileNotFoundException("Git for Windows Bash is required to run the renderer workflow selector.", path);
        return path;
    }
}
