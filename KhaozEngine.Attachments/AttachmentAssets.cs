using System;
using System.IO;

namespace KhaozEngine.Attachments;

public interface IAttachmentAssetSource { ReadOnlyMemory<byte> Read(string key); }

/// <summary>Reads owned byte snapshots without following filesystem links.</summary>
public sealed class DirectoryAttachmentAssetSource : IAttachmentAssetSource
{
    readonly string root;

    public DirectoryAttachmentAssetSource(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
        try
        {
            RefuseLinks(this.root);
            if (!Directory.Exists(this.root))
                throw new IOException("The asset root is not a directory.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Attachment root '{root}': {error.Message}", nameof(root), error);
        }
    }

    public ReadOnlyMemory<byte> Read(string key)
    {
        string? keyError = AttachmentAssetKey.Error(key);
        if (keyError is not null)
            throw new InvalidDataException($"Attachment key '{key}' {keyError}.");
        string path = Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            RefuseLinks(path);
            return File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Attachment asset '{key}': {error.Message}", error);
        }
    }

    static void RefuseLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Filesystem link traversal is refused at '{current}'.");
        }
    }
}

internal static class AttachmentAssetKey
{
    internal static string? Error(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "must not be empty";
        if (!AttachmentText.IsWellFormedUtf16(key))
            return "must contain well-formed UTF-16";
        if (key[0] == '/' || key.Contains('\\') || key.Contains(':') || Path.IsPathRooted(key))
            return "must be a relative forward-slash path without a URI";
        foreach (char c in key)
            if (char.IsControl(c))
                return "must not contain control characters";
        foreach (string segment in key.Split('/'))
            if (segment.Length == 0 || segment is "." or "..")
                return "must not contain empty or dot segments";
        return null;
    }
}
