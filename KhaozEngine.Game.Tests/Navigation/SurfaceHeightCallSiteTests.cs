using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class SurfaceHeightCallSiteTests
{
    [Fact]
    public void ProductionCallSitesAreClassified()
    {
        string[] expected =
        {
            "KhaozEngine.Movement/GroundNavigation.cs:Resolve:RestingHeightAt",
            "KhaozEngine.Movement/RouteStraightener.cs:TryFeet:RestingHeightAt",
            "KhaozEngine.Movement/PhysicsNavBake.Profiles.cs:Feet:RestingHeightAt",
            "KhaozEngine.Navigation/GridPathPlanner.Region.cs:CellFeet:RestingHeightAt",
            "KhaozEngine.Navigation/NavSpace.cs:LayerAt:RestingHeightAt",
            "KhaozEngine.Movement/PhysicsNavBake.Profiles.cs:IsFloatNode:SurfaceHeightAt",
            "KhaozEngine.Movement/GroundNavigationBake.Writer.cs:WriteProfile:SurfaceHeightAt",
            "KhaozEngine.Navigation/NavLayerLinks.cs:GenerateCore:SurfaceHeightAt",
            "KhaozEngine.Navigation/NavLayerLinks.cs:GenerateCore:SurfaceHeightAt",
            "KhaozEngine.Navigation/NavHopLinks.cs:Generate:SurfaceHeightAt",
            "KhaozEngine.Navigation/NavHopLinks.cs:Generate:SurfaceHeightAt",
        };
        var actual = new List<string>();
        string root = RepositoryRoot();
        foreach (string project in new[] { "KhaozEngine.Navigation", "KhaozEngine.Movement" })
        {
            string directory = Path.Combine(root, project);
            foreach (string path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Split('/').Any(segment => segment is "bin" or "obj")) continue;
                string name = Path.GetFileName(path);
                if (name == "NavGrid.cs" || name.StartsWith("NavGrid.", StringComparison.Ordinal)) continue;

                string source = WithoutComments(File.ReadAllText(path));
                Match[] methods = Regex.Matches(source,
                    @"(?m)^[ \t]*(?!(?:else|return|throw|new)\b)(?:(?:public|private|protected|internal|static|async|sealed|override|virtual|partial)\s+)*[\w.<>,?\[\]]+\s+(?<method>\w+)\s*\([^;{}]*\)\s*(?:\{|=>)")
                    .Cast<Match>().ToArray();
                foreach (Match call in Regex.Matches(source, @"\b(?<reader>SurfaceHeightAt|RestingHeightAt)\s*\("))
                {
                    Match? method = methods.LastOrDefault(candidate => candidate.Index < call.Index &&
                        call.Index < MethodEnd(source, candidate));
                    Assert.NotNull(method);
                    actual.Add($"{relative}:{method.Groups["method"].Value}:{call.Groups["reader"].Value}");
                }
            }
        }

        // Keep duplicates so both raw reads in each link generator are pinned.
        Assert.Equal(expected.OrderBy(site => site, StringComparer.Ordinal),
            actual.OrderBy(site => site, StringComparer.Ordinal));
    }

    static int MethodEnd(string source, Match method)
    {
        int start = method.Index + method.Length - 1;
        if (source[start] != '{') return source.IndexOf(';', start);
        int depth = 0;
        for (int index = start; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            if (source[index] == '}' && --depth == 0) return index;
        }
        throw new InvalidOperationException("A source method has no closing brace.");
    }

    // Reuse SourceSweep's comment blanking and source-only enumeration pattern from the render tests.
    static string WithoutComments(string source)
    {
        char[] chars = source.ToCharArray();
        for (int index = 0; index < chars.Length - 1; index++)
        {
            if (chars[index] != '/') continue;
            if (chars[index + 1] == '/')
            {
                while (index < chars.Length && chars[index] != '\n') chars[index++] = ' ';
            }
            else if (chars[index + 1] == '*')
            {
                while (index < chars.Length && !(chars[index] == '*' && index + 1 < chars.Length && chars[index + 1] == '/'))
                {
                    if (chars[index] != '\n') chars[index] = ' ';
                    index++;
                }
                if (index < chars.Length) chars[index] = ' ';
                if (index + 1 < chars.Length) chars[index + 1] = ' ';
                index++;
            }
        }
        return new string(chars);
    }

    static string RepositoryRoot()
    {
        DirectoryInfo? directory = new FileInfo(typeof(SurfaceHeightCallSiteTests).Assembly.Location).Directory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KhaozEngine.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("The call-site sweep needs a checkout containing KhaozEngine.slnx above the test assembly.");
    }
}
