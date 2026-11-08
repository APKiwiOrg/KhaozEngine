using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Identity;
using KhaozEngine.Tests.Gpu;
using Xunit;
using DiscordSignInException = KhaozEngine.Identity.Discord.IdentitySignInException;
using OidcSignInException = KhaozEngine.Identity.Oidc.IdentitySignInException;

namespace KhaozEngine.Tests.Identity;

public sealed class SignInRefusalConstructorFenceTests
{
    [Theory]
    [InlineData(typeof(SignInException))]
    [InlineData(typeof(OidcSignInException))]
    [InlineData(typeof(DiscordSignInException))]
    public void Engine_callers_always_supply_a_reason(Type carrier)
        => Assert.Empty(RefusalConstructorFence.EngineCallers(carrier == typeof(SignInException)
            ? [carrier.Assembly, typeof(OidcSignInException).Assembly, typeof(DiscordSignInException).Assembly]
            : [carrier.Assembly], LegacyConstructors(carrier)));

    [Theory]
    [InlineData(typeof(SignInException), nameof(BaseMessageControl), nameof(BaseInnerControl))]
    [InlineData(typeof(OidcSignInException), nameof(OidcMessageControl), nameof(OidcInnerControl))]
    [InlineData(typeof(DiscordSignInException), nameof(DiscordMessageControl), nameof(DiscordInnerControl))]
    public void Fence_finds_every_forbidden_constructor(Type carrier, string messageControl, string innerControl)
    {
        var callers = RefusalConstructorFence.EngineCallers([typeof(SignInRefusalConstructorFenceTests).Assembly], LegacyConstructors(carrier));
        Assert.Contains($"{typeof(SignInRefusalConstructorFenceTests).FullName}.{messageControl}", callers);
        Assert.Contains($"{typeof(SignInRefusalConstructorFenceTests).FullName}.{innerControl}", callers);
    }

    static ConstructorInfo[] LegacyConstructors(Type carrier) => carrier.GetConstructors()
        .Where(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(SignInRefusal)))
        .ToArray();

    static SignInException BaseMessageControl() => new("consumer refusal");
    static SignInException BaseInnerControl() => new("consumer refusal", new Exception());
    static OidcSignInException OidcMessageControl() => new("consumer refusal");
    static OidcSignInException OidcInnerControl() => new("consumer refusal", new Exception());
    static DiscordSignInException DiscordMessageControl() => new("consumer refusal");
    static DiscordSignInException DiscordInnerControl() => new("consumer refusal", new Exception());
}
