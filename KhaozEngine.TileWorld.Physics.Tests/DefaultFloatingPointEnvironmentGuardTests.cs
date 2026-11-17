using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

// Linked into both floating point test assemblies, which carry the same guard.
public sealed class DefaultFloatingPointEnvironmentGuardTests
{
    // The guard is an assembly attribute, so removing it would leave every other test green. Its Before runs ahead
    // of this test's body, so a registered guard has counted at least this test.
    [Fact]
    public void TheAssemblyGuardCheckedThisTestBeforeItRan() =>
        Assert.True(DefaultFloatingPointEnvironmentAttribute.BeforeCountForTests > 0,
            "The DefaultFloatingPointEnvironment assembly attribute is not registered on this test assembly.");
}
