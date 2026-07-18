using System;
using System.IO;
using System.Text;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class GameStorageTests
{
    public sealed class Save
    {
        public string Name { get; set; } = "";
        public int Level { get; set; }
    }

    public sealed class Prefs
    {
        public int Volume { get; set; } = 5;
    }

    public sealed class TestSave
    {
        public int Score { get; set; }
    }

    private static GameStorage NewStorage(out string root, GameStorageOptions? options = null)
    {
        root = Path.Combine(Path.GetTempPath(), "ke-gamestorage-" + Path.GetRandomFileName());
        var env = new FakeAppDataEnvironment { IsMacOS = true };
        env.Folders[Environment.SpecialFolder.ApplicationData] = root;
        // Build AppDataPaths with the fake env (KhaozEngine.App exposes internals to the test
        // assembly), then hand it to the public AppDataPaths-accepting GameStorage ctor.
        var paths = new AppDataPaths("APKiwi", "TestGame", env);
        return new GameStorage(paths, options);
    }

    private static GameStorage NewEncodedStorage(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "ke-gamestorage-" + Path.GetRandomFileName());
        var env = new FakeAppDataEnvironment { IsMacOS = true };
        env.Folders[Environment.SpecialFolder.ApplicationData] = root;
        var encoder = new SaveEncoder(new byte[] { 1, 2, 3, 4 }, "KESAVE");
        var paths = new AppDataPaths("APKiwi", "TestGame", env);
        return new GameStorage(paths, new GameStorageOptions { Encoder = encoder });
    }

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best-effort */ }
    }

    // dir is the resolved BaseDirectory (root/APKiwi/TestGame), not the fake ApplicationData root,
    // so callers can Path.Combine(dir, fileName) directly against what GameStorage writes to.
    private static GameStorage CreateStorage(out string dir, Action<GameStorageOptions>? configure = null)
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        var env = new FakeAppDataEnvironment { IsMacOS = true };
        env.Folders[Environment.SpecialFolder.ApplicationData] = root;
        var paths = new AppDataPaths("APKiwi", "TestGame", env);
        dir = paths.BaseDirectory;

        var options = new GameStorageOptions { BackupGenerations = 2 };
        configure?.Invoke(options);
        return new GameStorage(paths, options);
    }

    private static GameStorage CreateStorageWithEncoder(out SaveEncoder encoder, out string dir, Action<GameStorageOptions>? configure = null)
    {
        var localEncoder = new SaveEncoder(Encoding.UTF8.GetBytes("gs-test-key"), "GSV1");
        encoder = localEncoder;
        return CreateStorage(out dir, o => { o.Encoder = localEncoder; configure?.Invoke(o); });
    }

    [Fact]
    public void Paths_AreRootedUnderPublisherAndApp()
    {
        var storage = NewStorage(out string root);
        try
        {
            Assert.Equal(Path.Combine(root, "APKiwi", "TestGame"), storage.Paths.BaseDirectory);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void SaveThenLoad_PlaintextRoundTrips()
    {
        var storage = NewStorage(out string root);
        try
        {
            storage.Save("save.json", new Save { Name = "Ada", Level = 7 });
            storage.Flush();

            Save loaded = storage.Load<Save>("save.json");
            Assert.Equal("Ada", loaded.Name);
            Assert.Equal(7, loaded.Level);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void Load_AbsentFile_ReturnsNewInstance()
    {
        var storage = NewStorage(out string root);
        try
        {
            Save loaded = storage.Load<Save>("missing.json");
            Assert.NotNull(loaded);
            Assert.Equal("", loaded.Name);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void Exists_And_Delete()
    {
        var storage = NewStorage(out string root);
        try
        {
            Assert.False(storage.Exists("save.json"));

            storage.Save("save.json", new Save { Name = "x", Level = 1 });
            storage.Flush();
            Assert.True(storage.Exists("save.json"));

            storage.Delete("save.json");
            Assert.False(storage.Exists("save.json"));

            // Deleting an absent file is a no-op, not an error.
            storage.Delete("save.json");
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void Settings_SaveThenLoad_RoundTrips()
    {
        var storage = NewStorage(out string root);
        try
        {
            storage.Settings.SaveSettings(new Prefs { Volume = 9 });
            storage.Flush();

            Prefs loaded = storage.Settings.LoadSettings<Prefs>();
            Assert.Equal(9, loaded.Volume);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void Dispose_FlushesPendingWrites()
    {
        var storage = NewStorage(out string root);
        try
        {
            storage.Save("save.json", new Save { Name = "Grace", Level = 3 });
            storage.Dispose(); // must flush before returning

            string path = Path.Combine(root, "APKiwi", "TestGame", "save.json");
            Assert.True(File.Exists(path));
            Assert.Contains("Grace", File.ReadAllText(path));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SaveEncoded_ThenLoad_RoundTripsAndFileIsEncodedOnDisk()
    {
        var storage = NewEncodedStorage(out string root);
        try
        {
            storage.Save("save.json", new Save { Name = "Lin", Level = 4 }, encode: true);
            storage.Flush();

            // On-disk content is in the encoded format (not raw JSON).
            string raw = File.ReadAllText(Path.Combine(root, "APKiwi", "TestGame", "save.json"));
            Assert.StartsWith("KESAVE:", raw);
            Assert.DoesNotContain("\"Name\"", raw);

            // Load decodes transparently (no flag passed).
            Save loaded = storage.Load<Save>("save.json");
            Assert.Equal("Lin", loaded.Name);
            Assert.Equal(4, loaded.Level);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void Load_PlaintextFile_WithEncoderConfigured_StillReadsAsJson()
    {
        var storage = NewEncodedStorage(out string root);
        try
        {
            // Write plaintext (encode: false) even though an encoder is configured.
            storage.Save("save.json", new Save { Name = "Plain", Level = 1 }, encode: false);
            storage.Flush();

            Save loaded = storage.Load<Save>("save.json");
            Assert.Equal("Plain", loaded.Name);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void SaveEncoded_WithoutEncoder_Throws()
    {
        var storage = NewStorage(out string root);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                storage.Save("save.json", new Save { Name = "x", Level = 1 }, encode: true));
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void CreateSettingsManager_LoadsExistingSettingsOnConstruct()
    {
        var storage = NewStorage(out string root);
        try
        {
            storage.Settings.SaveSettings(new Prefs { Volume = 8 });
            storage.Flush();

            var manager = storage.CreateSettingsManager<Prefs>();
            Assert.Equal(8, manager.Settings.Volume);
        }
        finally { storage.Dispose(); Cleanup(root); }
    }

    [Fact]
    public void Save_EncoderConfigured_EncodesByDefault()
    {
        using GameStorage storage = CreateStorageWithEncoder(out SaveEncoder encoder, out string dir);
        storage.Save("slot.json", new TestSave { Score = 7 });
        storage.Flush();

        string raw = File.ReadAllText(Path.Combine(dir, "slot.json"));
        Assert.True(encoder.IsEncoded(raw));
        SaveDecodeResult r = encoder.TryDecode(raw);
        Assert.Equal(SaveDecodeVerdict.Ok, r.Verdict);
        Assert.NotNull(r.Metadata);
    }

    [Fact]
    public void Save_WriteOptionsOptOut_WritesPlaintext()
    {
        using GameStorage storage = CreateStorageWithEncoder(out SaveEncoder encoder, out string dir);
        storage.Save("slot.json", new TestSave { Score = 3 }, new SaveWriteOptions { Encode = false });
        storage.Flush();

        string raw = File.ReadAllText(Path.Combine(dir, "slot.json"));
        Assert.StartsWith("{", raw);
    }

    [Fact]
    public void Save_BoolFalse_ForcesPlaintext()
    {
        using GameStorage storage = CreateStorageWithEncoder(out SaveEncoder encoder, out string dir);
        storage.Save("slot.json", new TestSave { Score = 3 }, false);
        storage.Flush();

        string raw = File.ReadAllText(Path.Combine(dir, "slot.json"));
        Assert.StartsWith("{", raw);
    }

    [Fact]
    public void Save_NoEncoder_WritesPlaintext()
    {
        using GameStorage storage = CreateStorage(out string dir);
        storage.Save("slot.json", new TestSave { Score = 5 });
        storage.Flush();

        string raw = File.ReadAllText(Path.Combine(dir, "slot.json"));
        Assert.StartsWith("{", raw);
    }

    [Fact]
    public void Save_MetadataCarriesGameVersionAndSummary()
    {
        using GameStorage storage = CreateStorageWithEncoder(out SaveEncoder encoder, out string dir, o => o.GameVersion = "2.0.0");
        storage.Save("slot.json", new TestSave { Score = 9 }, new SaveWriteOptions { Summary = "boss" });
        storage.Flush();

        string raw = File.ReadAllText(Path.Combine(dir, "slot.json"));
        SaveDecodeResult r = encoder.TryDecode(raw);
        Assert.Equal("2.0.0", r.Metadata?.GameVersion);
        Assert.Equal("boss", r.Metadata?.Summary);
    }

    [Fact]
    public void Save_EncodeTrueWithoutEncoder_Throws()
    {
        using GameStorage storage = CreateStorage(out string dir);
        Assert.Throws<InvalidOperationException>(() =>
            storage.Save("slot.json", new TestSave { Score = 1 }, true));
    }
}
