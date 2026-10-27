using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class CapacityDemandSourceContractTests
{
    [Fact]
    public void Documentation_ListsEveryGateExpressionIncludingDynamicOrigins()
    {
        string root = Root();
        var actual = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string directory in new[] { "KhaozEngine.MapDoc", "KhaozEngine.MapDoc.Render3D", "KhaozEngine.Render3D", "KhaozEngine.Render2D" })
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                foreach (Match match in Regex.Matches(text, @"(?:(?:DemandDiagnostics|CapacityDemandDiagnostics)\.Check|\.Report)\("))
                {
                    var args = Arguments(text, match.Index + match.Length);
                    string limit = match.Value.Contains(".Check(", StringComparison.Ordinal) ? Normalize(args[2]) : "origin.LimitName";
                    actual.Add($"{Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')} | {Normalize(args[1])} | {limit}");
                }
            }
        var documented = File.ReadLines(Path.Combine(root, "docs", "CAPACITY-DEMAND.md"))
            .Where(line => line.StartsWith("| KhaozEngine.", StringComparison.Ordinal))
            .Select(line => line.Trim('|', ' ').Replace("`", "", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Empty(actual.Except(documented));
        Assert.Empty(documented.Except(actual));
    }

    static string Normalize(string expression) => Regex.Replace(expression, @"\s+", "");

    static List<string> Arguments(string text, int start)
    {
        var result = new List<string>();
        int depth = 0, part = start;
        bool quoted = false, escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') { quoted = true; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c == ')' && depth == 0) { result.Add(text[part..i]); return result; }
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) { result.Add(text[part..i]); part = i + 1; }
        }
        throw new InvalidOperationException("Unterminated capacity gate.");
    }

    static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KhaozEngine.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Checkout root not found.");
    }
}
