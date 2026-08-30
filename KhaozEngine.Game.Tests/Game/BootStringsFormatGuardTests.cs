using KhaozEngine.App;
using KhaozEngine.Game;
using Xunit;

namespace KhaozEngine.Tests.Game;

/// <summary>
/// <see cref="BootStrings.EnglishDefaults"/> formatted with a bare string.Format, which threw on a template its
/// arguments could not satisfy, out of the boot screen's own draw (#163). It routes through
/// <see cref="IStringCatalog.SafeFormat"/> now. Every boot default is placeholder-free, so the malformed
/// template this catalog can actually be handed is an absent key, which Get returns verbatim by contract.
/// </summary>
public class BootStringsFormatGuardTests
{
    [Fact]
    public void Absent_key_carrying_a_placeholder_falls_back_to_the_key()
    {
        Assert.Equal("boot.{0}.missing", BootStrings.EnglishDefaults.Format("boot.{0}.missing"));
    }

    [Fact]
    public void Present_key_still_resolves()
    {
        Assert.Equal("Starting", BootStrings.EnglishDefaults.Format("boot.title"));
    }
}
