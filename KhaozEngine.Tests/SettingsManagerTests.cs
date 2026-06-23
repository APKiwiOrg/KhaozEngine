using System;
using System.IO;
using KhaozEngine.App;
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

    [Fact]
    public void Load_CorruptFileThroughRealStorage_FallsBackToDefaults()
    {
        string root = Path.Combine(Path.GetTempPath(), "ke-item10-" + Path.GetRandomFileName());
        try
        {
            var env = new FakeAppDataEnvironment { IsMacOS = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;
            var paths = new AppDataPaths("APKiwi", "Item10Settings", env);
            File.WriteAllText(paths.GetFilePath("settings.json"), "not-json{{");

            var storage = new FileSettingsStorage(paths, new PersistenceQueue());
            var logger = new FakeLogger();

            var manager = new SettingsManager<Prefs>(storage, logger);

            Assert.Equal(0, manager.Settings.Volume);   // defaults despite corrupt file
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Tests for the sanitizeOnLoad hook.

    private sealed class Box { public int Value { get; set; } }

    private sealed class VersionedBox : ISchemaVersioned
    {
        public int SchemaVersion { get; set; }
        public int Value { get; set; }
    }

    [Fact]
    public void Migrations_RunOnLoad_BeforeSanitize()
    {
        // Box starts at version 1 with Value 1. Chain bumps Value to 10 (v1->v2), sanitize then clamps to <= 5.
        var storage = new FakeStorage { ToLoad = new VersionedBox { SchemaVersion = 1, Value = 1 } };
        var chain = MigrationChain.For<VersionedBox>()
            .Step(1, b => { b.Value = 10; return b; })
            .Build(2);

        var mgr = new SettingsManager<VersionedBox>(
            storage, logger: null,
            sanitizeOnLoad: b => { b.Value = Math.Min(b.Value, 5); return b; },
            migrations: chain);

        Assert.Equal(2, mgr.Settings.SchemaVersion);   // chain ran
        Assert.Equal(5, mgr.Settings.Value);           // sanitize ran AFTER the chain (10 -> clamp 5)
    }

    [Fact]
    public void Migrations_RunOnInitialCtorLoad_NoSanitize()
    {
        var storage = new FakeStorage { ToLoad = new VersionedBox { SchemaVersion = 1, Value = 0 } };
        var chain = MigrationChain.For<VersionedBox>()
            .Step(1, b => { b.Value = 42; return b; })
            .Build(2);

        var mgr = new SettingsManager<VersionedBox>(storage, logger: null, sanitizeOnLoad: null, migrations: chain);

        Assert.Equal(2, mgr.Settings.SchemaVersion);
        Assert.Equal(42, mgr.Settings.Value);
    }

    [Fact]
    public void Migrations_Null_BehaviourUnchanged()
    {
        var storage = new FakeStorage { ToLoad = new VersionedBox { SchemaVersion = 1, Value = 7 } };
        var mgr = new SettingsManager<VersionedBox>(storage);   // no chain, no sanitize
        Assert.Equal(1, mgr.Settings.SchemaVersion);            // untouched
        Assert.Equal(7, mgr.Settings.Value);
    }

    [Fact]
    public void SanitizeOnLoad_RunsOnInitialCtorLoad()
    {
        var storage = new FakeStorage { ToLoad = new Box { Value = 999 } };
        var mgr = new SettingsManager<Box>(storage, logger: null, sanitizeOnLoad: b => { b.Value = Math.Min(b.Value, 100); return b; });
        Assert.Equal(100, mgr.Settings.Value);   // clamped on the FIRST load, before any caller could subscribe
    }

    [Fact]
    public void SanitizeOnLoad_RunsOnReload()
    {
        var storage = new FakeStorage { ToLoad = new Box { Value = 5 } };
        var mgr = new SettingsManager<Box>(storage, logger: null, sanitizeOnLoad: b => { b.Value += 1; return b; });
        Assert.Equal(6, mgr.Settings.Value);
        storage.ToLoad = new Box { Value = 50 };
        mgr.Load();
        Assert.Equal(51, mgr.Settings.Value);    // hook ran again on reload
    }

    [Fact]
    public void SanitizeOnLoad_Null_IsPassthrough()
    {
        var storage = new FakeStorage { ToLoad = new Box { Value = 7 } };
        var mgr = new SettingsManager<Box>(storage);   // no hook
        Assert.Equal(7, mgr.Settings.Value);
    }

    [Fact]
    public void SanitizeOnLoad_ClampedValueIsWhatSettingsExposes()
    {
        var storage = new FakeStorage { ToLoad = new Box { Value = -40 } };
        var mgr = new SettingsManager<Box>(storage, logger: null, sanitizeOnLoad: b => { b.Value = Math.Max(b.Value, 0); return b; });
        Assert.Equal(0, mgr.Settings.Value);
    }

    [Fact]
    public void SanitizeOnLoad_HookThrows_UsesUnsanitizedValue_AndLogsError()
    {
        var storage = new FakeStorage { ToLoad = new Box { Value = 42 } };
        var logger = new FakeLogger();
        var mgr = new SettingsManager<Box>(storage, logger, sanitizeOnLoad: _ => throw new InvalidOperationException("bad hook"));
        Assert.Equal(42, mgr.Settings.Value);   // throw swallowed; unsanitized value used
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    // GameStorage integration tests (Task 6).

    private sealed class SaveDoc : ISchemaVersioned
    {
        public int SchemaVersion { get; set; }
        public System.Collections.Generic.List<string> Items { get; set; } = new();
    }

    [Fact]
    public void GameStorage_Load_WithChain_MigratesOldFileToCurrent()
    {
        string root = Path.Combine(Path.GetTempPath(), "ke-migrate-" + Path.GetRandomFileName());
        try
        {
            var env = new FakeAppDataEnvironment { IsMacOS = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;
            var paths = new AppDataPaths("APKiwi", "MigrateSave", env);
            File.WriteAllText(paths.GetFilePath("save.json"), "{\"SchemaVersion\":1,\"Items\":[]}");

            using var storage = new GameStorage(paths);
            var chain = MigrationChain.For<SaveDoc>()
                .Step(1, s => { s.Items.Add("from-v1"); return s; })
                .Build(2);

            var loaded = storage.Load<SaveDoc>("save.json", chain);

            Assert.Equal(2, loaded.SchemaVersion);
            Assert.Contains("from-v1", loaded.Items);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GameStorage_CreateSettingsManager_ForwardsChain()
    {
        string root = Path.Combine(Path.GetTempPath(), "ke-migrate-sm-" + Path.GetRandomFileName());
        try
        {
            var env = new FakeAppDataEnvironment { IsMacOS = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;
            var paths = new AppDataPaths("APKiwi", "MigrateSm", env);

            using var storage = new GameStorage(paths);
            File.WriteAllText(paths.GetFilePath(storage.Settings.SettingsFileName), "{\"SchemaVersion\":1,\"Value\":3}");

            var chain = MigrationChain.For<VersionedBox>()
                .Step(1, b => { b.Value += 100; return b; })
                .Build(2);

            var mgr = storage.CreateSettingsManager<VersionedBox>(sanitizeOnLoad: null, migrations: chain);

            Assert.Equal(2, mgr.Settings.SchemaVersion);
            Assert.Equal(103, mgr.Settings.Value);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
