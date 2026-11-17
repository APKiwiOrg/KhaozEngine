using System;
using System.Runtime.CompilerServices;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// The forced-off corpus qualification sets KHAOZENGINE_WATER_SHARED_PROJECTIONS=bypass, so every test that does
// not hold the bypass itself runs the original projection path on identical inputs. Default runs share.
internal static class SharedProjectionEnvironment
{
    internal const string Variable = "KHAOZENGINE_WATER_SHARED_PROJECTIONS";

    [ModuleInitializer]
    internal static void Apply()
    {
        if (Environment.GetEnvironmentVariable(Variable) == "bypass") TileWaterSharedProjections.BypassForTests = true;
    }
}
