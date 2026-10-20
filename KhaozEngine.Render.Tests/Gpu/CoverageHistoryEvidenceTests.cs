using System.IO;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageHistoryEvidenceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Unconfigured_directories_use_the_temporary_fallback(string? configured)
        => Assert.Equal(Path.Combine(Path.GetTempPath(), "grimhollow-equipment-coverage-task12"),
            CoverageHistoryEvidence.ResolveDirectory(configured));

    [Fact]
    public void An_explicit_directory_is_preserved()
    {
        string directory = Path.Combine(Path.GetTempPath(), "coverage-custom-evidence");
        Assert.Equal(directory, CoverageHistoryEvidence.ResolveDirectory(directory));
    }
}
