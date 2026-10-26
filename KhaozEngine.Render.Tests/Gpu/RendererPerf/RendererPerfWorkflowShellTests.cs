using System;
using System.IO;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfWorkflowShellTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Windows_selector_requires_the_Bash_from_its_Git_installation(bool installed)
    {
        string root = Path.Combine(Path.GetTempPath(), "khaozengine-renderer-shell-" + Guid.NewGuid().ToString("N"));
        string exec = Path.Combine(root, "mingw64", "libexec", "git-core");
        string expected = Path.Combine(root, "bin", "bash.exe");
        Directory.CreateDirectory(exec);
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        if (installed) File.WriteAllText(expected, "Git Bash fixture, never executed");
        try
        {
            if (installed) Assert.Equal(expected, RendererPerfWorkflowShell.WindowsBash(exec));
            else Assert.Throws<FileNotFoundException>(() => RendererPerfWorkflowShell.WindowsBash(exec));
        }
        finally { RendererPerfFixtureDirectory.Delete(root); }
    }
}
