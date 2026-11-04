using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Frozen hosted FXC listings and source pins for every opaque foliage fragment variant.</summary>
public sealed class FoliageOpaqueDxbcFixtureTests
{
    internal static ShippedGraphicsProgram[] Programs => ShippedShaderPrograms.GraphicsPrograms()
        .Where(program => program.Name.StartsWith("FoliageOpaque", StringComparison.Ordinal)).ToArray();

    public static IEnumerable<object[]> Cases => Programs.Select(program => new object[] { program.Name });
    static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "foliage-opaque-dxbc");

    [Fact]
    public void FixturesCoverEveryShippedOpaqueFoliageVariant()
    {
        Assert.Equal(4, Programs.Length);
        string[] expected = Programs.SelectMany(program => new[] { program.Name + ".dxbc.gz",
            program.Name + ".glsl.sha256" }).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, Directory.GetFiles(Fixtures).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void HostedFxcListingMatchesCurrentSource(string name)
    {
        string source = Programs.Single(program => program.Name == name).FragmentGlsl;
        Assert.Equal(TemporalVisibilityFrozenD.Sha256(source), File.ReadAllText(Path.Combine(Fixtures, name + ".glsl.sha256")).Trim());
        using var input = File.OpenRead(Path.Combine(Fixtures, name + ".dxbc.gz"));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        string listing = reader.ReadToEnd();
        Assert.Contains("ps_5_0", listing);
        if (OperatingSystem.IsWindows())
            Assert.Equal(listing, D3D11FxcValidationTests.Disassemble(source, name).ReplaceLineEndings("\n"));
    }
}
