using System.IO;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

public sealed class ManifestToolCommandsTests
{
    static (StringWriter outw, StringWriter errw) Writers() => (new StringWriter(), new StringWriter());

    [Fact]
    public void GenKey_then_sign_then_verify_roundtrips()
    {
        string dir = Directory.CreateTempSubdirectory("ke-updater-rt").FullName;
        try
        {
            var (o, e) = Writers();
            Assert.Equal(0, ManifestToolCommands.Run(new[] { "genkey", "--out", dir }, o, e));
            string priv = Path.Combine(dir, "private.pem");
            string pub = Path.Combine(dir, "public.pem");
            Assert.True(File.Exists(priv));
            Assert.True(File.Exists(pub));

            string manifest = Path.Combine(dir, "manifest.json");
            File.WriteAllText(manifest, "{\"version\":\"1.0.0\"}");
            Assert.Equal(0, ManifestToolCommands.Run(new[] { "sign", "--manifest", manifest, "--key", priv }, o, e));
            Assert.True(File.Exists(manifest + ".sig"));

            Assert.Equal(0, ManifestToolCommands.Run(
                new[] { "verify", "--manifest", manifest, "--sig", manifest + ".sig", "--key", pub }, o, e));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Verify_fails_on_tampered_manifest()
    {
        string dir = Directory.CreateTempSubdirectory("ke-updater-tamper").FullName;
        try
        {
            var (o, e) = Writers();
            ManifestToolCommands.Run(new[] { "genkey", "--out", dir }, o, e);
            string priv = Path.Combine(dir, "private.pem");
            string pub = Path.Combine(dir, "public.pem");
            string manifest = Path.Combine(dir, "manifest.json");
            File.WriteAllText(manifest, "{\"version\":\"1.0.0\"}");
            ManifestToolCommands.Run(new[] { "sign", "--manifest", manifest, "--key", priv }, o, e);
            File.WriteAllText(manifest, "{\"version\":\"6.6.6\"}"); // tamper after signing
            Assert.Equal(2, ManifestToolCommands.Run(
                new[] { "verify", "--manifest", manifest, "--sig", manifest + ".sig", "--key", pub }, o, e));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Manifest_writes_json_to_stdout()
    {
        string dir = Directory.CreateTempSubdirectory("ke-updater-man").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
            var (o, e) = Writers();
            Assert.Equal(0, ManifestToolCommands.Run(
                new[] { "manifest", "--dir", dir, "--platform", "win-x64", "--version", "1.2.3" }, o, e));
            string json = o.ToString();
            Assert.Contains("\"version\"", json);
            Assert.Contains("1.2.3", json);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Manifest_WithRequiredFlag_SetsRequiredTrue()
    {
        string dir = Directory.CreateTempSubdirectory("ke-updater-req").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
            var (o, e) = Writers();
            Assert.Equal(0, ManifestToolCommands.Run(
                new[] { "manifest", "--dir", dir, "--platform", "win-x64", "--version", "1.2.3", "--required" }, o, e));
            UpdateManifest? parsed = UpdateManifest.Deserialize(o.ToString());
            Assert.NotNull(parsed);
            Assert.True(parsed!.Required);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Manifest_WithoutRequiredFlag_RequiredFalse()
    {
        string dir = Directory.CreateTempSubdirectory("ke-updater-noreq").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
            var (o, e) = Writers();
            Assert.Equal(0, ManifestToolCommands.Run(
                new[] { "manifest", "--dir", dir, "--platform", "win-x64", "--version", "1.2.3" }, o, e));
            UpdateManifest? parsed = UpdateManifest.Deserialize(o.ToString());
            Assert.NotNull(parsed);
            Assert.False(parsed!.Required);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unknown_command_returns_nonzero()
    {
        var (o, e) = Writers();
        Assert.Equal(1, ManifestToolCommands.Run(new[] { "bogus" }, o, e));
    }

    [Fact]
    public void No_args_returns_nonzero_and_prints_usage()
    {
        var (o, e) = Writers();
        Assert.Equal(1, ManifestToolCommands.Run(System.Array.Empty<string>(), o, e));
        Assert.Contains("Usage", e.ToString());
    }
}
