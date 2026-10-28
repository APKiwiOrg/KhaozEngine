using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfRehearsalDocsTests
{
    [Theory]
    [InlineData("RENDERER-PERFORMANCE.md", "## Software correctness rehearsal", "## Cold backend evidence")]
    [InlineData("CROSS-PLATFORM.md", "### Free software renderer rehearsal", "## CI matrix")]
    public void Living_rehearsal_reference_covers_both_legs_and_explicit_schedules(string file, string start, string end)
    {
        string text = Read(file);
        int first = text.IndexOf(start, StringComparison.Ordinal);
        string section = text[first..text.IndexOf(end, first, StringComparison.Ordinal)];
        Assert.Contains("rehearsal-full", section, StringComparison.Ordinal);
        Assert.Contains("WARP", section, StringComparison.Ordinal);
        Assert.Contains("linux-vulkan", section, StringComparison.Ordinal);
        Assert.Contains("windows-d3d11", section, StringComparison.Ordinal);
        Assert.Contains("not permission to run", section, StringComparison.Ordinal);
    }

    static string Read(string file, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../../docs", file)));
}
