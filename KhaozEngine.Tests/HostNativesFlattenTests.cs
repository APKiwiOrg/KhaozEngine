using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Guards the shipped Linux host-native flatten (issue 722). Silk.NET reaches GLFW, OpenAL Soft,
/// <c>libshaderc_shared</c> and <c>libspirv-cross</c> through its own path resolver rather than
/// <c>[DllImport]</c>, and on Linux that resolver probes <c>runtimes/&lt;DISTRO rid&gt;/native</c> while the
/// packages ship under the portable rid, so nothing loads until the natives sit FLAT beside the assemblies.
/// The rule is an MSBuild asset, so the contract is asserted against the on-disk files the way
/// <see cref="CetCompatDefaultTests"/> does. Three separate silent failure modes are covered: the file being
/// renamed out of the import, the csproj forgetting to pack it, and an umbrella
/// <c>ProjectReference</c> suppressing the Build asset so it never reaches a consumer at all.
/// </summary>
public class HostNativesFlattenTests
{
    const string RuleFile = "KhaozEngine.HostNatives.targets";

    // [CallerFilePath] is the compile-time path of this source file, so it locates the repo tree regardless of
    // `dotnet test`'s working directory (the same trick CetCompatDefaultTests uses).
    static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    static XElement RuleRoot() => XDocument.Load(
        Path.Combine(RepoRoot(), "KhaozEngine.Foundation", "build", RuleFile)).Root!;

    static XElement Target(string name)
        => RuleRoot().Elements("Target").Single(t => (string?)t.Attribute("Name") == name);

    [Fact]
    public void BuildTarget_IsLinuxOnly_RunsAfterTheOutputCopy_AndIsOptOut()
    {
        XElement target = Target("KhaozEngineCopyHostNativesToOutput");

        // After the output copy, so the flat natives land in a directory the SDK has finished writing.
        Assert.Equal("CopyFilesToOutputDirectory", (string?)target.Attribute("AfterTargets"));

        string condition = (string?)target.Attribute("Condition") ?? "";
        // Linux only: Windows and macOS resolve out of runtimes/ already, and the copy is not free.
        Assert.Contains("IsOSPlatform(Linux)", condition);
        // The host rid has to be known, because it is what the item filter joins on.
        Assert.Contains("$(NETCoreSdkPortableRuntimeIdentifier)", condition);
        Assert.Contains("'$(KhaozEngineFlattenHostNatives)' == 'true'", condition);
    }

    [Fact]
    public void BuildTarget_CopiesTheHostRidsNativeAssets_Flat()
    {
        XElement target = Target("KhaozEngineCopyHostNativesToOutput");

        XElement item = target.Elements("ItemGroup").Elements().Single();
        Assert.Equal("@(RuntimeTargetsCopyLocalItems)", (string?)item.Attribute("Include"));
        string filter = (string?)item.Attribute("Condition") ?? "";
        Assert.Contains("AssetType)' == 'native'", filter);
        Assert.Contains("RuntimeIdentifier)' == '$(NETCoreSdkPortableRuntimeIdentifier)'", filter);

        XElement copy = target.Elements("Copy").Single();
        // Flat into the output directory: the resolver's FIRST probe is the app base itself.
        Assert.Equal("$(OutDir)", (string?)copy.Attribute("DestinationFolder"));
        Assert.Equal("true", (string?)copy.Attribute("UseHardlinksIfPossible"));
    }

    [Fact]
    public void PublishTarget_CoversARidAgnosticPublish_AndStandsDownWhenARidIsSet()
    {
        // A rid-agnostic `dotnet publish` republishes from the resolved package assets, so the flat copies made
        // in $(OutDir) do not travel and the published app is broken again. A `publish -r <rid>` already lays
        // them flat, and a second copy at the same relative path is a duplicate-file error.
        XElement target = Target("KhaozEngineFlattenHostNativesForPublish");

        Assert.Equal("ComputeResolvedFilesToPublishList", (string?)target.Attribute("AfterTargets"));
        string condition = (string?)target.Attribute("Condition") ?? "";
        Assert.Contains("IsOSPlatform(Linux)", condition);
        Assert.Contains("'$(RuntimeIdentifier)' == ''", condition);
        Assert.Contains("'$(KhaozEngineFlattenHostNatives)' == 'true'", condition);

        XElement item = target.Elements("ItemGroup").Elements("ResolvedFileToPublish").Single();
        Assert.Equal("@(RuntimeTargetsCopyLocalItems)", (string?)item.Attribute("Include"));
        // Flat relative path is the whole point: the default would keep runtimes/<rid>/native.
        Assert.Equal("%(Filename)%(Extension)", (string?)item.Element("RelativePath"));
    }

    [Fact]
    public void TheRuleLivesOnce_ImportedByFoundationsTargets_AndByTheRepoBuild()
    {
        // The engine's own projects consume the package's rule through an import rather than a second copy,
        // because build/ files do not flow across a ProjectReference.
        string foundation = Path.Combine(RepoRoot(), "KhaozEngine.Foundation", "build",
            "KhaozEngine.Foundation.targets");
        Assert.Contains(XDocument.Load(foundation).Root!.Elements("Import"),
            i => ((string?)i.Attribute("Project"))?.EndsWith(RuleFile, System.StringComparison.Ordinal) == true);

        string repo = Path.Combine(RepoRoot(), "Directory.Build.targets");
        Assert.Contains(XDocument.Load(repo).Root!.Elements("Import"),
            i => ((string?)i.Attribute("Project"))?.EndsWith(RuleFile, System.StringComparison.Ordinal) == true);

        // And the repo does NOT keep its own copy of the target, which is what would drift.
        Assert.DoesNotContain("KhaozEngineCopyHostNativesToOutput", File.ReadAllText(repo));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("buildTransitive")]
    public void Csproj_PacksTheRuleFile_NextToTheAutoImportedOne(string folder)
    {
        // NuGet auto-imports only <PackageId>.props|.targets, so the rule file rides in on the Import inside
        // KhaozEngine.Foundation.targets and has to ship in the SAME folder, in both copies.
        string csproj = Path.Combine(RepoRoot(), "KhaozEngine.Foundation", "KhaozEngine.Foundation.csproj");

        bool packed = XDocument.Load(csproj).Descendants("None").Any(n =>
            (string?)n.Attribute("Include") == $"build/{RuleFile}" &&
            (string?)n.Attribute("Pack") == "true" &&
            (string?)n.Attribute("PackagePath") == $"{folder}/{RuleFile}");

        Assert.True(packed, $"csproj must pack build/{RuleFile} to {folder}/{RuleFile}");
    }

    [Theory]
    [InlineData("KhaozEngine.Game2D", "KhaozEngine.Foundation")]
    [InlineData("KhaozEngine.Game3D", "KhaozEngine.Game2D")]
    [InlineData("KhaozEngine.Server", "KhaozEngine.Foundation")]
    public void UmbrellaEdge_KeepsTheBuildAssetFlowing(string umbrella, string referenced)
    {
        // THE failure this test exists for. A ProjectReference packs with PrivateAssets
        // contentfiles;analyzers;build by default, which stamps exclude="Build,Analyzers" on the packed
        // dependency, and a head that references an umbrella rather than Foundation itself then receives none
        // of Foundation's build/ folder. That silently dropped the CETCompat and single-file defaults for years
        // and would have dropped this rule too. Analyzers stay private: they travel on their own include="All"
        // dependencies.
        string csproj = Path.Combine(RepoRoot(), umbrella, $"{umbrella}.csproj");

        XElement edge = XDocument.Load(csproj).Descendants("ProjectReference").Single(r =>
            ((string?)r.Attribute("Include"))?.EndsWith($"{referenced}.csproj", System.StringComparison.Ordinal) == true);

        string privateAssets = (string?)edge.Attribute("PrivateAssets") ?? "";
        Assert.NotEqual("", privateAssets);
        Assert.DoesNotContain("build", privateAssets.ToLowerInvariant());
    }
}
