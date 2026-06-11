using System;
using KhaozEngine.Diagnostics;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class SettingsManagerTests
{
    private sealed class Prefs
    {
        public int Volume { get; set; }
    }

    // In-memory ISettingsStorage with optional fault injection.
    private sealed class FakeStorage : ISettingsStorage
    {
        public string SettingsFileName { get; set; } = "settings.json";
        public object? Saved;
        public object? ToLoad;
        public bool ThrowOnSave;
        public bool ThrowOnLoad;

        public void SaveSettings<T>(T settings) where T : new()
        {
            if (ThrowOnSave) throw new InvalidOperationException("save boom");
            Saved = settings;
        }

        public T LoadSettings<T>() where T : new()
        {
            if (ThrowOnLoad) throw new InvalidOperationException("load boom");
            return ToLoad is T typed ? typed : new T();
        }

        public bool SettingsExist() => ToLoad is not null;
    }

    [Fact]
    public void Ctor_NullStorage_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SettingsManager<Prefs>(null!));
    }

    [Fact]
    public void Ctor_LoadsFromStorage_AndRaisesSettingsLoaded()
    {
        var storage = new FakeStorage { ToLoad = new Prefs { Volume = 9 } };
        Prefs? loaded = null;

        // Subscribe before construction is impossible (Load runs in ctor); assert via Settings,
        // then verify the event fires on an explicit reload.
        var manager = new SettingsManager<Prefs>(storage);
        Assert.Equal(9, manager.Settings.Volume);

        manager.SettingsLoaded += p => loaded = p;
        manager.Load();
        Assert.NotNull(loaded);
        Assert.Equal(9, loaded!.Volume);
    }

    [Fact]
    public void Save_PersistsAndRaisesSettingsSaved()
    {
        var storage = new FakeStorage();
        var manager = new SettingsManager<Prefs>(storage);
        manager.Settings.Volume = 3;
        Prefs? saved = null;
        manager.SettingsSaved += p => saved = p;

        manager.Save();

        Assert.Same(manager.Settings, storage.Saved);
        Assert.Same(manager.Settings, saved);
    }

    [Fact]
    public void Load_StorageThrows_UsesDefaults_AndLogsError()
    {
        var storage = new FakeStorage { ThrowOnLoad = true };
        var logger = new FakeLogger();

        var manager = new SettingsManager<Prefs>(storage, logger);

        Assert.Equal(0, manager.Settings.Volume);   // defaults
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void Load_StorageThrows_RaisesSettingsLoaded_WithDefaults()
    {
        var storage = new FakeStorage { ThrowOnLoad = true };
        var manager = new SettingsManager<Prefs>(storage);
        Prefs? loadedArg = null;
        manager.SettingsLoaded += p => loadedArg = p;

        manager.Load();   // failure path must still fire SettingsLoaded with defaults

        Assert.NotNull(loadedArg);
        Assert.Equal(0, loadedArg!.Volume);
    }

    [Fact]
    public void Save_StorageThrows_Swallowed_AndLogsError()
    {
        var storage = new FakeStorage { ThrowOnSave = true };
        var logger = new FakeLogger();
        var manager = new SettingsManager<Prefs>(storage, logger);
        bool savedRaised = false;
        manager.SettingsSaved += _ => savedRaised = true;

        manager.Save();   // must not throw

        Assert.False(savedRaised);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }
}
