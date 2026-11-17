using System;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;
using S = KhaozEngine.TileWorld.Physics.TileWaterPreparedQuery.BasisState;
using Row = KhaozEngine.TileWorld.Physics.TileWaterSharedProjections.Row;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed partial class WaterSharedProjectionTests(ITestOutputHelper output)
{
    // The other rows run in KhaozEngine.TileWorld.Physics.Tests.
    [Theory]
    [InlineData("control-token")]
    public void AnyChangedKeyFieldMissesAndRunsTheOriginalExpressions(string field) =>
        AnyChangedKeyField(field, WaterFloatingPointEnvironment.AnyMode);

    [Theory]
    [InlineData("wrong-control")]
    public void DefeatingOneSharingCheckBreaksFrozenParity(string fault) =>
        DefeatingOneSharingCheck(fault, output, WaterFloatingPointEnvironment.AnyMode);
}
