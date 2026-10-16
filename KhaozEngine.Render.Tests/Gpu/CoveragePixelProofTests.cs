using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePixelProofTests
{
    [Theory]
    [InlineData(4, 5, false)]
    [InlineData(5, 4, false)]
    [InlineData(3, 5, true)]
    public void Background_classification_obeys_the_one_byte_colour_contract(byte actualRed, byte currentRed, bool significant)
    {
        Assert.Equal(significant, CoveragePixelProof.SignificantClassMismatch(
            [actualRed, 38, 9, 255], [currentRed, 38, 9, 255], 0));
    }

    [Fact]
    public void Original_skin_against_plate_is_a_substantial_class_mismatch()
    {
        Assert.True(CoveragePixelProof.SignificantClassMismatch([255, 160, 80, 255], [46, 255, 255, 255], 0));
    }
}
