using System;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using KhaozEngine.ShaderBuild;
using Xunit;

namespace KhaozEngine.Tests.ShaderBuild;

public sealed class TarGzExtractorTests : IDisposable
{
    private const UnixFileMode Executable = (UnixFileMode)0x1ED;
    private static readonly string LongPath =
        "lib/slang-standard-module-2026.23/slang/numerics/" + new string('m', 60) + "/__builtin_differentiable.slang-module";

    private readonly string _work = Path.Combine(Path.GetTempPath(), "khaozengine-tar-tests", Guid.NewGuid().ToString("N"));

    private string Destination => Path.Combine(_work, "out");

    public void Dispose()
    {
        if (Directory.Exists(_work))
            Directory.Delete(_work, recursive: true);
    }

    [Fact]
    public void Extracts_the_entry_kinds_the_pinned_releases_use()
    {
        var archive = Archive((tar, raw) =>
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
            WriteBinaryPaxHeader(raw, "SCHILY.xattr.com.apple.cs.CodeSignature", [0x01, (byte)'\n', 0x00, (byte)'s', (byte)'\n']);
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "./bin/slangc") { Mode = Executable, DataStream = Text("slangc") });
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, LongPath) { DataStream = Text("module") });
            if (!OperatingSystem.IsWindows())
                tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "./lib/slangc") { LinkName = "../bin/slangc" });
        });

        Extract(archive);

        Assert.Equal("slangc", File.ReadAllText(Path.Combine(Destination, "bin", "slangc")));
        Assert.Equal("module", File.ReadAllText(Path.Combine(Destination, LongPath)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(Executable, File.GetUnixFileMode(Path.Combine(Destination, "bin", "slangc")));
            Assert.Equal("slangc", File.ReadAllText(Path.Combine(Destination, "lib", "slangc")));
        }
    }

    [Theory]
    [InlineData("parent-path")]
    [InlineData("rooted-path")]
    [InlineData("escaping-symlink")]
    [InlineData("hard-link")]
    public void Refuses_an_entry_it_cannot_place_inside_the_destination(string entry)
    {
        var archive = Archive((tar, _) => tar.WriteEntry(entry switch
        {
            "parent-path" => new PaxTarEntry(TarEntryType.RegularFile, "../escape") { DataStream = Text("x") },
            "rooted-path" => new PaxTarEntry(TarEntryType.RegularFile, "/escape") { DataStream = Text("x") },
            "escaping-symlink" => new PaxTarEntry(TarEntryType.SymbolicLink, "lib/escape") { LinkName = "../../escape" },
            _ => new PaxTarEntry(TarEntryType.HardLink, "bin/escape") { LinkName = "../escape" },
        }));

        Assert.Throws<InvalidDataException>(() => Extract(archive));
        Assert.Equal([Destination], Directory.GetFileSystemEntries(_work));
        Assert.False(File.Exists(Path.Combine(Path.GetPathRoot(_work)!, "escape")));
    }

    private void Extract(byte[] archive)
    {
        using var stream = new MemoryStream(archive);
        TarGzExtractor.Extract(stream, Destination);
    }

    private static MemoryStream Text(string text) => new(Encoding.UTF8.GetBytes(text));

    private static byte[] Archive(Action<TarWriter, Stream> write)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            write(tar, gzip);
        return buffer.ToArray();
    }

    // libarchive writes xattr values as raw bytes in a length-prefixed PAX record, which TarWriter refuses to produce.
    private static void WriteBinaryPaxHeader(Stream raw, string key, byte[] value)
    {
        var body = Encoding.ASCII.GetBytes($" {key}=").Concat(value).Append((byte)'\n').ToArray();
        var length = body.Length + 1;
        while ((length.ToString(CultureInfo.InvariantCulture).Length + body.Length) != length)
            length++;
        var record = Encoding.ASCII.GetBytes(length.ToString(CultureInfo.InvariantCulture)).Concat(body).ToArray();

        var header = new byte[512];
        Encoding.ASCII.GetBytes("./bin/PaxHeader/slangc").CopyTo(header, 0);
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);
        Encoding.ASCII.GetBytes(Convert.ToString(record.Length, 8).PadLeft(11, '0') + "\0").CopyTo(header, 124);
        header[156] = (byte)'x';
        Encoding.ASCII.GetBytes("ustar\000").CopyTo(header, 257);
        Encoding.ASCII.GetBytes("        ").CopyTo(header, 148);
        Encoding.ASCII.GetBytes(Convert.ToString(header.Sum(b => b), 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);

        raw.Write(header);
        raw.Write(record);
        raw.Write(new byte[(512 - record.Length % 512) % 512]);
    }
}
