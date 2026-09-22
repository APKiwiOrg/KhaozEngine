using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

// The save posture is a required GameStorage constructor argument (#236): saves follow it, settings never do.
public sealed class GameStorageSaveEncodingTests : IDisposable
{
    public sealed class Progress
    {
        public int Level { get; set; }
    }

    public sealed class Prefs
    {
        public int Volume { get; set; } = 5;
    }

    private const string Prefix = "PSV1";
    private readonly string root = Directory.CreateTempSubdirectory("ke-save-encoding-").FullName;
    private readonly SaveEncoder encoder = new(Encoding.UTF8.GetBytes("posture-test-key"), Prefix);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    private GameStorage Build(SaveEncoding encoding) => new(AppDataPaths.FromDirectory(root), encoding);

    private string ReadRaw(string fileName) => File.ReadAllText(Path.Combine(root, fileName));

    private static bool IsJson(string text)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [Fact]
    public void EveryPublicConstructor_RequiresThePosture()
    {
        ConstructorInfo[] ctors = typeof(GameStorage).GetConstructors();
        Assert.NotEmpty(ctors);
        Assert.All(ctors, ctor =>
        {
            ParameterInfo posture = Assert.Single(ctor.GetParameters(), p => p.ParameterType == typeof(SaveEncoding));
            Assert.False(posture.IsOptional);
        });
    }

    [Fact]
    public void Options_NoLongerCarryAnEncoder()
    {
        Assert.DoesNotContain(typeof(GameStorageOptions).GetProperties(), p => p.PropertyType == typeof(SaveEncoder));
    }

    [Fact]
    public void NullPosture_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new GameStorage(AppDataPaths.FromDirectory(root), null!));
        Assert.Throws<ArgumentNullException>(() => SaveEncoding.Encoded(null!));
    }

    [Fact]
    public void Plaintext_HasNoEncoder_AndEncoded_CarriesIt()
    {
        Assert.False(SaveEncoding.Plaintext.IsEncoded);
        Assert.Null(SaveEncoding.Plaintext.Encoder);

        SaveEncoding encoded = SaveEncoding.Encoded(encoder);
        Assert.True(encoded.IsEncoded);
        Assert.Same(encoder, encoded.Encoder);

        using GameStorage storage = Build(encoded);
        Assert.Same(encoded, storage.SaveEncoding);
        Assert.Same(encoder, storage.Encoder);
    }

    [Fact]
    public void Plaintext_WritesPlaintextByDefault()
    {
        using GameStorage storage = Build(SaveEncoding.Plaintext);
        storage.Save("save.json", new Progress { Level = 3 });
        storage.Flush();

        string raw = ReadRaw("save.json");
        Assert.True(IsJson(raw));
        Assert.False(encoder.IsEncoded(raw));
        Assert.Equal(3, storage.Load<Progress>("save.json").Level);
    }

    [Fact]
    public void Plaintext_PerCallEncodeTrue_Throws()
    {
        using GameStorage storage = Build(SaveEncoding.Plaintext);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => storage.Save("save.json", new Progress(), new SaveWriteOptions { Encode = true }));
        Assert.Contains(nameof(SaveEncoding.Plaintext), ex.Message);
    }

    [Fact]
    public void Encoded_WritesEncodedByDefault()
    {
        using GameStorage storage = Build(SaveEncoding.Encoded(encoder));
        storage.Save("save.json", new Progress { Level = 7 });
        storage.Flush();

        string raw = ReadRaw("save.json");
        Assert.StartsWith(Prefix + ":", raw);
        Assert.True(encoder.IsEncoded(raw));

        SaveLoadResult<Progress> loaded = storage.LoadWithOutcome<Progress>("save.json");
        Assert.Equal(SaveLoadOutcome.Loaded, loaded.Outcome);
        Assert.Equal(7, loaded.Value.Level);
    }

    [Fact]
    public void Encoded_PerCallEncodeFalse_StillWritesPlaintext()
    {
        using GameStorage storage = Build(SaveEncoding.Encoded(encoder));
        storage.Save("editable.json", new Progress { Level = 9 }, new SaveWriteOptions { Encode = false });
        storage.Flush();

        string raw = ReadRaw("editable.json");
        Assert.True(IsJson(raw));
        Assert.False(encoder.IsEncoded(raw));

        SaveLoadResult<Progress> loaded = storage.LoadWithOutcome<Progress>("editable.json");
        Assert.Equal(SaveLoadOutcome.LoadedLegacyPlaintext, loaded.Outcome);
        Assert.Equal(9, loaded.Value.Level);
    }

    [Fact]
    public void Settings_StayPlaintext_UnderEncoded()
    {
        using GameStorage storage = Build(SaveEncoding.Encoded(encoder));
        SettingsManager<Prefs> manager = storage.CreateSettingsManager<Prefs>();
        manager.Settings.Volume = 11;
        manager.Save();
        storage.Flush();

        string raw = ReadRaw(storage.Settings.SettingsFileName);
        Assert.True(IsJson(raw));
        Assert.False(encoder.IsEncoded(raw));
        Assert.Contains("\"Volume\": 11", raw);
        Assert.Equal(11, storage.Settings.LoadSettings<Prefs>().Volume);
    }

    [Fact]
    public void SettingsStorage_DirectWrite_StaysPlaintext_UnderEncoded()
    {
        using GameStorage storage = Build(SaveEncoding.Encoded(encoder));
        storage.Settings.SaveSettings(new Prefs { Volume = 2 });
        storage.Flush();

        string raw = ReadRaw(storage.Settings.SettingsFileName);
        Assert.True(IsJson(raw));
        Assert.False(encoder.IsEncoded(raw));
        Assert.Equal(2, storage.Settings.LoadSettings<Prefs>().Volume);
    }
}
