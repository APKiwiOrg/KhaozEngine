using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class NavBakeTerrainIdentityTests
{
    [Theory]
    [InlineData("capture", NavBakeLoadStatus.OptionsChanged)]
    [InlineData("profile", NavBakeLoadStatus.ProfilesChanged)]
    [InlineData("engine", NavBakeLoadStatus.EngineChanged)]
    public void TerrainIdentityUsesTheExistingDifferencePriority(string changed, NavBakeLoadStatus expected)
    {
        var profile = new NavBakeProfile("player", ContactDropFixture.Tuning, default)
        { MaxDropMetres = 6, AllowTerrainDescents = true };
        var original = new NavBakeExpectation(ContactDropFixture.Options with { CaptureTerrainDescentEvidence = true },
            ContactBakeFile.Sources(), [profile]);
        var want = changed == "capture" ? original with
        { Options = original.Options with { CaptureTerrainDescentEvidence = false }, Profiles = [profile with { AllowTerrainDescents = false }] } :
            original with { Profiles = [profile with { AllowTerrainDescents = false }] };
        var stored = NavBakeIdentity.Encode(original, "terrain-engine");
        var desired = NavBakeIdentity.Encode(want, changed == "engine" ? "another-engine" : "terrain-engine");
        Assert.Equal(expected, NavBakeIdentity.Compare(stored, desired, 4, changed == "capture" ? (ushort)3 : (ushort)4).Status);
    }
}
