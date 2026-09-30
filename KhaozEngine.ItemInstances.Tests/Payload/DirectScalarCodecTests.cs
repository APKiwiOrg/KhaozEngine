using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Payload;

/// <summary>Registered scalar codecs also reject incomplete and extended bodies when called directly.</summary>
public class DirectScalarCodecTests
{
    [Theory]
    [InlineData(1, new byte[] { }, "field-truncated")]
    [InlineData(1, new byte[] { 0x01, 0x00 }, "field-malformed")]
    [InlineData(2, new byte[] { }, "field-truncated")]
    [InlineData(2, new byte[] { 0x01, 0x00 }, "field-malformed")]
    [InlineData(3, new byte[] { }, "field-truncated")]
    [InlineData(3, new byte[] { 0x00, 0x00 }, "field-malformed")]
    [InlineData(4, new byte[] { 0x00 }, "field-truncated")]
    [InlineData(4, new byte[] { 0x00, 0x00, 0x00 }, "field-malformed")]
    [InlineData(5, new byte[] { 0x00 }, "field-truncated")]
    [InlineData(5, new byte[] { 0x00, 0x00, 0x00 }, "field-malformed")]
    [InlineData(6, new byte[] { }, "field-truncated")]
    [InlineData(6, new byte[] { 0x01, 0x00 }, "field-malformed")]
    [InlineData(128, new byte[] { }, "field-malformed")]
    [InlineData(128, new byte[] { 0x00 }, "field-truncated")]
    [InlineData(128, new byte[] { 0x01 }, "field-truncated")]
    [InlineData(128, new byte[] { 0x00, 0x00, 0x00 }, "field-malformed")]
    public void A_registered_scalar_codec_requires_its_complete_body(int kind, byte[] body, string expectedReason)
    {
        // Skipping the read or the consumed-length check would accept an incomplete or extra slot.
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        Assert.True(registry.TryGet((ushort)kind, out InstancePropertyRegistration? registration));
        Assert.NotNull(registration);

        Assert.False(registration.Codec.TryValidate(body, out string? reason));
        Assert.Equal(expectedReason, reason);
    }
}
