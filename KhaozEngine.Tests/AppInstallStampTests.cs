using System;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests;

public class AppInstallStampTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 6, 22, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Resolve_FirstRun_SetsBothDatesAndReportsChanged()
    {
        AppInstallStampResult result = AppInstallStamp.Resolve(previous: null, currentVersion: "1.0.0", utcNow: T0);

        Assert.True(result.Changed);
        Assert.Equal("1.0.0", result.Stamp.Version);
        Assert.Equal(T0, result.Stamp.FirstInstalledAtUtc);
        Assert.Equal(T0, result.Stamp.UpdatedAtUtc);
    }

    [Fact]
    public void Resolve_SameVersion_ReturnsPreviousUntouchedAndNotChanged()
    {
        var previous = new AppInstallStamp("1.0.0", T0, T0);

        AppInstallStampResult result = AppInstallStamp.Resolve(previous, currentVersion: "1.0.0", utcNow: T1);

        Assert.False(result.Changed);
        Assert.Same(previous, result.Stamp);
        Assert.Equal(T0, result.Stamp.UpdatedAtUtc);
    }

    [Fact]
    public void Resolve_Upgrade_PreservesFirstInstalledBumpsUpdatedAndVersion()
    {
        var previous = new AppInstallStamp("1.0.0", T0, T0);

        AppInstallStampResult result = AppInstallStamp.Resolve(previous, currentVersion: "1.1.0", utcNow: T1);

        Assert.True(result.Changed);
        Assert.Equal("1.1.0", result.Stamp.Version);
        Assert.Equal(T0, result.Stamp.FirstInstalledAtUtc);
        Assert.Equal(T1, result.Stamp.UpdatedAtUtc);
    }

    [Fact]
    public void Resolve_Downgrade_TreatedAsChangeSameAsUpgrade()
    {
        // Any version-string difference counts as a change; no semver ordering is compared,
        // so a downgrade preserves FirstInstalledAtUtc and bumps UpdatedAtUtc just like an upgrade.
        var previous = new AppInstallStamp("2.0.0", T0, T0);

        AppInstallStampResult result = AppInstallStamp.Resolve(previous, currentVersion: "1.9.0", utcNow: T1);

        Assert.True(result.Changed);
        Assert.Equal("1.9.0", result.Stamp.Version);
        Assert.Equal(T0, result.Stamp.FirstInstalledAtUtc);
        Assert.Equal(T1, result.Stamp.UpdatedAtUtc);
    }

    [Fact]
    public void Resolve_NullCurrentVersion_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AppInstallStamp.Resolve(previous: null, currentVersion: null!, utcNow: T0));
    }
}
