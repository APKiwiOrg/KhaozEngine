using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Fact]
    public void SceneFullPath_RigidFoliageRetainsDifferentWindowEnvelopes()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("foliage-stream-aliases", json =>
        {
            var windows = json["windows"]!;
            windows[1]!["minX"] = 30; windows[1]!["maxX"] = 38;
            windows[1]!["minZ"] = -5; windows[1]!["maxZ"] = 1;
            windows[2]!["minX"] = 0; windows[2]!["maxX"] = 64;
            windows[2]!["minZ"] = -64; windows[2]!["maxZ"] = 0;
        });
        var envelopes = new Dictionary<MapRenderKey, HashSet<MapBox3>>();
        MapDrawSet Observe(MapDrawSet set)
        {
            foreach (var resource in set.Resources.Where(r => r.Reference.Key.Kind == MapRenderKind.Foliage && r.Payload is MapRigidResource))
            {
                if (!envelopes.TryGetValue(resource.Reference.Key, out var bounds)) envelopes.Add(resource.Reference.Key, bounds = []);
                bounds.Add(resource.Bounds);
            }
            return set;
        }
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        PrivateOracleEntry.Run(fixture.Environment, (_, report) => ShippedSceneComparison.Compare(inputs, report,
            observe: observation => observation with { DrawSet = Observe(observation.DrawSet) }, observePlan: (_, set) => Observe(set)));
        Assert.Contains(envelopes.Values, bounds => bounds.Count > 1);
        Assert.Empty(fixture.FailureCategories());
    }
}
