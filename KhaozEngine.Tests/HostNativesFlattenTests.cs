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
/// <see cref="CetCompatDefaultTests"/> does. Four separate silent failure modes are covered: the file being
/// renamed out of the import, the csproj forgetting to pack it, an umbrella
/// <c>ProjectReference</c> suppressing the Build asset so it never reaches a consumer at all, and a
/// native-carrying package shipping without a copy of its own.
/// <para>
/// THE LAST ONE IS ISSUE 723. The 722 fix ships the rule in <c>KhaozEngine.Foundation</c> only, and
/// <c>Foundation</c> is not in the dependency closure of <c>KhaozEngine.Gpu</c>, <c>KhaozEngine.Windowing</c> or
/// <c>KhaozEngine.Audio</c>. A project that references one of those on its own and takes no umbrella, say a
/// Linux shader tool on <c>Gpu</c> alone, got nothing and still died with "Could not load from any of the
/// possible library names". The three packages that carry the natives now pack the SAME physical file under
/// their own <c>&lt;PackageId&gt;.targets</c> name, which NuGet auto-imports with no <c>Import</c> line needed.
/// Landing two copies in one build is an override rather than an error, because the target definitions are
/// byte-identical, and it changes asset flow nowhere: no <c>PrivateAssets</c> moved.
/// </para>
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

    /// <summary>
    /// EVERY PACKAGE THAT CARRIES A SILK.NET NATIVE SHIPS THE RULE ITSELF, under its own PackageId name so
    /// NuGet auto-imports it (issue 723). A consumer that takes one of these directly and no umbrella is the
    /// case Foundation cannot reach, because Foundation is in none of their dependency closures.
    /// </summary>
    [Theory]
    [InlineData("KhaozEngine.Gpu", "build")]
    [InlineData("KhaozEngine.Gpu", "buildTransitive")]
    [InlineData("KhaozEngine.Windowing", "build")]
    [InlineData("KhaozEngine.Windowing", "buildTransitive")]
    [InlineData("KhaozEngine.Audio", "build")]
    [InlineData("KhaozEngine.Audio", "buildTransitive")]
    public void NativeCarryingPackage_PacksTheRule_UnderItsOwnPackageIdName(string package, string folder)
    {
        string csproj = Path.Combine(RepoRoot(), package, $"{package}.csproj");

        bool packed = XDocument.Load(csproj).Descendants("None").Any(n =>
            ((string?)n.Attribute("Include"))?.Replace('\\', '/').EndsWith(
                $"KhaozEngine.Foundation/build/{RuleFile}", System.StringComparison.Ordinal) == true &&
            (string?)n.Attribute("Pack") == "true" &&
            (string?)n.Attribute("PackagePath") == $"{folder}/{package}.targets");

        Assert.True(packed,
            $"{package}.csproj must pack the ONE copy of {RuleFile} to {folder}/{package}.targets, so a "
            + "consumer that references it without an umbrella gets the Linux flatten (issue 723)");
    }

    /// <summary>
    /// AND THERE IS STILL EXACTLY ONE PHYSICAL COPY OF THE RULE. Six more pack entries is the cheap half of
    /// issue 723 only while they all point at the same file: a second copy on disk is the drift this whole
    /// class exists to stop, and it would be invisible until the two behaved differently on someone's Linux box.
    /// </summary>
    [Fact]
    public void TheRuleFileExistsExactlyOnceOnDisk()
    {
        string[] copies = Directory.GetFiles(RepoRoot(), RuleFile, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            System.StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            System.StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}local-feed{Path.DirectorySeparatorChar}",
                            System.StringComparison.Ordinal))
            .ToArray();

        Assert.True(copies.Length == 1,
            $"expected one {RuleFile} in the tree, found {copies.Length}: {string.Join(", ", copies)}");
    }
}
