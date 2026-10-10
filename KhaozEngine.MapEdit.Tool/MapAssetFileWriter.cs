using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapEdit;

/// <summary>Writes native asset resources as content-addressed files under an explicit absolute asset root, the root a
/// document's resource references resolve against. A resource lands at <c>assets/&lt;sha256&gt;.&lt;extension&gt;</c>,
/// so equal bytes share one file and an existing file is never overwritten. Each returned
/// <see cref="MapAssetRef"/> carries that relative path, the lowercase SHA-256 digest and payload version 1.</summary>
public sealed class MapAssetFileWriter
{
    /// <summary>The directory below the asset root that holds every written resource.</summary>
    public const string ContentDirectory = "assets";

    /// <summary>The extension <see cref="WriteManifest"/> gives a manifest.</summary>
    public const string ManifestExtension = "json";

    static readonly JsonSerializerOptions ManifestOptions = CreateManifestOptions();

    readonly string _root;

    /// <summary>Creates a writer over <paramref name="assetRoot"/>, which must be an absolute path.</summary>
    public MapAssetFileWriter(string assetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        if (!Path.IsPathFullyQualified(assetRoot)) throw new ArgumentException("An absolute asset root is required.", nameof(assetRoot));
        _root = Path.GetFullPath(assetRoot);
    }

    /// <summary>The absolute asset root.</summary>
    public string AssetRoot => _root;

    /// <summary>Writes <paramref name="bytes"/> at their content address unless that file already holds them. The
    /// returned reference's <see cref="MapAssetRef.Id"/> is the digest, so a caller that keeps a stable resource ID
    /// rebinds it with a <c>with</c> expression. <paramref name="extension"/> is 1 to 16 lowercase ASCII letters or
    /// digits without a dot. Throws <see cref="MapDocumentException"/> when the address holds other bytes or the file
    /// cannot be written. A failed write leaves no partial file at the address.</summary>
    public MapAssetRef WriteResource(byte[] bytes, string extension)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        MapAssetRef reference = Address(bytes, extension);
        string path = Path.GetFullPath(reference.Path, _root);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) Publish(bytes, path);
            string existing = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (!StringComparer.Ordinal.Equals(existing, reference.Sha256))
                throw new MapDocumentException(
                    $"'{path}' already holds other content. A content-addressed resource is never overwritten.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MapDocumentException($"Cannot write native resource '{path}': {ex.Message}", ex);
        }
        return reference;
    }

    /// <summary>Serializes <paramref name="manifest"/> as payload 1 JSON, proves it reads back under the strict manifest
    /// reader, and writes it like <see cref="WriteResource"/> with the <see cref="ManifestExtension"/> extension. The
    /// returned reference carries <paramref name="id"/>, ready to stand as a document root.</summary>
    public MapAssetRef WriteManifest(MapAssetManifestDoc manifest, string id)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return WriteResource(Serialize(manifest, id), ManifestExtension) with { Id = id };
    }

    /// <summary>The reference <see cref="WriteResource"/> returns for <paramref name="bytes"/>, without writing.</summary>
    internal static MapAssetRef Address(byte[] bytes, string extension)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (extension.Length > 16)
            throw new ArgumentException("A resource extension has at most 16 characters.", nameof(extension));
        foreach (char c in extension)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9'))
                throw new ArgumentException("A resource extension is lowercase ASCII letters and digits only.", nameof(extension));
        string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new MapAssetRef(digest, $"{ContentDirectory}/{digest}.{extension}", digest, 1);
    }

    /// <summary>The manifest bytes <see cref="WriteManifest"/> writes, after a strict read-back under
    /// <paramref name="id"/>.</summary>
    internal static byte[] Serialize(MapAssetManifestDoc manifest, string id)
    {
        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestOptions);
        }
        catch (JsonException ex)
        {
            throw new MapDocumentException($"Native manifest '{id}' cannot be serialized: {ex.Message}", ex);
        }
        MapAssetManifestReader.Read(bytes, id);
        return bytes;
    }

    // Stage beside the target and promote with a rename that never replaces an existing file. A concurrent writer of the
    // same address may win the rename. The caller then checks whatever holds the address.
    static void Publish(byte[] bytes, string path)
    {
        string staging = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(staging, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    static JsonSerializerOptions CreateManifestOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        MapNativeJson.Configure(options);
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}
