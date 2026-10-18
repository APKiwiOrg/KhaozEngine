using System;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageOracleAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void Warm_checks_allocate_nothing_across_success_refusal_and_recovery()
    {
        var (topology, _, model, commands, _) = CoverageCertificationTests.Scenario("valid");
        var oracle = new CoverageOracle(topology, CoverageOracleCases.IdentityBinds);
        var palette = CoverageFixture.Pose;
        CoverageCommandPose[] escaped = [CoverageOracleCases.Escaped(commands[0])];
        CoverageCommandPose[] budget = [commands[0] with { MaxWorldRecession = .1f }];
        var tight = CoverageOracleCases.TightCollar();
        var tightOracle = new CoverageOracle(tight.Topology, tight.InverseBind);
        var results = new bool[5];
        var failures = new CoverageOracleFailure[5];
        Action check = () =>
        {
            results[0] = oracle.TryCheck(palette, model, commands, out _, out failures[0]);
            results[1] = oracle.TryCheck(palette, model, escaped, out _, out failures[1]);
            results[2] = oracle.TryCheck(palette, model, budget, out _, out failures[2]);
            results[3] = tightOracle.TryCheck(tight.Palette, tight.Model, tight.Commands, out _, out failures[3]);
            results[4] = oracle.TryCheck(palette, model, commands, out _, out failures[4]);
        };
        check();
        long allocated = AllocationMeasurement.Measure("coverage oracle success, three refusals and recovery", check);
        output.WriteLine($"Warm oracle allocation: {allocated} bytes across five checks.");
        Assert.Equal(0, allocated);
        Assert.Equal(new[] { true, false, false, false, true }, results);
        Assert.Equal(default, failures[0]);
        Assert.Equal(default, failures[4]);
        Assert.Equal(CoverageOracleStage.Construction, failures[1].Stage);
        Assert.Equal(CoverageFailureCode.CoreEscaped, failures[1].Failure.Code);
        Assert.Equal(CoverageOracleStage.Construction, failures[2].Stage);
        Assert.Equal(CoverageFailureCode.RecessionExceeded, failures[2].Failure.Code);
        Assert.Equal(CoverageOracleStage.Certification, failures[3].Stage);
        Assert.Equal(CoverageFailureCode.EquipmentIntersection, failures[3].Failure.Code);
    }
}
