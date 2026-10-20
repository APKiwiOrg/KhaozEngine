using System;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Structural workflow assertions read checked-out text, which is CRLF under core.autocrlf=true.
internal static class CoveragePerfWorkflowText
{
    internal static string Lf(string text) => text.ReplaceLineEndings("\n");

    internal static string After(string text, string marker)
    {
        text = Lf(text);
        int index = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "Missing workflow marker: " + marker.Trim());
        return text[(index + marker.Length)..];
    }

    internal static string Before(string text, string marker)
    {
        text = Lf(text);
        int index = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "Missing workflow marker: " + marker.Trim());
        return text[..index];
    }

    internal static string Events(string workflow) => Before(After(workflow, "\non:\n"), "\npermissions:");

    internal static string Step(string workflow, string name)
    {
        string rest = After(workflow, "      - name: " + name);
        int next = rest.IndexOf("\n      - ", StringComparison.Ordinal);
        return next < 0 ? rest : rest[..next];
    }

    internal static string[] Steps(string workflow) => Lf(workflow).Split("\n      - name: ", StringSplitOptions.None);
}
