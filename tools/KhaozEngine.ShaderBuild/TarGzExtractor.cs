using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace KhaozEngine.ShaderBuild;

// System.Formats.Tar splits PAX records on newline instead of honouring their length prefix, so it rejects
// the binary SCHILY.xattr code-signature values in the macOS slang release.
/// <summary>Extracts a .tar.gz of regular files, directories and symlinks, refusing any entry that leaves the destination.</summary>
internal static class TarGzExtractor
{
    private const int BlockSize = 512;
    private const UnixFileMode PermissionBits = (UnixFileMode)0x1FF;

    public static void Extract(Stream compressed, string destination)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var tar = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        var header = new byte[BlockSize];
        string? paxPath = null;
        string? paxLinkPath = null;
        long? paxSize = null;
        while (true)
        {
            tar.ReadExactly(header);
            if (header.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                return;
            if (Encoding.ASCII.GetString(header, 257, 5) != "ustar")
                throw new InvalidDataException("The archive holds a header that is not ustar.");

            var type = (char)header[156];
            var size = paxSize ?? ReadOctal(header, 124, 12);
            var name = paxPath ?? UstarName(header);
            var linkName = paxLinkPath ?? Field(header, 157, 100);
            paxPath = paxLinkPath = null;
            paxSize = null;

            switch (type)
            {
                case 'x':
                    ReadPaxRecords(ReadData(tar, size), out paxPath, out paxLinkPath, out paxSize);
                    continue;
                case 'g':
                    Skip(tar, size);
                    continue;
                case '5':
                    Directory.CreateDirectory(Contained(root, name));
                    Skip(tar, size);
                    continue;
                case '0' or '\0':
                    WriteFile(tar, Contained(root, name), size, (UnixFileMode)ReadOctal(header, 100, 8) & PermissionBits);
                    continue;
                case '2':
                    WriteSymlink(root, Contained(root, name), linkName);
                    Skip(tar, size);
                    continue;
                default:
                    throw new InvalidDataException($"The archive entry {name} has unsupported tar type '{type}'.");
            }
        }
    }

    private static void WriteFile(Stream tar, string path, long size, UnixFileMode mode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                for (var left = size; left > 0;)
                {
                    var chunk = (int)Math.Min(buffer.Length, left);
                    tar.ReadExactly(buffer, 0, chunk);
                    file.Write(buffer, 0, chunk);
                    left -= chunk;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        SkipPadding(tar, size);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }

    private static void WriteSymlink(string root, string path, string target)
    {
        if (target.Length == 0 || Path.IsPathRooted(target))
            throw new InvalidDataException($"The archive symlink {path} has target '{target}', which is not relative.");
        Contained(root, Path.GetRelativePath(root, Path.Combine(Path.GetDirectoryName(path)!, target)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, target);
    }

    private static string Contained(string root, string entryName)
    {
        var relative = entryName.Replace('\\', '/');
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !(full + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException($"The archive entry {entryName} lies outside the extraction folder.");
        return full;
    }

    // PAX records are "<length> <key>=<value>\n" where length counts the whole record, so values may hold any byte.
    private static void ReadPaxRecords(byte[] data, out string? path, out string? linkPath, out long? size)
    {
        path = linkPath = null;
        size = null;
        for (var offset = 0; offset < data.Length;)
        {
            var space = Array.IndexOf(data, (byte)' ', offset);
            if (space < 0 || !int.TryParse(Encoding.ASCII.GetString(data, offset, space - offset), out var length)
                || length <= space - offset || offset + length > data.Length || data[offset + length - 1] != '\n')
                throw new InvalidDataException("The archive holds a malformed PAX record.");
            var record = data.AsSpan(space + 1, offset + length - space - 2);
            var equals = record.IndexOf((byte)'=');
            if (equals < 0)
                throw new InvalidDataException("The archive holds a PAX record without a key.");
            var key = Encoding.UTF8.GetString(record[..equals]);
            var value = record[(equals + 1)..];
            switch (key)
            {
                case "path":
                    path = Encoding.UTF8.GetString(value);
                    break;
                case "linkpath":
                    linkPath = Encoding.UTF8.GetString(value);
                    break;
                case "size":
                    size = long.Parse(Encoding.ASCII.GetString(value));
                    break;
            }
            offset += length;
        }
    }

    private static string UstarName(byte[] header)
    {
        var name = Field(header, 0, 100);
        var prefix = Field(header, 345, 155);
        return prefix.Length == 0 ? name : prefix + "/" + name;
    }

    private static string Field(byte[] header, int offset, int length)
    {
        var field = header.AsSpan(offset, length);
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }

    private static long ReadOctal(byte[] header, int offset, int length)
    {
        var text = Encoding.ASCII.GetString(header, offset, length).Trim('\0', ' ');
        try
        {
            return text.Length == 0 ? 0 : Convert.ToInt64(text, 8);
        }
        catch (FormatException problem)
        {
            throw new InvalidDataException($"The archive holds a numeric header field '{text}' that is not octal.", problem);
        }
    }

    private static byte[] ReadData(Stream tar, long size)
    {
        var data = new byte[size];
        tar.ReadExactly(data);
        SkipPadding(tar, size);
        return data;
    }

    private static void Skip(Stream tar, long size)
    {
        var buffer = new byte[BlockSize];
        for (var left = size + Padding(size); left > 0; left -= BlockSize)
            tar.ReadExactly(buffer, 0, (int)Math.Min(BlockSize, left));
    }

    private static void SkipPadding(Stream tar, long size)
    {
        var padding = Padding(size);
        if (padding > 0)
            tar.ReadExactly(new byte[padding]);
    }

    private static int Padding(long size) => (int)((BlockSize - size % BlockSize) % BlockSize);
}
