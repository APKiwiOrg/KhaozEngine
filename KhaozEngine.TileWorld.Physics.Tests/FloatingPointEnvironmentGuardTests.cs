using System;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public sealed class FloatingPointEnvironmentGuardTests
{
    // #1830. The assembly guard must see every control the shared fixture can change, beside the default control.
    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("zero")]
    [InlineData("flush-output")]
    [InlineData("flush-input")]
    public void ATestThatStartsOrEndsUnderAChangedControlFails(string mode)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        if (environment is null) return; // No native reader, so neither the fixture nor the guard can act.
        ulong? unchanged = WaterFloatingPointEnvironment.CurrentModes();
        if (!environment.Set(mode)) return; // flush-input has no independent control on arm64.
        // Only the read runs under the changed control. The guard itself runs after the restore.
        ulong? changed = WaterFloatingPointEnvironment.CurrentModes();
        environment.Restore();
        Assert.NotNull(unchanged);
        DefaultFloatingPointEnvironmentAttribute.Require(unchanged, "control", "started");
        Assert.Throws<InvalidOperationException>(() => DefaultFloatingPointEnvironmentAttribute.Require(changed, "test", "started"));
    }
}
