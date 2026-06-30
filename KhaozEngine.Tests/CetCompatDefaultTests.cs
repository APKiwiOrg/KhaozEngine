using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Guards the "CET off on game heads" engine standard: <c>KhaozEngine.Foundation</c> ships an
/// overridable <c>CETCompat=false</c> default via buildTransitive props, plus a build-log message
/// announcing it. These are MSBuild / packaging assets (not <c>InputState</c> runtime behaviour),
/// so the contract is asserted against the on-disk files rather than a headless InputManager frame.
/// The silent failure mode this guards against is a rename/typo that stops NuGet auto-importing the
/// asset (NuGet auto-imports <c>buildTransitive/&lt;PackageId&gt;.props|.targets</c> by exact
/// name/path), which would quietly drop CET-off from every consumer head.
/// </summary>
public class CetCompatDefaultTests
{
    // [CallerFilePath] is the compile-time path of this source file, so it locates the repo tree
    // regardless of `dotnet test`'s working directory (same trick GoldenCompare.GoldenPath uses).
    static string FoundationDir([CallerFilePath] string thisFile = "")
    {
        string repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!; // <repo>/KhaozEngine.Tests -> <repo>
        return Path.Combine(repoRoot, "KhaozEngine.Foundation");
    }

    [Fact]
    public void PropsFile_DefaultsCetCompatOff_AndStaysOverridable()
    {
        string path = Path.Combine(FoundationDir(), "build", "KhaozEngine.Foundation.props");
        Assert.True(File.Exists(path), $"Missing {path}");

        XElement group = XDocument.Load(path).Root!
            .Elements("PropertyGroup").Single(g => g.Elements("CETCompat").Any());

        // Overridable: the default only applies when the head has not pinned CETCompat itself.
        Assert.Equal("'$(CETCompat)' == ''", (string?)group.Attribute("Condition"));
        Assert.Equal("false", (string?)group.Element("CETCompat"));
        // Marker that gates the build-log message (set only when the inherited default actually wins).
        Assert.Equal("true", (string?)group.Element("_KhaozEngineCetDefaulted"));
    }

    [Fact]
    public void PropsFile_DefaultsNativeLibsLoose_AndStaysOverridable()
    {
        // Single-file publish (PublishSingleFile=true) defaults IncludeNativeLibrariesForSelfExtract=true,
        // which bundles GLFW/Veldrid/OpenAL into the self-extracting exe where Silk.NET's loader can't find
        // them ("Couldn't find a suitable window platform"). Foundation defaults it off so the natives stay
        // loose. Same buildTransitive asset / silent-drop failure mode as the CET default, hence guarded here.
        string path = Path.Combine(FoundationDir(), "build", "KhaozEngine.Foundation.props");
        Assert.True(File.Exists(path), $"Missing {path}");

        XElement group = XDocument.Load(path).Root!
            .Elements("PropertyGroup").Single(g => g.Elements("IncludeNativeLibrariesForSelfExtract").Any());

        // Overridable: applies only when the head has not pinned the property itself.
        Assert.Equal("'$(IncludeNativeLibrariesForSelfExtract)' == ''", (string?)group.Attribute("Condition"));
        Assert.Equal("false", (string?)group.Element("IncludeNativeLibrariesForSelfExtract"));
    }

    [Fact]
    public void TargetsFile_AnnouncesTheDefault_OnlyWhenItWins()
    {
        string path = Path.Combine(FoundationDir(), "build", "KhaozEngine.Foundation.targets");
        Assert.True(File.Exists(path), $"Missing {path}");

        XElement target = XDocument.Load(path).Root!
            .Elements("Target").Single(t => (string?)t.Attribute("Name") == "KhaozEngineReportCetDefault");

        // Dual-gated: our default applied (marker) AND it survived to the final value (still off).
        // The second clause is what suppresses the message when a head overrides to true in its
        // .csproj body, which runs after the props marker is already set.
        Assert.Equal("'$(_KhaozEngineCetDefaulted)' == 'true' and '$(CETCompat)' == 'false'",
            (string?)target.Attribute("Condition"));
        Assert.Contains(target.Elements("Message"),
            m => ((string?)m.Attribute("Text"))?.Contains("CETCompat") == true);
    }

    [Theory]
    [InlineData("KhaozEngine.Foundation.props")]
    [InlineData("KhaozEngine.Foundation.targets")]
    public void Csproj_PacksAsset_ToBuildTransitive(string file)
    {
        // buildTransitive/ is what makes the default flow to heads that pull Foundation transitively
        // via Game2D/Game3D (not just direct references). Without it, Nullwake and SpaceGame would
        // silently NOT inherit CET-off.
        string csproj = Path.Combine(FoundationDir(), "KhaozEngine.Foundation.csproj");

        bool packedTransitive = XDocument.Load(csproj).Descendants("None").Any(n =>
            (string?)n.Attribute("Include") == $"build/{file}" &&
            (string?)n.Attribute("Pack") == "true" &&
            (string?)n.Attribute("PackagePath") == $"buildTransitive/{file}");

        Assert.True(packedTransitive, $"csproj must pack build/{file} to buildTransitive/{file}");
    }
}
