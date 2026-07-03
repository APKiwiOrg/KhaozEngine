using KhaozEngine.Social;
using Xunit;

namespace KhaozEngine.Tests;

public class NullSocialProviderTests
{
    [Fact]
    public void NullProvider_IsSilentAndNeverThrows()
    {
        ISocialProvider social = new NullSocialProvider();

        Assert.False(social.TryInitialize("123"));
        social.SetPresence(new RichPresence { Details = "x", State = "y" });
        social.ClearPresence();
        social.Update();

        Assert.False(social.IsConnected);
        Assert.False(social.TryGetLocalUser(out SocialUser user));
        Assert.Equal(default, user);

        social.Dispose();
    }
}
