using System;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests.Persistence;

public class AppInstallStampPersistenceTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 6, 22, 9, 30, 0, DateTimeKind.Utc);

    private sealed class GameSettings
    {
        public int Volume { get; set; }
        public AppInstallStamp? Install { get; set; }
    }

    // In-memory storage that records how many times settings were saved.
    private sealed class MemoryStorage : ISettingsStorage
    {
        private object? stored;
        public int SaveCount { get; private set; }
        public string SettingsFileName { get; set; } = "settings.json";

        public void SaveSettings<T>(T settings) where T : new() { stored = settings; SaveCount++; }
        public T LoadSettings<T>() where T : new() => stored is T t ? t : new T();
        public bool SettingsExist() => stored is not null;
    }

    [Fact]
    public void StampInstall_FirstRun_WritesStampAndSaves()
    {
        var storage = new MemoryStorage();
        var manager = new SettingsManager<GameSettings>(storage);

        AppInstallStampResult result = manager.StampInstall(
            s => s.Install, (s, stamp) => s.Install = stamp, currentVersion: "1.0.0", utcNow: T0);

        Assert.True(result.Changed);
        Assert.Equal("1.0.0", manager.Settings.Install!.Version);
        Assert.Equal(T0, manager.Settings.Install.FirstInstalledAtUtc);
        Assert.Equal(1, storage.SaveCount);
    }

    [Fact]
    public void StampInstall_NoChange_DoesNotSave()
    {
        var storage = new MemoryStorage();
        var manager = new SettingsManager<GameSettings>(storage)
        {
            Settings = { Install = new AppInstallStamp("1.0.0", T0, T0) }
        };

        AppInstallStampResult result = manager.StampInstall(
            s => s.Install, (s, stamp) => s.Install = stamp, currentVersion: "1.0.0", utcNow: T1);

        Assert.False(result.Changed);
        Assert.Equal(T0, manager.Settings.Install!.UpdatedAtUtc);
        Assert.Equal(0, storage.SaveCount);
    }

    [Fact]
    public void StampInstall_Upgrade_BumpsUpdatedAndSaves()
    {
        var storage = new MemoryStorage();
        var manager = new SettingsManager<GameSettings>(storage)
        {
            Settings = { Install = new AppInstallStamp("1.0.0", T0, T0) }
        };

        AppInstallStampResult result = manager.StampInstall(
            s => s.Install, (s, stamp) => s.Install = stamp, currentVersion: "1.1.0", utcNow: T1);

        Assert.True(result.Changed);
        Assert.Equal("1.1.0", manager.Settings.Install!.Version);
        Assert.Equal(T0, manager.Settings.Install.FirstInstalledAtUtc);
        Assert.Equal(T1, manager.Settings.Install.UpdatedAtUtc);
        Assert.Equal(1, storage.SaveCount);
    }
}
